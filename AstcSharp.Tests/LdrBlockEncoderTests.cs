using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Tests;

/// <summary>
/// Round-trip tests for the single-partition LDR encoder (spec §C.2.14 mode 12, §C.2.19). Every 2D
/// footprint must produce legal blocks (weight-grid decimation handles those over 64 texels). Tight
/// reconstruction quality is checked on the smaller footprints, where the fitted grid affords a fine
/// weight range; the broader quality bar is validated against the reference encoder.
/// </summary>
public class LdrBlockEncoderTests
{
    public static TheoryData<FootprintType> AllFootprints =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6, FootprintType.Footprint8x5,
        FootprintType.Footprint8x6, FootprintType.Footprint8x8, FootprintType.Footprint10x5,
        FootprintType.Footprint10x6, FootprintType.Footprint10x8, FootprintType.Footprint10x10,
        FootprintType.Footprint12x10, FootprintType.Footprint12x12,
    ];

    // Footprints small enough that the fitted weight grid still affords a fine weight range,
    // so a smooth single-line ramp reconstructs tightly.
    public static TheoryData<FootprintType> FineGridFootprints =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6,
    ];

    [Theory]
    [MemberData(nameof(AllFootprints))]
    public void Compress_Gradient_ProducesValidBlocks(FootprintType footprintType)
    {
        // Every 2D footprint must produce legal blocks. An illegal block decodes to the magenta
        // error colour; reconstruction quality is validated against the reference encoder.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        (byte[] pixels, Span<byte> decoded) = EncodeThenDecode(footprint, GradientImage);

        Assert.Equal(pixels.Length, decoded.Length);
        AssertNoMagentaBlocks(decoded);
    }

    [Theory]
    [MemberData(nameof(FineGridFootprints))]
    public void Compress_SingleColorLineRamp_ReconstructsTightly(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        (byte[] pixels, Span<byte> decoded) = EncodeThenDecode(footprint, SingleColorLineRamp);

        // ~PSNR 28 dB: a single-line block should track the ramp closely, but weight quantisation
        // and the byte-rounded endpoints leave a small residual that grows with texel count.
        AssertMeanSquaredErrorAtMost(pixels, decoded, maxMeanSquaredError: 100.0, footprint);
    }

    [Fact]
    public void Compress_GrayscaleOpaqueBlock_SelectsLuminanceMode()
    {
        // A grey, opaque block should be encoded with a luminance CEM (mode 0 or 1), which is
        // cheaper than RGBA and frees budget for weight precision.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        byte[] pixels = SingleChannelRamp(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        ColorEndpointMode mode = DecodeEndpointMode(encoded);

        Assert.True(
            mode is ColorEndpointMode.LdrLumaDirect or ColorEndpointMode.LdrLumaBaseOffset,
            $"expected a luminance mode for grey content, got {mode}");
    }

    [Fact]
    public void Compress_OpaqueColorBlock_SelectsRgbMode()
    {
        // An opaque, chromatic block should use an RGB CEM (mode 8 or 9), dropping alpha.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        byte[] pixels = SingleColorLineRamp(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        ColorEndpointMode mode = DecodeEndpointMode(encoded);

        Assert.True(
            mode is ColorEndpointMode.LdrRgbDirect or ColorEndpointMode.LdrRgbBaseOffset,
            $"expected an RGB mode for opaque colour content, got {mode}");
    }

    [Theory]
    [MemberData(nameof(AllFootprints))]
    public void Compress_SolidColor_RoundTripsExactly(FootprintType footprintType)
    {
        // Constant blocks take the void-extent path even through the general encoder entry point.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        byte[] pixels = SolidImage(footprint.Width, footprint.Height, 73, 140, 200, 255);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);

        Assert.Equal(pixels, decoded.ToArray());
    }

    /// <summary>
    /// Encodes a 2x2-block image (so interior and edge blocks are exercised) built by
    /// <paramref name="fill"/>, decodes it back, and returns both for comparison.
    /// </summary>
    private static (byte[] Pixels, byte[] Decoded) EncodeThenDecode(Footprint footprint, Func<int, int, byte[]> fill)
    {
        int width = footprint.Width * 2;
        int height = footprint.Height * 2;
        byte[] pixels = fill(width, height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, width, height, footprint);
        byte[] decoded = AstcDecoder.DecompressImage(encoded, width, height, footprint).ToArray();
        return (pixels, decoded);
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

    // A ramp between two fixed endpoint colours (all texels on one line in RGBA space):
    // (20,200,0,255) -> (220,20,255,255) — the ideal case for a single-partition, single-line block.
    private static byte[] SingleColorLineRamp(int width, int height)
    {
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

        return pixels;
    }

    // A grayscale ramp (R=G=B varying, opaque) — single-channel content for the luminance modes.
    private static byte[] SingleChannelRamp(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                byte v = (byte)(255 * (x + y) / (width + height - 2));
                pixels[idx] = v; pixels[idx + 1] = v; pixels[idx + 2] = v; pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Decodes the colour endpoint mode of the first block of an encoded single-block image.
    /// </summary>
    private static ColorEndpointMode DecodeEndpointMode(byte[] encoded)
    {
        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        return info.GetEndpointMode(0);
    }

    private static byte[] SolidImage(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
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
