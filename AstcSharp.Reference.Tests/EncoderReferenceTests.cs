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

        CompareRgba8(armDecoded, pixels, $"VoidExtent_{footprintType}");
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

        CompareRgba8(armDecoded, pixels, $"VoidExtentColor_{r}_{g}_{b}_{a}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedGradient_DecodesUnderArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        byte[] pixels = GradientImage(width, height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);

        // Our blocks must be spec-legal: ARM's decoder reads them, and its reconstruction must
        // agree with ours (both decode the same legal bitstream).
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(encoded, width, height, blockX, blockY);
        Span<byte> ourDecoded = AstcDecoder.DecompressImage(encoded, width, height, footprint);

        CompareRgba8(armDecoded, ourDecoded.ToArray(), $"Gradient_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedGradient_QualityWithinMarginOfReferenceEncoder(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        byte[] pixels = GradientImage(width, height);

        // Our encoder, decoded by our decoder.
        byte[] ourEncoded = AstcEncoder.CompressImage(pixels, width, height, footprint);
        Span<byte> ourDecoded = AstcDecoder.DecompressImage(ourEncoded, width, height, footprint);
        double ourPsnr = Psnr(pixels, ourDecoded);

        // ARM's encoder, decoded by ARM's decoder.
        byte[] armEncoded = ReferenceDecoder.CompressLdr(pixels, width, height, blockX, blockY);
        byte[] armDecoded = ReferenceDecoder.DecompressLdr(armEncoded, width, height, blockX, blockY);
        double armPsnr = Psnr(pixels, armDecoded);

        // The encoder targets quality within 3 dB of the reference, not bit parity.
        ourPsnr.Should().BeGreaterThanOrEqualTo(
            armPsnr - 3.0,
            because: $"[{footprintType}] our PSNR {ourPsnr:F2} dB should be within 3 dB of ARM's {armPsnr:F2} dB");
    }

    private static byte[] GradientImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                byte v = (byte)(255 * x / (width - 1));
                pixels[idx] = v;
                pixels[idx + 1] = (byte)(255 - v);
                pixels[idx + 2] = (byte)(128 + (v / 2));
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
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

    private static void CompareRgba8(byte[] actual, byte[] expected, string label)
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
