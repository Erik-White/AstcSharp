using System.Buffers.Binary;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Reference.Tests.Utils;
using AstcSharp.Tests.Utils;
using AwesomeAssertions;
using Xunit.Abstractions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// Quality validation of the HDR encoder on real (non-synthetic) content: a real mixed LDR/HDR
/// fixture is decoded to FP16, a crop is re-encoded with both our encoder and ARM's, and the
/// reconstructions are compared. Unlike the synthetic-archetype quality test, this measures the
/// encoder against the mix of block content a real image contains.
/// </summary>
public class HdrRealImageQualityTests
{
    // A 64×64 crop of the 256×256 fixture: enough real block-content variety to be representative
    // while keeping the per-block search (≈1 ms/block) to a few hundred blocks per footprint.
    private const int CropSize = 64;

    // The real mixed LDR/HDR fixture (256×256). Decoded to FP16 once per test.
    private const string Fixture = "mixed-256-4x4";

    // Absolute log-PSNR floor our encoder clears on every footprint for this real fixture.This is
    // a regression guard, this floor keeps the encoder from silently regressing below its known level.
    private const double MinLogPsnrDb = 48.0;

    private readonly ITestOutputHelper output;

    public HdrRealImageQualityTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [MemberData(nameof(Footprints))]
    public void ReencodedRealHdrImage_AchievesQualityFloor(FootprintType footprintType)
    {
        // Re-encode a real mixed LDR/HDR crop and require our own decode to reconstruct it above a
        // known log-PSNR floor. This guards against quality regressions on real content.
        // The ARM comparison lives in ProfileRealHdrImageQualityAndModeUsage.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        Half[] source = LoadFixtureCrop();

        double ourPsnr = OurRoundTripPsnr(source, footprint);

        ourPsnr.Should().BeGreaterThanOrEqualTo(
            MinLogPsnrDb,
            because: $"[{footprintType}] our log-PSNR {ourPsnr:F2} dB should stay above the {MinLogPsnrDb} dB floor on real content");
    }

    /// <summary>
    /// Diagnostic harness (not an assertion): reports, per footprint, our vs. ARM log-PSNR and the gap
    /// on the real fixture crop, plus the per-block endpoint-mode / layout histogram of our encode.
    /// Run explicitly to see where the encoder loses quality and which modes it actually uses.
    /// </summary>
    [Fact(Skip = "Diagnostic harness, not an assertion test. Run explicitly and read its output to " +
        "profile HDR encode quality and mode usage on real content.")]
    public void ProfileRealHdrImageQualityAndModeUsage()
    {
        Half[] source = LoadFixtureCrop();
        this.output.WriteLine($"fixture={Fixture} crop={CropSize}x{CropSize}");
        this.output.WriteLine("  footprint      ourPSNR   armPSNR    gap   modeUsage");

        foreach (FootprintType footprintType in ProfiledFootprints)
        {
            var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
            Footprint footprint = Footprint.FromFootprintType(footprintType);

            byte[] ourEncoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
            double ourPsnr = LogPsnr(source, StreamCodec.DecodeHdr(ourEncoded, CropSize, CropSize, footprint));
            double armPsnr = ArmRoundTripPsnr(source, blockX, blockY);
            string usage = SummariseModeUsage(ourEncoded);

            this.output.WriteLine($"  {footprintType,-14} {ourPsnr,7:F2}   {armPsnr,7:F2}   {ourPsnr - armPsnr,5:F2}   {usage}");
        }
    }

    private static readonly FootprintType[] ProfiledFootprints =
    [
        FootprintType.Footprint4x4, FootprintType.Footprint6x6, FootprintType.Footprint8x8,
        FootprintType.Footprint10x10, FootprintType.Footprint12x12,
    ];

    public static TheoryData<FootprintType> Footprints
    {
        get
        {
            var data = new TheoryData<FootprintType>();
            foreach (FootprintType footprint in ProfiledFootprints)
            {
                data.Add(footprint);
            }

            return data;
        }
    }

    private static Half[] LoadFixtureCrop()
    {
        string filePath = Path.Combine("TestData", "Input", "Astc", "HdrPipeline", Fixture + ".astc");
        AstcFile file = AstcFile.FromMemory(File.ReadAllBytes(filePath));
        Half[] full = StreamCodec.DecodeHdrHalf(file.Blocks, file.Width, file.Height, file.Footprint);
        return CropTopLeft(full, file.Width, CropSize, CropSize);
    }

    private static double OurRoundTripPsnr(Half[] source, Footprint footprint)
    {
        byte[] encoded = StreamCodec.EncodeHdr(source, CropSize, CropSize, footprint);
        return LogPsnr(source, StreamCodec.DecodeHdr(encoded, CropSize, CropSize, footprint));
    }

    private static double ArmRoundTripPsnr(Half[] source, int blockX, int blockY)
    {
        byte[] armEncoded = ReferenceDecoder.CompressHdr(source, CropSize, CropSize, blockX, blockY);
        Half[] armDecoded = ReferenceDecoder.DecompressHdr(armEncoded, CropSize, CropSize, blockX, blockY);
        return LogPsnr(source, HalvesToFloats(armDecoded));
    }

    /// <summary>
    /// Per-block histogram of the endpoint mode and layout our encoder chose across the encoded image,
    /// e.g. <c>"RgbDirect×180 dual×40 multi×36"</c> — shows which modes/layouts real content elicits.
    /// </summary>
    private static string SummariseModeUsage(byte[] encoded)
    {
        var modeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int blockCount = encoded.Length / BlockInfo.SizeInBytes;
        for (int i = 0; i < blockCount; i++)
        {
            UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(i * BlockInfo.SizeInBytes, BlockInfo.SizeInBytes));
            BlockInfo info = BlockModeDecoder.Decode(bits);
            string key = info.IsVoidExtent ? "void"
                : info.PartitionCount > 1 ? $"multi{info.PartitionCount}"
                : info.DualPlane.Enabled ? $"dual:{info.EndpointMode0}"
                : info.EndpointMode0.ToString();
            modeCounts[key] = modeCounts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        return string.Join(" ", modeCounts.Select(kv => $"{kv.Key}×{kv.Value}"));
    }

    /// <summary>
    /// Log-space PSNR on the FP16 bit patterns (the domain HDR error is perceived in), comparable
    /// between the two encoders. Peak is the largest finite FP16 pattern.
    /// </summary>
    private static double LogPsnr(ReadOnlySpan<Half> original, ReadOnlySpan<float> decoded)
    {
        const double peak = 0x7BFF;
        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            double o = BitConverter.HalfToUInt16Bits(original[i]);
            double d = BitConverter.HalfToUInt16Bits((Half)decoded[i]);
            double diff = d - o;
            sumSquaredError += diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double meanSquaredError = sumSquaredError / original.Length;
        return 10.0 * Math.Log10((peak * peak) / meanSquaredError);
    }

    private static Half[] CropTopLeft(ReadOnlySpan<Half> rgba, int sourceWidth, int cropWidth, int cropHeight)
    {
        int channels = BlockInfo.ChannelsPerPixel;
        Half[] cropped = new Half[cropWidth * cropHeight * channels];
        for (int y = 0; y < cropHeight; y++)
        {
            ReadOnlySpan<Half> sourceRow = rgba.Slice(y * sourceWidth * channels, cropWidth * channels);
            sourceRow.CopyTo(cropped.AsSpan(y * cropWidth * channels, cropWidth * channels));
        }

        return cropped;
    }

    private static float[] HalvesToFloats(Half[] halves)
    {
        float[] floats = new float[halves.Length];
        for (int i = 0; i < halves.Length; i++)
        {
            floats[i] = (float)halves[i];
        }

        return floats;
    }
}
