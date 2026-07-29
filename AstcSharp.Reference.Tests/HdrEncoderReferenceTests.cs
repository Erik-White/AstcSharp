using AstcSharp.Core;
using AstcSharp.Reference.Tests.Utils;
using AstcSharp.Tests.Utils;

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
        Half[] pixels = SolidImage(width, height, (Half)2.5f, (Half)1.25f, (Half)3.75f, (Half)1.0f);
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

        Half[] pixels = SolidImage(width, height, (Half)r, (Half)g, (Half)b, (Half)a);
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
        Half[] pixels = HdrGradient(width, height);

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

    private static Half[] HdrGradient(int width, int height)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * 4;
                float t = (float)x / Math.Max(1, width - 1);
                pixels[idx] = (Half)(4.0f * t);
                pixels[idx + 1] = (Half)(2.0f * (1.0f - t));
                pixels[idx + 2] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 3] = (Half)1.0f;
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

    private static Half[] SolidImage(int width, int height, Half r, Half g, Half b, Half a)
    {
        Half[] pixels = new Half[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
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
