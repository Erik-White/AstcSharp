using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Shared quality measurement for <see cref="LdrQualityBenchmark"/>: loads a real LDR fixture crop,
/// re-encodes it with both AstcSharp and the ARM reference encoder, decodes each back through the ARM
/// decoder, and reports the PSNR (dB) of each against the source.
/// </summary>
public static class LdrQuality
{
    private const string Fixture = "rgb-4x4";
    public const int CropSize = 64;

    /// <summary>
    /// Measures both codecs' PSNR against the source for <paramref name="footprintType"/>. Encodes with
    /// AstcSharp and ARM, decodes each through ARM's decoder, and compares to the RGBA8 source.
    /// </summary>
    public static QualityResult Measure(FootprintType footprintType)
    {
        byte[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(footprintType);

        byte[] ourBlocks = StreamCodec.Encode(source, CropSize, CropSize, footprint);
        byte[] ourDecoded = DecompressLdr(ourBlocks, footprint);

        byte[] armBlocks = CompressLdr(source, footprint);
        byte[] armDecoded = DecompressLdr(armBlocks, footprint);

        return new QualityResult(Psnr(source, ourDecoded), Psnr(source, armDecoded));
    }

    /// <summary>
    /// Loads the fixture crop the benchmark's timed encode runs on — the same source
    /// <see cref="Measure"/> uses, so the timed encode and the quality columns describe one encode.
    /// </summary>
    public static byte[] LoadFixtureCrop()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", Fixture + ".astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        byte[] full = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);

        return ImageHelper.CropTopLeft(full, file.Width, CropSize, CropSize);
    }

    private static double Psnr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded)
    {
        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            int diff = decoded[i] - original[i];
            sumSquaredError += (double)diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / original.Length;
        return 10.0 * Math.Log10((255.0 * 255.0) / meanSquaredError);
    }

    private static byte[] CompressLdr(byte[] pixels, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreMedium, flags: 0);
        try
        {
            var image = new AstcencImage { dimX = CropSize, dimY = CropSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = pixels };
            int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
            int blocksHigh = (CropSize + footprint.Height - 1) / footprint.Height;
            byte[] blocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];
            ArmCodec.ThrowOnError(Astcenc.AstcencCompressImage(context, ref image, ArmCodec.IdentitySwizzle, blocks, 0), "CompressImage(LDR)");

            return blocks;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }

    private static byte[] DecompressLdr(byte[] blocks, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreFastest, AstcencFlags.DecompressOnly);
        try
        {
            byte[] output = new byte[CropSize * CropSize * BlockInfo.ChannelsPerPixel];
            var image = new AstcencImage { dimX = CropSize, dimY = CropSize, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = output };
            ArmCodec.ThrowOnError(Astcenc.AstcencDecompressImage(context, blocks, ref image, ArmCodec.IdentitySwizzle, 0), "DecompressImage(LDR)");

            return output;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }
}
