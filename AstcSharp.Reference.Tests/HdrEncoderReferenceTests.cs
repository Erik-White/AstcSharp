using System.Buffers.Binary;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.Reference.Tests.Utils;
using AstcSharp.Tests.Utils;
using AwesomeAssertions;

namespace AstcSharp.Reference.Tests;

/// <summary>
/// Cross-decoder validity tests for the HDR encode path of <see cref="AstcEncoder"/>: blocks we
/// produce must be spec-legal, i.e. ARM's reference decoder must read them back in agreement with
/// our own decoder (within FP16 tolerance).
/// </summary>
public class HdrEncoderReferenceTests
{
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
    public void EncodedHdrVoidExtent_DecodesUnderArmReference(FootprintType footprintType)
    {
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX;
        int height = blockY;

        // A constant HDR colour above the LDR range drives the HDR void-extent path.
        Half[] pixels = TestImages.SolidHdr(width, height, (Half)2.5f, (Half)1.25f, (Half)3.75f, (Half)1.0f);
        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);

        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrVoidExtent_{footprintType}");
    }

    [Theory]
    [InlineData(0.0f, 0.0f, 0.0f, 1.0f)]
    [InlineData(1.0f, 1.0f, 1.0f, 1.0f)]
    [InlineData(8.0f, 4.0f, 2.0f, 0.5f)]
    [InlineData(100.0f, 0.25f, 60000.0f, 1.0f)]
    public void EncodedHdrVoidExtent_VariousColors_DecodeUnderArmReference(float r, float g, float b, float a)
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        int width = footprint.Width;
        int height = footprint.Height;

        Half[] pixels = TestImages.SolidHdr(width, height, (Half)r, (Half)g, (Half)b, (Half)a);
        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);

        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, 6, 6));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrVoidExtentColor_{r}_{g}_{b}_{a}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedHdrGradient_DecodesUnderArmReference(FootprintType footprintType)
    {
        // A chromatic HDR gradient (values above 1.0) drives the single-partition RGB-direct search.
        // The block is a legal bitstream, so ARM's decode must agree with ours; we do not compare to
        // the original pixels, since the encode is lossy.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        Half[] pixels = TestImages.ChromaticGradientHdr(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrGradient_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedHdrGrayscaleRamp_DecodesUnderArmReference(FootprintType footprintType)
    {
        // A grey HDR ramp drives the luminance-mode (CEM 2) search; ARM's decode must agree with ours.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        Half[] pixels = HdrGrayscaleRamp(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrGrayscaleRamp_{footprintType}");
    }

    [Fact]
    public void EncodedHdrUniformDarkening_SelectsBaseScaleAndDecodesUnderArmReference()
    {
        // A block whose texels are a single bright HDR colour uniformly dimmed toward black in the
        // log domain is exactly what CEM 7 base+scale models. Assert the cheaper 4-value mode is
        // actually chosen (not CEM 11), then require ARM to read the base+scale bitstream in agreement
        // with our decoder.
        var footprintType = FootprintType.Footprint6x6;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        Half[] pixels = HdrUniformDarkening(blockX, blockY);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, blockX, blockY, footprint);

        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        info.EndpointMode0.Should().Be(
            ColorEndpointMode.HdrRgbBaseScale, because: "a uniformly log-darkened HDR colour is the base+scale mode's ideal content");

        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, blockX, blockY, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, blockX, blockY, footprint);

        CompareF16(ourDecoded, armDecoded, "HdrUniformDarkening");
    }

    [Fact]
    public void EncodedHdrNarrowGrayRange_SelectsLumaSmallRangeAndDecodesUnderArmReference()
    {
        // A grey block whose luma spans only a narrow range is what CEM 3 (small range) is for: its
        // finer base step beats CEM 2 (large range) here. Assert the small-range mode is chosen, then
        // require ARM to read the bitstream in agreement with our decoder.
        var footprintType = FootprintType.Footprint6x6;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        Half[] pixels = HdrNarrowGrayRamp(blockX, blockY);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, blockX, blockY, footprint);

        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        info.EndpointMode0.Should().Be(
            ColorEndpointMode.HdrLumaSmallRange, because: "a narrow grey luma span is the small-range luminance mode's ideal content");

        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, blockX, blockY, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, blockX, blockY, footprint);

        CompareF16(ourDecoded, armDecoded, "HdrNarrowGrayRange");
    }

    // Footprints large enough to host distinct colour regions, where the encoder may pick a
    // multi-partition encoding (the HDR analogue of the LDR PartitionableFootprints set).
    public static TheoryData<FootprintType> PartitionableFootprints =>
    [
        FootprintType.Footprint6x6, FootprintType.Footprint8x6, FootprintType.Footprint8x8,
        FootprintType.Footprint10x8, FootprintType.Footprint10x10, FootprintType.Footprint12x12,
    ];

    [Theory]
    [MemberData(nameof(PartitionableFootprints))]
    public void EncodedHdrTwoRegion_DecodesUnderArmReference(FootprintType footprintType)
    {
        // Two well-separated HDR colour regions the encoder may encode with multiple partitions
        // (spec §C.2.21). The resulting shared-CEM multi-partition block must be spec-legal: ARM's
        // decoder must read it back in agreement with ours.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        Half[] pixels = TestImages.TwoRegionHdr(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrTwoRegion_{footprintType}");
    }

    [Fact]
    public void EncodedHdrMultiPartition_DecodesUnderArmReference()
    {
        // Four saturated HDR quadrants drive the encoder to a multi-partition shared-CEM block. Its
        // bitstream layout is what must be spec-legal; we assert it really is multi-partition (not a
        // 1-partition fallback) so the cross-check exercises that layout, then require ARM to agree.
        var footprintType = FootprintType.Footprint12x12;
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        Half[] pixels = TestImages.FourQuadrantHdr(blockX, blockY);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, blockX, blockY, footprint);

        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        info.PartitionCount.Should().BeGreaterThan(1, because: "the four-quadrant HDR block should encode with multiple partitions");

        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, blockX, blockY, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, blockX, blockY, footprint);

        CompareF16(ourDecoded, armDecoded, "HdrMultiPartition");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedHdrVaryingAlpha_DecodesUnderArmReference(FootprintType footprintType)
    {
        // HDR content whose alpha varies drives the RGB+HDR-alpha search (CEM 15). The block must be
        // spec-legal: ARM's decoder must read the alpha pair back in agreement with ours.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        Half[] pixels = HdrVaryingAlpha(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
        float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        CompareF16(ourDecoded, armDecoded, $"HdrVaryingAlpha_{footprintType}");
    }

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void EncodedHdrGradient_QualityWithinMarginOfReferenceEncoder(FootprintType footprintType)
    {
        // Our HDR encoder targets quality within a margin of ARM's, not bit parity. Both are measured
        // as log-space PSNR (error taken on the FP16 bit patterns, the domain HDR error is perceived
        // in) so the comparison is like-for-like.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        Half[] pixels = TestImages.ChromaticGradientHdr(width, height);

        // Our encoder, decoded by our decoder.
        byte[] ourEncoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] ourDecoded = StreamCodec.DecodeHdr(ourEncoded, width, height, footprint);
        double ourPsnr = LogPsnr(pixels, ourDecoded);

        // ARM's encoder, decoded by ARM's decoder.
        byte[] armEncoded = ReferenceDecoder.CompressHdr(pixels, width, height, blockX, blockY);
        float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(armEncoded, width, height, blockX, blockY));
        double armPsnr = LogPsnr(pixels, armDecoded);

        ourPsnr.Should().BeGreaterThanOrEqualTo(
            armPsnr - 6.0,
            because: $"[{footprintType}] our log-PSNR {ourPsnr:F2} dB should be within 6 dB of ARM's {armPsnr:F2} dB");
    }

    [Theory]
    [InlineData(FootprintType.Footprint4x4)]
    [InlineData(FootprintType.Footprint6x6)]
    [InlineData(FootprintType.Footprint8x8)]
    [InlineData(FootprintType.Footprint12x12)]
    public void EncodedRandomHdrBlocks_DecodeUnderArmReference(FootprintType footprintType)
    {
        // The strongest correctness guard: no seeded-random HDR input may produce a bitstream ARM
        // reads differently from us. Multi-region content with random alpha exercises the mode,
        // partition, and dual-plane search across many blocks.
        var (blockX, blockY) = ReferenceDecoder.ToBlockDimensions(footprintType);
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = blockX * 2;
        int height = blockY * 2;
        var rng = new Random(20260730);

        for (int trial = 0; trial < 8; trial++)
        {
            Half[] pixels = RandomHdrRegions(width, height, rng);
            byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);

            float[] armDecoded = HalvesToFloats(ReferenceDecoder.DecompressHdr(encoded, width, height, blockX, blockY));
            float[] ourDecoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

            CompareF16(ourDecoded, armDecoded, $"RandomHdr_{footprintType}_trial{trial}");
        }
    }

    /// <summary>
    /// PSNR computed on the FP16 bit patterns (the log-encoded HDR representation), so the metric
    /// reflects perceived HDR error rather than raw linear magnitude and is comparable between the two
    /// encoders. Peak is the 16-bit FP16 pattern range.
    /// </summary>
    private static double LogPsnr(ReadOnlySpan<Half> original, ReadOnlySpan<float> decoded)
    {
        const double peak = 0x7BFF; // MaxFinite FP16 bit pattern
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

    // A colinear HDR colour ramp with a co-varying HDR alpha ramp — content the RGB+alpha mode
    // (CEM 15) can fit with a single endpoint line.
    private static Half[] HdrVaryingAlpha(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 1] = (Half)(2.0f + (1.0f * t));
                pixels[idx + 2] = (Half)(3.0f - (2.0f * t));
                pixels[idx + 3] = (Half)(0.5f + (2.0f * t));
            }
        }

        return pixels;
    }

    private static Half[] HdrGrayscaleRamp(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float v = 8.0f * (x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)v;
                pixels[idx + 1] = (Half)v;
                pixels[idx + 2] = (Half)v;
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // A grey ramp over a narrow HDR luma span (2.0 to ~2.25), where CEM 3's finer base step wins over
    // CEM 2's coarse large-range step.
    private static Half[] HdrNarrowGrayRamp(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float v = 2.0f + (0.25f * (x + y) / Math.Max(1, width + height - 2));
                pixels[idx] = (Half)v;
                pixels[idx + 1] = (Half)v;
                pixels[idx + 2] = (Half)v;
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // A single bright HDR colour scaled uniformly toward black across the block (constant field
    // difference per channel) — the log-space uniform darkening CEM 7 mode 5 reconstructs exactly.
    private static Half[] HdrUniformDarkening(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                // Scale in the FP16 bit-pattern (log) domain by subtracting a per-texel field offset
                // uniformly from each channel's high value, so all texels lie on one base+scale line.
                int offset = 0x200 * ((x + y) % 4);
                pixels[idx] = ScaledHalf(0x5800, offset);
                pixels[idx + 1] = ScaledHalf(0x5400, offset);
                pixels[idx + 2] = ScaledHalf(0x5000, offset);
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half ScaledHalf(int highBits, int offset)
        => BitConverter.UInt16BitsToHalf((ushort)Math.Max(highBits - offset, 0));

    // A few random solid HDR regions with random per-region alpha, spanning sub-1.0 to well above 1.0.
    private static Half[] RandomHdrRegions(int width, int height, Random rng)
    {
        int regionCount = rng.Next(2, 5);
        (Half R, Half G, Half B, Half A)[] regions = new (Half, Half, Half, Half)[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            regions[i] = (RandomHdrChannel(rng), RandomHdrChannel(rng), RandomHdrChannel(rng), RandomHdrChannel(rng));
        }

        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                (Half r, Half g, Half b, Half a) = regions[rng.Next(regionCount)];
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                pixels[idx + 3] = a;
            }
        }

        return pixels;
    }

    private static Half RandomHdrChannel(Random rng) => (Half)(rng.NextSingle() * 64.0f);

    private static float[] HalvesToFloats(Half[] halves)
    {
        float[] floats = new float[halves.Length];
        for (int i = 0; i < halves.Length; i++)
        {
            floats[i] = (float)halves[i];
        }

        return floats;
    }

    /// <summary>
    /// Asserts every channel of <paramref name="actual"/> matches <paramref name="expected"/> within
    /// a small relative + absolute FP16 tolerance (NaN matches NaN).
    /// </summary>
    private static void CompareF16(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected, string label)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            float a = actual[i];
            float e = expected[i];
            if (float.IsNaN(a) && float.IsNaN(e))
            {
                continue;
            }

            float absDiff = MathF.Abs(a - e);
            float maxVal = MathF.Max(MathF.Abs(a), MathF.Max(MathF.Abs(e), 1e-6f));
            float relDiff = absDiff / maxVal;
            Assert.True(
                absDiff <= 0.001f || relDiff <= 0.001f,
                $"[{label}] channel {i} (pixel {i / 4}) mismatch: actual={a:G5} expected={e:G5} (relDiff={relDiff:P2}).");
        }
    }
}
