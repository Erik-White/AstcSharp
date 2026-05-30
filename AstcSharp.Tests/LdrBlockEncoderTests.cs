using AstcSharp.Core;

namespace AstcSharp.Tests;

/// <summary>
/// Round-trip tests for the single-partition identity-grid LDR encoder (spec §C.2.14 mode 12,
/// §C.2.19). Correctness holds for every footprint with at most 64 texels; tight reconstruction
/// quality is only expected on small footprints, where one weight per texel leaves enough bits for
/// a fine weight range. Larger footprints need weight-grid decimation for comparable quality.
/// </summary>
public class LdrBlockEncoderTests
{
    // Footprints with <= 64 texels: the identity-grid encoder supports these before decimation.
    public static TheoryData<FootprintType> SmallFootprints =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6, FootprintType.Footprint8x5,
        FootprintType.Footprint8x6, FootprintType.Footprint8x8, FootprintType.Footprint10x5,
        FootprintType.Footprint10x6,
    ];

    // Footprints small enough that one weight per texel still affords a fine weight range,
    // so a smooth single-line ramp reconstructs tightly.
    public static TheoryData<FootprintType> FineGridFootprints =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6,
    ];

    [Theory]
    [MemberData(nameof(SmallFootprints))]
    public void Compress_Gradient_ProducesValidBlocks(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width * 2;
        int height = footprint.Height * 2;
        byte[] pixels = GradientImage(width, height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, width, height, footprint);

        // Correctness guarantee for all <= 64-texel footprints: every block is legal. An illegal
        // block decodes to the magenta error colour. Reconstruction quality (which varies with
        // footprint size for the identity grid) is validated against the reference encoder.
        Assert.Equal(pixels.Length, decoded.Length);
        AssertNoMagentaBlocks(decoded);
    }

    [Theory]
    [MemberData(nameof(FineGridFootprints))]
    public void Compress_SingleColorLineRamp_ReconstructsTightly(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width * 2;
        int height = footprint.Height * 2;

        // A ramp between two fixed endpoint colours (all texels on one line in RGBA space) is the
        // ideal case for a single-partition, single-line block: (20,200,0,255) -> (220,20,255,255).
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / (width + height - 2);
                pixels[idx] = (byte)(20 + (t * 200));
                pixels[idx + 1] = (byte)(200 - (t * 180));
                pixels[idx + 2] = (byte)(t * 255);
                pixels[idx + 3] = 255;
            }
        }

        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, width, height, footprint);

        // ~PSNR 28 dB: a single-line block should track the ramp closely, but weight quantisation
        // and the byte-rounded endpoints leave a small residual that grows with texel count.
        AssertMeanSquaredErrorAtMost(pixels, decoded, maxMeanSquaredError: 100.0, footprint);
    }

    [Theory]
    [MemberData(nameof(SmallFootprints))]
    public void Compress_SolidColor_RoundTripsExactly(FootprintType footprintType)
    {
        // Constant blocks take the void-extent path even through the general encoder entry point.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width;
        int height = footprint.Height;
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 73; pixels[i + 1] = 140; pixels[i + 2] = 200; pixels[i + 3] = 255;
        }

        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, width, height, footprint);

        Assert.Equal(pixels, decoded.ToArray());
    }

    [Fact]
    public void Compress_LargeFootprintNonConstant_Throws()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint12x12);
        int width = footprint.Width;
        int height = footprint.Height;
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        Assert.Throws<NotSupportedException>(() => AstcEncoder.CompressImage(pixels, width, height, footprint));
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
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    private static void AssertMeanSquaredErrorAtMost(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded, double maxMeanSquaredError, Footprint footprint)
    {
        Assert.Equal(original.Length, decoded.Length);

        double sumSquaredError = 0;
        for (int i = 0; i < original.Length; i++)
        {
            int diff = decoded[i] - original[i];
            sumSquaredError += (double)diff * diff;
        }

        double meanSquaredError = sumSquaredError / original.Length;
        Assert.True(
            meanSquaredError <= maxMeanSquaredError,
            $"MSE {meanSquaredError:F2} exceeded {maxMeanSquaredError} for {footprint.Type}");
    }

    private static void AssertNoMagentaBlocks(ReadOnlySpan<byte> decoded)
    {
        for (int i = 0; i < decoded.Length; i += 4)
        {
            bool isMagenta = decoded[i] == 255 && decoded[i + 1] == 0 && decoded[i + 2] == 255 && decoded[i + 3] == 255;
            Assert.False(isMagenta, $"Found error-colour (magenta) texel at index {i / 4}; a block was encoded illegally.");
        }
    }
}
