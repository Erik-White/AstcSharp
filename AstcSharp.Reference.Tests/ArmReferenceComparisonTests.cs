using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.Reference.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// LDR comparison tests between AstcSharp and the ARM reference ASTC decoder.
/// These validate that AstcSharp produces output matching the official ARM implementation.
/// </summary>
public class ArmReferenceComparisonTests
{
    // Per-channel tolerance for RGBA8 comparisons.
    // ASTC spec conformance allows ±1 for UNORM8 output due to rounding differences.
    private const int Ldr8BitTolerance = 1;

    [Theory]
    [InlineData("atlas_small_4x4")]
    [InlineData("atlas_small_5x5")]
    [InlineData("atlas_small_6x6")]
    [InlineData("atlas_small_8x8")]
    [InlineData("checkerboard")]
    [InlineData("checkered_4")]
    [InlineData("checkered_5")]
    [InlineData("checkered_6")]
    [InlineData("checkered_7")]
    [InlineData("checkered_8")]
    [InlineData("checkered_9")]
    [InlineData("checkered_10")]
    [InlineData("checkered_11")]
    [InlineData("checkered_12")]
    [InlineData("footprint_4x4")]
    [InlineData("footprint_5x4")]
    [InlineData("footprint_5x5")]
    [InlineData("footprint_6x5")]
    [InlineData("footprint_6x6")]
    [InlineData("footprint_8x5")]
    [InlineData("footprint_8x6")]
    [InlineData("footprint_8x8")]
    [InlineData("footprint_10x5")]
    [InlineData("footprint_10x6")]
    [InlineData("footprint_10x8")]
    [InlineData("footprint_10x10")]
    [InlineData("footprint_12x10")]
    [InlineData("footprint_12x12")]
    [InlineData("rgb_4x4")]
    [InlineData("rgb_5x4")]
    [InlineData("rgb_6x6")]
    [InlineData("rgb_8x8")]
    [InlineData("rgb_12x12")]
    public void LdrDecode_ShouldMatchArmReference(string basename)
    {
        var filePath = Path.Combine("TestData", "Input", basename + ".astc");
        var bytes = File.ReadAllBytes(filePath);
        var astcFile = AstcFile.FromMemory(bytes);
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(astcFile.Footprint.Type);

        var astcSharpResult = AstcDecoder.DecompressImage(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(
            astcFile.Blocks, astcFile.Width, astcFile.Height, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, astcFile.Width, astcFile.Height, basename);
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticLdr_SolidColor_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        int w = blockX;
        int h = blockY;

        // Single solid color block: R=128, G=64, B=200, A=255
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4 + 0] = 128;
            pixels[i * 4 + 1] = 64;
            pixels[i * 4 + 2] = 200;
            pixels[i * 4 + 3] = 255;
        }

        var compressed = ArmReferenceDecoder.CompressLdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(compressed, w, h, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, w, h, $"SolidColor_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticLdr_Gradient_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        // 2×2 blocks for gradient
        int w = blockX * 2;
        int h = blockY * 2;

        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                pixels[idx + 0] = (byte)(255 * x / (w - 1)); // R: left-to-right
                pixels[idx + 1] = (byte)(255 * y / (h - 1)); // G: top-to-bottom
                pixels[idx + 2] = (byte)(255 - 255 * x / (w - 1)); // B: inverse of R
                pixels[idx + 3] = 255;
            }
        }

        var compressed = ArmReferenceDecoder.CompressLdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(compressed, w, h, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, w, h, $"Gradient_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void SyntheticLdr_RandomNoise_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);
        // 2×2 blocks
        int w = blockX * 2;
        int h = blockY * 2;

        var rng = new Random(42); // Fixed seed for reproducibility
        var pixels = new byte[w * h * 4];
        rng.NextBytes(pixels);
        // Force alpha to 255 so compression doesn't introduce alpha-related variance
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var compressed = ArmReferenceDecoder.CompressLdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(compressed, w, h, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, w, h, $"RandomNoise_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EdgeCase_NonBlockAlignedDimensions_ShouldMatchArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ArmReferenceDecoder.ToBlockDimensions(footprintType);

        // Non-block-aligned dimensions: use dimensions that don't evenly divide by block size
        int w = blockX + blockX / 2 + 1; // e.g. for 4x4: 7, for 8x8: 13
        int h = blockY + blockY / 2 + 1;

        var rng = new Random(123);
        var pixels = new byte[w * h * 4];
        rng.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var compressed = ArmReferenceDecoder.CompressLdr(pixels, w, h, blockX, blockY);
        var footprint = Footprint.FromFootprintType(footprintType);

        var astcSharpResult = AstcDecoder.DecompressImage(compressed, w, h, footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(compressed, w, h, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, w, h, $"NonAligned_{footprintType}");
    }

    [Fact]
    public void EdgeCase_VoidExtentBlock_ShouldMatchArmReference()
    {
        // Manually construct a void-extent constant-color block (128 bits):
        // Bits [0..8]   = 0b111111100 (0x1FC, void-extent marker)
        // Bit  [9]      = 0 (LDR mode)
        // Bits [10..11]  = 0b11 (reserved, must be 11 for valid void-extent)
        // Bits [12..63]  = all 1s (no extent coordinates = constant color block)
        // Bits [64..79]  = R (UNORM16)
        // Bits [80..95]  = G (UNORM16)
        // Bits [96..111] = B (UNORM16)
        // Bits [112..127]= A (UNORM16)

        var block = new byte[16];
        ulong low = 0xFFFFFFFFFFFFFDFC;
        ulong high = ((ulong)0xFFFF << 48) | ((ulong)0xC000 << 32) | ((ulong)0x4000 << 16) | 0x8000;
        BitConverter.TryWriteBytes(block.AsSpan(0, 8), low);
        BitConverter.TryWriteBytes(block.AsSpan(8, 8), high);

        const int blockX = 4;
        const int blockY = 4;
        var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        var astcSharpResult = AstcDecoder.DecompressImage(block, blockX, blockY, footprint);
        var armResult = ArmReferenceDecoder.DecompressLdr(block, blockX, blockY, blockX, blockY);

        CompareRgba8(astcSharpResult, armResult, blockX, blockY, "VoidExtent");
    }

    /// <summary>
    /// Compare RGBA8 output from both decoders with per-channel tolerance.
    /// </summary>
    private static void CompareRgba8(Span<byte> astcSharp, byte[] armRef, int w, int h, string label)
    {
        int expectedLength = w * h * 4;
        astcSharp.Length.Should().Be(expectedLength, because: $"AstcSharp output size should match for {label}");
        armRef.Length.Should().Be(expectedLength, because: $"ARM output size should match for {label}");

        int mismatches = 0;
        int worstDiff = 0;
        int worstPixel = -1;
        int worstChannel = -1;

        for (int i = 0; i < expectedLength; i++)
        {
            int diff = Math.Abs(astcSharp[i] - armRef[i]);
            if (diff > Ldr8BitTolerance)
            {
                mismatches++;
                if (diff > worstDiff)
                {
                    worstDiff = diff;
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
                $"[{label}] {mismatches} channel mismatches exceed tolerance ±{Ldr8BitTolerance}. " +
                $"Worst: pixel ({px},{py}) channel {channelName}, " +
                $"AstcSharp={astcSharp[worstPixel * 4 + worstChannel]} vs ARM={armRef[worstPixel * 4 + worstChannel]} (diff={worstDiff})");
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
