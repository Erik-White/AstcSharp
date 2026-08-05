using AstcEncoder;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Benchmarks;

/// <summary>
/// Shared quality measurement for <see cref="LdrQualityBenchmark"/>: loads the LDR fixture
/// image, re-encodes it with both AstcSharp and the ARM reference encoder, decodes each back through
/// the ARM decoder, and reports the PSNR (dB) of each against the source.
/// </summary>
public static class LdrQuality
{
    private const string Fixture = "rgb-4x4";

    /// <summary>
    /// The loaded source image: its RGBA8 pixels and dimensions.
    /// </summary>
    public readonly record struct Image(byte[] Pixels, int Width, int Height);

    /// <summary>
    /// Measures both codecs' PSNR against the source for <paramref name="footprintType"/>. Encodes the
    /// image with AstcSharp and ARM, decodes each through ARM's decoder, and compares to the RGBA8 source.
    /// </summary>
    public static QualityResult Measure(FootprintType footprintType)
    {
        Image image = LoadFixture();
        Footprint footprint = Footprint.FromFootprintType(footprintType);

        byte[] ourBlocks = StreamCodec.Encode(image.Pixels, image.Width, image.Height, footprint);
        byte[] ourDecoded = DecompressLdr(ourBlocks, image.Width, image.Height, footprint);

        byte[] armBlocks = CompressLdr(image, footprint);
        byte[] armDecoded = DecompressLdr(armBlocks, image.Width, image.Height, footprint);

        return new QualityResult(Psnr(image.Pixels, ourDecoded), Psnr(image.Pixels, armDecoded));
    }

    /// <summary>
    /// Loads the image fixture the benchmark's timed encode runs on — the same source
    /// <see cref="Measure"/> uses, so the timed encode and the quality columns describe one encode.
    /// </summary>
    public static Image LoadFixture()
    {
        string path = BenchmarkTestDataLocator.FindTestData(Path.Combine("Astc", Fixture + ".astc"));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(path));
        byte[] pixels = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, file.Footprint);
        return new Image(pixels, file.Width, file.Height);
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

    private static byte[] CompressLdr(Image image, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreMedium, flags: 0);
        try
        {
            var armImage = new AstcencImage { dimX = (uint)image.Width, dimY = (uint)image.Height, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = image.Pixels };
            int blocksWide = (image.Width + footprint.Width - 1) / footprint.Width;
            int blocksHigh = (image.Height + footprint.Height - 1) / footprint.Height;
            byte[] blocks = new byte[blocksWide * blocksHigh * BlockInfo.SizeInBytes];
            ArmCodec.ThrowOnError(Astcenc.AstcencCompressImage(context, ref armImage, ArmCodec.IdentitySwizzle, blocks, 0), "CompressImage(LDR)");

            return blocks;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }

    private static byte[] DecompressLdr(byte[] blocks, int width, int height, Footprint footprint)
    {
        AstcencContext context = ArmCodec.CreateContext(footprint, AstcencProfile.AstcencPrfLdr, Astcenc.AstcencPreFastest, AstcencFlags.DecompressOnly);
        try
        {
            byte[] output = new byte[width * height * BlockInfo.ChannelsPerPixel];
            var image = new AstcencImage { dimX = (uint)width, dimY = (uint)height, dimZ = 1, dataType = AstcencType.AstcencTypeU8, data = output };
            ArmCodec.ThrowOnError(Astcenc.AstcencDecompressImage(context, blocks, ref image, ArmCodec.IdentitySwizzle, 0), "DecompressImage(LDR)");

            return output;
        }
        finally
        {
            Astcenc.AstcencContextFree(context);
        }
    }
}
