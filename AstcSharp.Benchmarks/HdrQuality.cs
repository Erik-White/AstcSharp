using System.Runtime.InteropServices;
using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Shared quality measurement for <see cref="HdrQualityBenchmark"/>: loads the real HDR fixture crop,
/// re-encodes it with both AstcSharp and the ARM reference encoder, decodes each back through the ARM
/// decoder, and reports the log-PSNR of each against the source.
/// </summary>
public static class HdrQuality
{
    private const string Fixture = "HdrPipeline/mixed-256-4x4";
    public const int CropSize = 64;

    // The FP16 bit pattern of the largest finite Half (65504), used as the PSNR peak — the fixed
    // reference the reconstruction error is measured against.
    private const double Fp16Peak = 0x7BFF;

    /// <summary>
    /// Measures both codecs' log-PSNR against the source for <paramref name="footprintType"/>. Encodes
    /// with AstcSharp and ARM, decodes each through ARM's decoder, and compares to the FP16 source.
    /// </summary>
    public static QualityResult Measure(FootprintType footprintType)
    {
        Half[] source = LoadFixtureCrop();
        Footprint footprint = Footprint.FromFootprintType(footprintType);

        byte[] ourBlocks = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        Half[] ourDecoded = DecompressHdr(ourBlocks, footprint);

        byte[] armBlocks = CompressHdr(source, footprint);
        Half[] armDecoded = DecompressHdr(armBlocks, footprint);

        return new QualityResult(LogPsnr(source, ourDecoded), LogPsnr(source, armDecoded));
    }

    /// <summary>
    /// Loads the fixture crop the benchmark's timed encode runs on — the same source
    /// <see cref="Measure"/> uses, so the timed encode and the quality columns describe one encode.
    /// </summary>
    public static Half[] LoadFixtureCropForFootprint() => LoadFixtureCrop();

    private static Half[] LoadFixtureCrop()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", Fixture + ".astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        Half[] full = StreamCodec.DecodeHdrHalf(file.Blocks, file.Width, file.Height, file.Footprint);
        return CropTopLeft(full, file.Width, CropSize, CropSize);
    }

    private static double LogPsnr(ReadOnlySpan<Half> original, ReadOnlySpan<Half> decoded)
    {
        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            double diff = BitConverter.HalfToUInt16Bits(decoded[i]) - BitConverter.HalfToUInt16Bits(original[i]);
            sumSquaredError += diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / original.Length;

        return 10.0 * Math.Log10((Fp16Peak * Fp16Peak) / meanSquaredError);
    }

    private static Half[] CropTopLeft(ReadOnlySpan<Half> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        int channels = BlockInfo.ChannelsPerPixel;
        var cropped = new Half[cropWidth * cropHeight * channels];
        for (int y = 0; y < cropHeight; y++)
        {
            ReadOnlySpan<Half> sourceRow = rgba.Slice(y * sourceWidth * channels, cropWidth * channels);
            sourceRow.CopyTo(cropped.AsSpan(y * cropWidth * channels, cropWidth * channels));
        }

        return cropped;
    }

    private static byte[] CompressHdr(Half[] pixels, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfHdr, Astcenc.AstcencPreMedium, flags: 0);
        try
        {
            byte[] pixelBytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
            var image = new AstcencImage { dimX = CropSize, dimY = CropSize, dimZ = 1, dataType = AstcencType.AstcencTypeF16, data = pixelBytes };
            int blocksWide = (CropSize + footprint.Width - 1) / footprint.Width;
            int blocksHigh = (CropSize + footprint.Height - 1) / footprint.Height;
            byte[] blocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];
            ArmCodec.ThrowOnError(Astcenc.AstcencCompressImage(context, ref image, ArmCodec.IdentitySwizzle, blocks, 0), "CompressImage(HDR)");

            return blocks;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }

    private static Half[] DecompressHdr(byte[] blocks, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfHdr, Astcenc.AstcencPreFastest, AstcencFlags.DecompressOnly);
        try
        {
            var outputHalves = new Half[CropSize * CropSize * BlockInfo.ChannelsPerPixel];
            byte[] outputBytes = new byte[MemoryMarshal.AsBytes(outputHalves.AsSpan()).Length];
            var image = new AstcencImage { dimX = CropSize, dimY = CropSize, dimZ = 1, dataType = AstcencType.AstcencTypeF16, data = outputBytes };
            ArmCodec.ThrowOnError(Astcenc.AstcencDecompressImage(context, blocks, ref image, ArmCodec.IdentitySwizzle, 0), "DecompressImage(HDR)");

            outputBytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(outputHalves.AsSpan()));
            return outputHalves;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }
}
