using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Reference.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// HDR comparison tests between AstcSharp and the ARM reference ASTC decoder.
/// These validate that AstcSharp produces HDR output matching the official ARM implementation.
/// </summary>
public class ArmReferenceHdrComparisonTests
{
    [Theory]
    [InlineData("HDR-A-1x1")]
    [InlineData("hdr-tile")]
    [InlineData("LDR-A-1x1")]
    [InlineData("ldr-tile")]
    public void HdrDecode_WithHdrFiles_ShouldMatchArmReference(string basename)
    {
        var filePath = Path.Combine("TestData", "HDR", basename + ".astc");
        if (!File.Exists(filePath))
            return; // Skip if HDR test file not present

        var bytes = File.ReadAllBytes(filePath);
        var astcFile = AstcFile.FromMemory(bytes);
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(astcFile.Footprint.Type);

        var astcSharpResult = AstcDecoder.DecompressHdrImage(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        var armResult = ArmReferenceDecoder.DecompressHdr(
            astcFile.Blocks, astcFile.Width, astcFile.Height, blockX, blockY);

        CompareF16(astcSharpResult, armResult, astcFile.Width, astcFile.Height, basename);
    }

    [Theory]
    [InlineData("atlas_small_4x4")]
    [InlineData("atlas_small_5x5")]
    [InlineData("atlas_small_6x6")]
    [InlineData("atlas_small_8x8")]
    public void HdrDecode_WithLdrFiles_ShouldMatchArmReference(string basename)
    {
        var filePath = Path.Combine("TestData", "Input", basename + ".astc");
        var bytes = File.ReadAllBytes(filePath);
        var astcFile = AstcFile.FromMemory(bytes);
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(astcFile.Footprint.Type);

        var astcSharpResult = AstcDecoder.DecompressHdrImage(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        var armResult = ArmReferenceDecoder.DecompressHdr(
            astcFile.Blocks, astcFile.Width, astcFile.Height, blockX, blockY);

        CompareF16(astcSharpResult, armResult, astcFile.Width, astcFile.Height, basename);
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticHdr_BrightSolid_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        int w = blockX;
        int h = blockY;

        // Single block: R=G=B=2.0, A=1.0 (above LDR range)
        var pixels = new Half[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4 + 0] = (Half)2.0f;
            pixels[i * 4 + 1] = (Half)2.0f;
            pixels[i * 4 + 2] = (Half)2.0f;
            pixels[i * 4 + 3] = (Half)1.0f;
        }

        var compressed = ArmReferenceDecoder.CompressHdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressHdrImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressHdr(compressed, w, h, blockX, blockY);

        CompareF16(astcSharpResult, armResult, w, h, $"BrightSolid_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticHdr_Gradient_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        // 2×2 blocks for HDR gradient
        int w = blockX * 2;
        int h = blockY * 2;

        // Gradient from 0.0 to 4.0
        var pixels = new Half[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                float t = (float)(y * w + x) / (w * h - 1);
                float value = t * 4.0f;
                pixels[idx + 0] = (Half)value;
                pixels[idx + 1] = (Half)value;
                pixels[idx + 2] = (Half)value;
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        var compressed = ArmReferenceDecoder.CompressHdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressHdrImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressHdr(compressed, w, h, blockX, blockY);

        CompareF16(astcSharpResult, armResult, w, h, $"HdrGradient_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticHdr_MixedLdrHdr_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        // 2×2 blocks
        int w = blockX * 2;
        int h = blockY * 2;
        int halfW = w / 2;

        var pixels = new Half[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                if (x < halfW)
                {
                    // LDR left half: values in 0.0-1.0
                    float t = (float)y / (h - 1);
                    pixels[idx + 0] = (Half)(t * 0.8f);
                    pixels[idx + 1] = (Half)(t * 0.5f);
                    pixels[idx + 2] = (Half)(t * 0.3f);
                }
                else
                {
                    // HDR right half: values above 1.0
                    float t = (float)y / (h - 1);
                    pixels[idx + 0] = (Half)(1.0f + t * 3.0f);
                    pixels[idx + 1] = (Half)(0.5f + t * 2.0f);
                    pixels[idx + 2] = (Half)(0.2f + t * 1.5f);
                }
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        var compressed = ArmReferenceDecoder.CompressHdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressHdrImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressHdr(compressed, w, h, blockX, blockY);

        CompareF16(astcSharpResult, armResult, w, h, $"MixedLdrHdr_{footprintType}");
    }

    /// <summary>
    /// Compare FP16 output from both decoders.
    /// The ARM reference with AstcencTypeF16 outputs raw FP16 values (bit-cast from uint16).
    /// AstcSharp normalizes ushort→Half via value/65535.0f.
    /// Per ASTC spec (Section C.2.19), HDR output should be bit-cast from uint16 to FP16.
    /// This comparison checks for exact bit-level match, which will likely reveal the
    /// normalization vs bit-cast difference in AstcSharp's HDR path.
    /// </summary>
    private static void CompareF16(Span<Half> astcSharp, Half[] armRef, int w, int h, string label)
    {
        int expectedLength = w * h * 4;
        astcSharp.Length.Should().Be(expectedLength, because: $"AstcSharp F16 output size should match for {label}");
        armRef.Length.Should().Be(expectedLength, because: $"ARM F16 output size should match for {label}");

        int mismatches = 0;
        float worstRelDiff = 0;
        int worstPixel = -1;
        int worstChannel = -1;

        for (int i = 0; i < expectedLength; i++)
        {
            float a = (float)astcSharp[i];
            float b = (float)armRef[i];

            // Both NaN → match; one NaN → mismatch
            if (float.IsNaN(a) && float.IsNaN(b))
                continue;
            if (float.IsNaN(a) || float.IsNaN(b))
            {
                mismatches++;
                continue;
            }

            float absDiff = MathF.Abs(a - b);
            float maxVal = MathF.Max(MathF.Abs(a), MathF.Max(MathF.Abs(b), 1e-6f));
            float relDiff = absDiff / maxVal;

            // Use a relative tolerance of 0.1% plus absolute tolerance of one FP16 ULP (~0.001 for values near 1.0)
            if (absDiff > 0.001f && relDiff > 0.001f)
            {
                mismatches++;
                if (relDiff > worstRelDiff)
                {
                    worstRelDiff = relDiff;
                    worstPixel = i / 4;
                    worstChannel = i % 4;
                }
            }
        }

        if (mismatches > 0)
        {
            string channelName = worstChannel switch { 0 => "R", 1 => "G", 2 => "B", _ => "A" };
            int px = worstPixel % w;
            int py = worstPixel / w;
            Assert.Fail(
                $"[{label}] {mismatches}/{expectedLength} F16 channel mismatches. " +
                $"Worst: pixel ({px},{py}) channel {channelName}, " +
                $"AstcSharp={(float)astcSharp[worstPixel * 4 + worstChannel]:G5} vs " +
                $"ARM={(float)armRef[worstPixel * 4 + worstChannel]:G5} " +
                $"(relDiff={worstRelDiff:P2}). " +
                $"This may indicate AstcSharp is normalizing (ushort/65535) instead of bit-casting to FP16.");
        }
    }

    public static TheoryData<FootprintType> AllFootprintTypes => new()
    {
        FootprintType.Footprint4x4,
        FootprintType.Footprint5x4,
        FootprintType.Footprint5x5,
        FootprintType.Footprint6x5,
        FootprintType.Footprint6x6,
        FootprintType.Footprint8x5,
        FootprintType.Footprint8x6,
        FootprintType.Footprint8x8,
        FootprintType.Footprint10x5,
        FootprintType.Footprint10x6,
        FootprintType.Footprint10x8,
        FootprintType.Footprint10x10,
        FootprintType.Footprint12x10,
        FootprintType.Footprint12x12,
    };
}
