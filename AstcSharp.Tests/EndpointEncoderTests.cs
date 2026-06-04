using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests;

/// <summary>
/// Per-mode round-trip tests for <see cref="EndpointEncoder"/>: encoding an endpoint pair, then
/// decoding it back through the real <see cref="EndpointCodec"/> (the exact path the block encoder
/// measures against), must recover the original endpoints. These pin the per-mode colour-value
/// index layout — especially the alpha slots — that the full encode->decode tests never exercise
/// with varying alpha. The colour range is the widest (255), where quantisation is the identity, so
/// recovery is exact for every mode given endpoints ordered by RGB sum and offsets within range.
/// </summary>
public class EndpointEncoderTests
{
    // Widest endpoint range: an 8-bit direct quantisation, so quantise/unquantise round-trips
    // losslessly and any residual error would be a wiring bug, not quantisation.
    private const int LosslessColorRange = 255;

    /// <summary>
    /// Encodes <paramref name="low"/>/<paramref name="high"/> for <paramref name="mode"/>, then
    /// unquantises and decodes exactly as the block encoder and decoder do, returning the recovered
    /// endpoint pair.
    /// </summary>
    private static (RgbaColor Low, RgbaColor High) RoundTrip(ColorEndpointMode mode, RgbaColor low, RgbaColor high)
    {
        int count = mode.GetColorValuesCount();
        Span<int> colorValues = stackalloc int[count];
        EndpointEncoder.Encode(mode, low, high, LosslessColorRange, colorValues);

        Quantization.UnquantizeCEValuesBatch(colorValues, LosslessColorRange);
        ColorEndpointPair pair = EndpointCodec.Decode(colorValues, mode);
        return (pair.LdrLow, pair.LdrHigh);
    }

    [Fact]
    public void Encode_LumaDirect_RecoversGreyEndpoints()
    {
        // Luma modes collapse RGB to a single grey value, so feed grey endpoints.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrLumaDirect, new RgbaColor(50, 50, 50, 255), new RgbaColor(200, 200, 200, 255));

        Assert.Equal(new RgbaColor(50, 50, 50, 255), low);
        Assert.Equal(new RgbaColor(200, 200, 200, 255), high);
    }

    [Fact]
    public void Encode_LumaAlphaDirect_RecoversGreyAndAlpha()
    {
        // Distinct alpha values (30/220) catch an alpha slot wired to the wrong index (2/3).
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrLumaAlphaDirect, new RgbaColor(50, 50, 50, 30), new RgbaColor(200, 200, 200, 220));

        Assert.Equal(new RgbaColor(50, 50, 50, 30), low);
        Assert.Equal(new RgbaColor(200, 200, 200, 220), high);
    }

    [Fact]
    public void Encode_RgbDirect_RecoversRgbWithOpaqueAlpha()
    {
        // Distinct per-channel values (and low RGB sum < high RGB sum) catch a channel swap and keep
        // the decoder's blue-contract path from firing. RGB modes have no alpha; the decoder fills 255.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbDirect, new RgbaColor(40, 80, 120, 255), new RgbaColor(70, 110, 150, 255));

        Assert.Equal(new RgbaColor(40, 80, 120, 255), low);
        Assert.Equal(new RgbaColor(70, 110, 150, 255), high);
    }

    [Fact]
    public void Encode_RgbaDirect_RecoversAllFourChannels()
    {
        // Alpha (160/190) distinct from every RGB value pins the alpha slots (6/7) against an
        // RGB-channel mix-up; the high RGB sum exceeds the low's, so no blue-contract swap.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbaDirect, new RgbaColor(40, 80, 120, 160), new RgbaColor(70, 110, 150, 190));

        Assert.Equal(new RgbaColor(40, 80, 120, 160), low);
        Assert.Equal(new RgbaColor(70, 110, 150, 190), high);
    }

    [Fact]
    public void Encode_LumaBaseOffset_RecoversGreyEndpoints()
    {
        // High luma - base = 50, within the mode's 6-bit non-negative offset range.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrLumaBaseOffset, new RgbaColor(50, 50, 50, 255), new RgbaColor(100, 100, 100, 255));

        Assert.Equal(new RgbaColor(50, 50, 50, 255), low);
        Assert.Equal(new RgbaColor(100, 100, 100, 255), high);
    }

    [Fact]
    public void Encode_LumaAlphaBaseOffset_RecoversGreyAndAlpha()
    {
        // Per-channel offsets of 30 stay within the signed 6-bit [-32, 31] base+offset range.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrLumaAlphaBaseOffset, new RgbaColor(60, 60, 60, 40), new RgbaColor(90, 90, 90, 70));

        Assert.Equal(new RgbaColor(60, 60, 60, 40), low);
        Assert.Equal(new RgbaColor(90, 90, 90, 70), high);
    }

    [Fact]
    public void Encode_RgbBaseOffset_RecoversRgbWithOpaqueAlpha()
    {
        // Positive per-channel offsets (20) keep the decoder on the non-blue-contract branch.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbBaseOffset, new RgbaColor(40, 80, 120, 255), new RgbaColor(60, 100, 140, 255));

        Assert.Equal(new RgbaColor(40, 80, 120, 255), low);
        Assert.Equal(new RgbaColor(60, 100, 140, 255), high);
    }

    [Fact]
    public void Encode_RgbaBaseOffset_RecoversAllFourChannels()
    {
        // Distinct alpha (160/180) pins the alpha base+offset pair (slot 3) against an RGB mix-up.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbaBaseOffset, new RgbaColor(40, 80, 120, 160), new RgbaColor(60, 100, 140, 180));

        Assert.Equal(new RgbaColor(40, 80, 120, 160), low);
        Assert.Equal(new RgbaColor(60, 100, 140, 180), high);
    }

    [Fact]
    public void Encode_RgbBaseScale_RecoversScaledEndpoints()
    {
        // Base+scale reconstructs low = high * scale >> 8, so it is exact only when low lies on the
        // origin->high line. low = high * 128 >> 8 = high / 2 satisfies that; pin all three channels.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbBaseScale, new RgbaColor(32, 64, 96, 255), new RgbaColor(64, 128, 192, 255));

        Assert.Equal(new RgbaColor(32, 64, 96, 255), low);
        Assert.Equal(new RgbaColor(64, 128, 192, 255), high);
    }

    [Fact]
    public void Encode_RgbBaseScaleTwoAlpha_RecoversScaledRgbAndIndependentAlpha()
    {
        // Mode 10 is mode 6's RGB scaling plus independent low/high alpha (slots 4/5); distinct alpha
        // (50/200) pins those slots against an RGB mix-up.
        (RgbaColor low, RgbaColor high) = RoundTrip(
            ColorEndpointMode.LdrRgbBaseScaleTwoA, new RgbaColor(32, 64, 96, 50), new RgbaColor(64, 128, 192, 200));

        Assert.Equal(new RgbaColor(32, 64, 96, 50), low);
        Assert.Equal(new RgbaColor(64, 128, 192, 200), high);
    }
}
