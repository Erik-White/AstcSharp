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
    public void Compress_TwoColorRegionBlock_SelectsMultiPartition()
    {
        // A block split into two very different solid colours is poorly served by a single
        // endpoint line; the encoder should pick a multi-partition encoding for it.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint8x8);
        byte[] pixels = TwoRegionImage(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);

        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        Assert.True(info.PartitionCount > 1, $"expected a multi-partition block, got {info.PartitionCount} partition(s)");
    }

    [Fact]
    public void Compress_FourColorRegionBlock_SelectsThreePartitions()
    {
        // Four saturated quadrant colours need more than two endpoint lines. A shared RGB mode fits
        // three partitions (3 x 6 = 18 colour values) within budget, so the encoder should pick a
        // 3-partition encoding.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint12x12);
        byte[] pixels = FourQuadrantImage(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);

        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        Assert.Equal(3, info.PartitionCount);

        // Selecting 3 partitions is not enough; the endpoints and partition assignment must also be
        // sound. Four flat colours mapped onto three partitions can't be exact (one partition spans
        // two quadrants), but the reconstruction should still be close. The bound is loose — well
        // above the natural error of this content (~490) yet far below what a mis-assigned partition
        // or corrupted endpoints would produce (a 2-partition fit of the same image exceeds 1200) —
        // so it is a regression tripwire for the 3-partition path, not a tight quality claim.
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);
        AssertMeanSquaredErrorAtMost(pixels, decoded, maxMeanSquaredError: 700.0, footprint);
    }

    [Theory]
    [InlineData(FootprintType.Footprint6x6)]
    [InlineData(FootprintType.Footprint8x8)]
    [InlineData(FootprintType.Footprint10x10)]
    [InlineData(FootprintType.Footprint12x12)]
    public void Compress_ManyRandomMultiRegionBlocks_StayWithinColorValueBudget(FootprintType footprintType)
    {
        // A shared colour endpoint mode stores 8 values per partition for RGBA, 6 for RGB, 2 for
        // luma; the encoder picks one that fits the 18-value budget (spec §C.2.11) at the chosen
        // partition count (up to 4). Fuzz many multi-region blocks and assert every emitted block is
        // legal: within the colour-value budget and decoding without any error-colour (magenta) texels.
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        var rng = new Random(1234);

        for (int trial = 0; trial < 64; trial++)
        {
            byte[] pixels = RandomRegionImage(footprint.Width, footprint.Height, rng);
            byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);

            UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
            BlockInfo info = BlockModeDecoder.Decode(bits);
            Assert.True(info.IsValid, $"trial {trial}: encoded block must be legal");
            Assert.True(info.IsVoidExtent || info.Colors.Count <= 18, $"trial {trial}: colour value count {info.Colors.Count} exceeds the 18-value budget");

            Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);
            AssertNoMagentaBlocks(decoded);
        }
    }

    [Fact]
    public void Compress_TwoColorRegionBlock_ReconstructsWellViaPartitioning()
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint8x8);
        byte[] pixels = TwoRegionImage(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);

        // Two separate ramps reconstruct well once each region gets its own endpoint line.
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

    [Fact]
    public void Compress_VaryingAlphaBlock_SelectsRgbaModeAndReconstructsAlpha()
    {
        // Alpha that varies across the block forces a full RGBA CEM (mode 12 or 13) — the only modes
        // that carry alpha. This is the path that exercises the RGBA alpha colour-value slots
        // end-to-end, which the opaque test images never reach.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        byte[] pixels = VaryingAlphaRamp(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        ColorEndpointMode mode = DecodeEndpointMode(encoded);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);

        Assert.True(
            mode is ColorEndpointMode.LdrRgbaDirect or ColorEndpointMode.LdrRgbaBaseOffset,
            $"expected an RGBA mode for varying-alpha content, got {mode}");
        AssertMeanSquaredErrorAtMost(pixels, decoded, maxMeanSquaredError: 100.0, footprint);
    }

    [Fact]
    public void Compress_DecorrelatedAlphaBlock_SelectsDualPlaneAndReconstructsWell()
    {
        // Alpha that ramps opposite to RGB cannot be tracked by a single shared weight (one endpoint
        // line can't run two directions at once). A second weight plane on the alpha channel
        // (spec §C.2.20) fits both independently, so the encoder should pick a dual-plane block and
        // reconstruct the anti-correlated content tightly.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        byte[] pixels = DecorrelatedAlphaRamp(footprint.Width, footprint.Height);

        byte[] encoded = AstcEncoder.CompressImage(pixels, footprint.Width, footprint.Height, footprint);
        Span<byte> decoded = AstcDecoder.DecompressImage(encoded, footprint.Width, footprint.Height, footprint);

        UInt128 bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        Assert.True(info.DualPlane.Enabled, "expected a dual-plane block for anti-correlated alpha content");
        AssertMeanSquaredErrorAtMost(pixels, decoded, maxMeanSquaredError: 50.0, footprint);
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

    // RGB ramps up while alpha ramps down — anti-correlated channels that one weight line cannot
    // track, the ideal case for a dual-plane block with the second plane on alpha.
    private static byte[] DecorrelatedAlphaRamp(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / (width + height - 2);
                byte up = (byte)(t * 255);
                pixels[idx] = up; pixels[idx + 1] = up; pixels[idx + 2] = up;
                pixels[idx + 3] = (byte)((1 - t) * 255);
            }
        }

        return pixels;
    }

    // A ramp whose alpha varies across the block (RGB fixed-ish), forcing a full RGBA endpoint mode.
    private static byte[] VaryingAlphaRamp(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / (width + height - 2);
                pixels[idx] = (byte)(40 + (t * 160));
                pixels[idx + 1] = 90;
                pixels[idx + 2] = 150;
                pixels[idx + 3] = (byte)(20 + (t * 220));
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

    // Two regions, each with its own colour ramp between different endpoint pairs. The four
    // endpoints are not collinear, so no single endpoint line fits well — partitioning is needed.
    private static byte[] TwoRegionImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)y / (height - 1);
                if (x < width / 2)
                {
                    // Left: red -> yellow ramp.
                    pixels[idx] = 220;
                    pixels[idx + 1] = (byte)(20 + (t * 200));
                    pixels[idx + 2] = 20;
                }
                else
                {
                    // Right: blue -> cyan ramp (a different line in RGB space).
                    pixels[idx] = 20;
                    pixels[idx + 1] = (byte)(20 + (t * 200));
                    pixels[idx + 2] = 220;
                }

                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    // Four saturated solid colours, one per quadrant — four well-separated points in RGB space that
    // no two endpoint lines cover, eliciting a 3-partition fit (a shared RGB mode fits three).
    private static byte[] FourQuadrantImage(int width, int height)
    {
        (byte R, byte G, byte B)[] quadrant =
        [
            (240, 20, 20), (20, 240, 20), (20, 20, 240), (240, 240, 20),
        ];

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                int cell = ((y < height / 2) ? 0 : 2) + ((x < width / 2) ? 0 : 1);
                (byte r, byte g, byte b) = quadrant[cell];
                pixels[idx] = r; pixels[idx + 1] = g; pixels[idx + 2] = b; pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    // A block tiled into a 2D grid of distinct random solid colours — multi-region content that no
    // single endpoint line (and often no 2-way split) fits well, the kind most likely to tempt the
    // encoder toward a 3+ partition fit.
    private static byte[] RandomRegionImage(int width, int height, Random rng)
    {
        int cols = rng.Next(2, 4);
        int rows = rng.Next(2, 4);
        var colors = new (byte R, byte G, byte B)[cols * rows];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        }

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                int cell = (Math.Min(y * rows / height, rows - 1) * cols) + Math.Min(x * cols / width, cols - 1);
                (byte r, byte g, byte b) = colors[cell];
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
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
