using System.Buffers.Binary;
using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// End-to-end quality tests for the HDR block encoder: encoding smooth HDR content then decoding
/// through our own decoder must reconstruct the source closely. The content is colinear and bounded
/// away from zero, where a single endpoint line genuinely fits — so a loose reconstruction would
/// signal a broken fit or the void-extent fallback, not the inherent limits of the two implemented modes.
/// </summary>
public class HdrBlockEncoderTests
{
    // Fine-grid footprints (weight grid up to the footprint size), where a colinear ramp reconstructs
    // tightly without decimation — the HDR analogue of the LDR SingleColorLineRamp test.
    public static TheoryData<FootprintType> Footprints =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x5, FootprintType.Footprint6x6, FootprintType.Footprint8x8,
    ];

    // Colinear ramps bounded away from zero: a tight relative-error bar is meaningful (no near-zero
    // channel to inflate the ratio) and genuinely achievable by a single endpoint line.
    private const float MaxMeanRelError = 0.05f;

    // The RGB+alpha mode (CEM 15) fits four channels and stores alpha in a coarser 7-bit (>> 9) field
    // than RGB's (>> 8), so a colinear four-channel ramp reconstructs slightly less tightly than the
    // RGB-only cases. A marginally looser bar keeps the test meaningful without masking a bad fit.
    private const float MaxMeanRelErrorWithAlpha = 0.07f;

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Encode_HdrColorRamp_ReconstructsCloselyViaRgbMode(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width;
        int height = footprint.Height;
        Half[] pixels = ColorRamp(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] decoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        AssertMeanRelativeErrorAtMost(pixels, decoded, MaxMeanRelError);
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Encode_HdrGrayscaleRamp_ReconstructsCloselyViaLumaMode(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width;
        int height = footprint.Height;
        Half[] pixels = GrayscaleRamp(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] decoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        AssertMeanRelativeErrorAtMost(pixels, decoded, MaxMeanRelError);
    }

    [Theory]
    [MemberData(nameof(Footprints))]
    public void Encode_HdrColorRampWithAlpha_ReconstructsCloselyViaAlphaMode(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        int width = footprint.Width;
        int height = footprint.Height;
        Half[] pixels = ColorRampWithAlpha(width, height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, width, height, footprint);
        float[] decoded = StreamCodec.DecodeHdr(encoded, width, height, footprint);

        AssertMeanRelativeErrorAtMost(pixels, decoded, MaxMeanRelErrorWithAlpha);
    }

    [Fact]
    public void Encode_SmoothGradientBlock_SelectsDualPlane()
    {
        // A smooth HDR gradient whose channels vary independently reconstructs several dB better with
        // a dual-plane split than with a single partition (measured in HdrEarlyOutSweep), at low
        // single-partition error. Asserting dual-plane specifically guards two things at once: the
        // tuned early-out threshold (a regression upward would early-out to single-partition here) and
        // that the dual-plane HDR search is reached and wins for independently-varying channels.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint8x8);
        Half[] pixels = TestImage.SmoothGradientHdr(footprint.Width, footprint.Height);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, footprint.Width, footprint.Height, footprint);

        UInt128 bits = BinaryPrimitives.ReadUInt128LittleEndian(encoded.AsSpan(0, 16));
        BlockInfo info = BlockModeDecoder.Decode(bits);
        Assert.True(info.DualPlane.Enabled, $"expected a dual-plane block, got {(info.PartitionCount > 1 ? "multi-partition" : "single-partition")} (mode {info.EndpointMode0})");
    }

    [Fact]
    public void Encode_ConstantBlockWithNonFiniteChannels_SanitizesToMatchSearchPath()
    {
        // A constant block takes the void-extent path, which stores FP16 bits verbatim — but a
        // non-constant block's search path clamps out-of-domain channels via Fp16.ToLns. The encoder
        // sanitises the void-extent constant the same way so both paths reconstruct alike: +Inf and
        // positive NaN become the largest finite magnitude, negatives become zero, and finite channels
        // (alpha) are untouched.
        Half positiveNaN = BitConverter.UInt16BitsToHalf(0x7E00);
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        Half[] pixels = ConstantBlock(
            footprint.Width, footprint.Height, Half.PositiveInfinity, positiveNaN, (Half)(-2.0f), (Half)1.0f);

        byte[] encoded = StreamCodec.EncodeHdr(pixels, footprint.Width, footprint.Height, footprint);
        float[] decoded = StreamCodec.DecodeHdr(encoded, footprint.Width, footprint.Height, footprint);

        float maxFinite = (float)Half.MaxValue;
        Assert.Equal(maxFinite, decoded[0]);
        Assert.Equal(maxFinite, decoded[1]);
        Assert.Equal(0.0f, decoded[2]);
        Assert.Equal(1.0f, decoded[3]);
    }

    // Per-channel ramps between HDR values bounded away from zero, colinear in RGB space.
    private static Half[] ColorRamp(int width, int height)
    {
        Half[] pixels = new Half[width * height * BlockInfo.ChannelsPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * BlockInfo.ChannelsPerPixel;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 1] = (Half)(2.0f + (2.0f * t));
                pixels[idx + 2] = (Half)(4.0f - (2.0f * t));
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    private static Half[] GrayscaleRamp(int width, int height)
    {
        Half[] pixels = new Half[width * height * BlockInfo.ChannelsPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * BlockInfo.ChannelsPerPixel;
                float v = 1.0f + (5.0f * (x + y) / Math.Max(1, width + height - 2));
                pixels[idx] = (Half)v;
                pixels[idx + 1] = (Half)v;
                pixels[idx + 2] = (Half)v;
                pixels[idx + 3] = (Half)1.0f;
            }
        }

        return pixels;
    }

    // A colinear RGB ramp plus a co-varying HDR alpha ramp, all bounded away from zero — content the
    // CEM 15 RGB+alpha mode fits with a single endpoint line.
    private static Half[] ColorRampWithAlpha(int width, int height)
    {
        Half[] pixels = new Half[width * height * BlockInfo.ChannelsPerPixel];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = ((y * width) + x) * BlockInfo.ChannelsPerPixel;
                float t = (float)(x + y) / Math.Max(1, width + height - 2);
                pixels[idx] = (Half)(1.0f + (3.0f * t));
                pixels[idx + 1] = (Half)(2.0f + (2.0f * t));
                pixels[idx + 2] = (Half)(4.0f - (2.0f * t));
                pixels[idx + 3] = (Half)(0.5f + (2.0f * t));
            }
        }

        return pixels;
    }

    /// <summary>
    /// Asserts the mean per-channel relative error is at most <paramref name="maxRelError"/>. Content
    /// is bounded away from zero, so the ratio against the source magnitude is well-conditioned.
    /// </summary>
    private static void AssertMeanRelativeErrorAtMost(ReadOnlySpan<Half> original, ReadOnlySpan<float> decoded, float maxRelError)
    {
        double sum = 0;
        for (int i = 0; i < original.Length; i++)
        {
            float o = (float)original[i];
            float d = decoded[i];
            sum += MathF.Abs(o - d) / MathF.Max(MathF.Abs(o), 1e-3f);
        }

        double meanRelError = sum / original.Length;
        Assert.True(meanRelError <= maxRelError, $"mean relative error {meanRelError:F4} exceeded {maxRelError:F4}");
    }

    private static Half[] ConstantBlock(int width, int height, Half r, Half g, Half b, Half a)
    {
        Half[] pixels = new Half[width * height * BlockInfo.ChannelsPerPixel];
        for (int i = 0; i < width * height; i++)
        {
            int idx = i * BlockInfo.ChannelsPerPixel;
            pixels[idx] = r;
            pixels[idx + 1] = g;
            pixels[idx + 2] = b;
            pixels[idx + 3] = a;
        }

        return pixels;
    }
}
