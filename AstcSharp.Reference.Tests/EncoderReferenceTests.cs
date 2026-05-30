using AstcSharp.Core;
using AstcSharp.Reference.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// Cross-decoder validity tests for <see cref="AstcEncoder"/>: blocks we produce must be
/// spec-legal, i.e. ARM's reference decoder must read them back to the original image (within the
/// ±1 UNORM8 conformance tolerance). This is the key guard that the encoded bitstream is correct,
/// not merely self-consistent with our own decoder.
/// </summary>
public class EncoderReferenceTests
{
    private const int Ldr8BitTolerance = 1;

    public static TheoryData<FootprintType> AllFootprintTypes =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6, FootprintType.Footprint8x5,
        FootprintType.Footprint8x6, FootprintType.Footprint8x8, FootprintType.Footprint10x5,
        FootprintType.Footprint10x6, FootprintType.Footprint10x8, FootprintType.Footprint10x10,
        FootprintType.Footprint12x10, FootprintType.Footprint12x12,
    ];

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedVoidExtent_DecodesUnderArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX;
        int height = blockY;

        byte[] pixels = SolidImage(width, height, 0x80, 0x40, 0xC0, 0xFF);
        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, blockX, blockY);

        CompareRgba8(armDecoded, pixels, width, height, $"VoidExtent_{footprintType}");
    }

    [Theory]
    [InlineData(0, 0, 0, 255)]
    [InlineData(255, 255, 255, 255)]
    [InlineData(200, 100, 50, 128)]
    public void EncodedVoidExtent_VariousColors_DecodeUnderArmReference(byte r, byte g, byte b, byte a)
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        int width = footprint.Width;
        int height = footprint.Height;

        byte[] pixels = SolidImage(width, height, r, g, b, a);
        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);

        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, 6, 6);

        CompareRgba8(armDecoded, pixels, width, height, $"VoidExtentColor_{r}_{g}_{b}_{a}");
    }

    private static byte[] SolidImage(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }

    private static void CompareRgba8(byte[] actual, byte[] expected, int width, int height, string label)
    {
        actual.Length.Should().Be(expected.Length, because: $"decoded size should match for {label}");

        for (int i = 0; i < expected.Length; i++)
        {
            int diff = Math.Abs(actual[i] - expected[i]);
            diff.Should().BeLessThanOrEqualTo(
                Ldr8BitTolerance,
                because: $"[{label}] channel {i} (pixel {i / 4}) should be within ±{Ldr8BitTolerance}: ARM={actual[i]} expected={expected[i]}");
        }
    }
}
