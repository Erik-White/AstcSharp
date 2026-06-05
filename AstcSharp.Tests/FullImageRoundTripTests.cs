using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Tests;

/// <summary>
/// Full-image encode round-trip tests over real multi-block content: each ASTC fixture is decoded
/// to RGBA8, re-encoded by <see cref="AstcEncoder"/>, and decoded again; the re-encoded image must
/// stay above a PSNR floor and contain no error-colour (magenta) blocks. Unlike the synthetic
/// single-/2x2-block encoder tests, this exercises the encoder on full-size natural content
/// (thousands of varied blocks), stressing per-block mode/partition selection and the multi-block
/// output layout end-to-end.
/// </summary>
public class FullImageRoundTripTests
{
    // The lowest observed re-encode PSNR across these fixtures is ~34 dB (rgba-6x6); 30 dB leaves
    // a comfortable margin so the test guards against a real encoder regression without flaking.
    private const double MinPsnr = 30.0;

    // A representative subset spanning the footprint range and both opaque/alpha content: the
    // smallest and largest RGB footprints and the smallest and largest RGBA footprints (the latter
    // is also the lowest-PSNR case). The encoder runs a full per-block search, so the full fixture
    // set is too slow for routine runs.
    [Theory]
    [InlineData(TestData.Astc.Rgb_4x4)]
    [InlineData(TestData.Astc.Rgb_12x12)]
    [InlineData(TestData.Astc.Rgba_4x4)]
    [InlineData(TestData.Astc.Rgba_8x8)]
    public void DecodeReencodeDecode_RealImage_StaysAbovePsnrFloor(string inputFile)
    {
        string filePath = TestFile.GetInputFileFullPath(Path.Combine("Astc", inputFile));
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));
        Footprint footprint = file.Footprint;

        // Decode the fixture to RGBA8 — this is the real-world source image the encoder must handle.
        byte[] source = StreamCodec.DecodeLdr(file.Blocks, file.Width, file.Height, footprint);

        byte[] reencoded = StreamCodec.Encode(source, file.Width, file.Height, footprint);
        byte[] roundTripped = StreamCodec.DecodeLdr(reencoded, file.Width, file.Height, footprint);

        AssertNoIntroducedMagenta(source, roundTripped);
        double psnr = Psnr(source, roundTripped);
        psnr.Should().BeGreaterThanOrEqualTo(
            MinPsnr,
            because: $"{inputFile} ({footprint.Type}) re-encode PSNR {psnr:F2} dB should stay above {MinPsnr} dB");
    }

    private static double Psnr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded)
    {
        decoded.Length.Should().Be(original.Length);

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

    /// <summary>
    /// Fails if the round-tripped image has an error-colour (magenta) texel that the source did not,
    /// i.e. an illegal block the encoder produced. Comparing against the source avoids false
    /// positives on real content that happens to contain genuinely magenta pixels.
    /// </summary>
    private static void AssertNoIntroducedMagenta(ReadOnlySpan<byte> source, ReadOnlySpan<byte> decoded)
    {
        for (int i = 0; i < decoded.Length; i += BlockInfo.ChannelsPerPixel)
        {
            bool decodedMagenta = decoded[i] == 255 && decoded[i + 1] == 0 && decoded[i + 2] == 255 && decoded[i + 3] == 255;
            bool sourceMagenta = source[i] == 255 && source[i + 1] == 0 && source[i + 2] == 255 && source[i + 3] == 255;
            (decodedMagenta && !sourceMagenta).Should().BeFalse(
                because: $"pixel {i / BlockInfo.ChannelsPerPixel} is error-colour (magenta) but the source is not; a block was encoded illegally");
        }
    }
}
