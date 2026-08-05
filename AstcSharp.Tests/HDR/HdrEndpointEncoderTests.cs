using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Per-mode round-trip tests for <see cref="HdrEndpointEncoder"/>: encoding an HDR endpoint pair,
/// then decoding it back through the real <see cref="EndpointCodec"/> (the exact path the block
/// encoder measures against), must recover the endpoints. These pin the per-mode colour-value layout
/// the block encoder relies on. The colour range is the widest 8-bit range (255), where CE
/// quantisation is the identity, so recovery is exact for values whose discarded low bits are zero.
/// </summary>
public class HdrEndpointEncoderTests
{
    // Widest 8-bit endpoint range: quantise/unquantise round-trips losslessly, so any residual error
    // is a wiring bug, not quantisation.
    private const int LosslessColorRange = byte.MaxValue;

    // These modes decode alpha to the FP16 pattern for 1.0.
    private const ushort AlphaOne = Fp16.One;

    private static (RgbaHdrColor Low, RgbaHdrColor High) RoundTrip(ColorEndpointMode mode, RgbaHdrColor low, RgbaHdrColor high)
    {
        int count = mode.GetColorValuesCount();
        Span<int> colorValues = stackalloc int[count];
        HdrEndpointEncoder.Encode(mode, low, high, LosslessColorRange, colorValues);

        Quantization.UnquantizeCEValuesBatch(colorValues, LosslessColorRange);
        ColorEndpointPair pair = EndpointCodec.Decode(colorValues, mode);
        return (pair.HdrLow, pair.HdrHigh);
    }

    [Fact]
    public void Encode_LumaLargeRange_RecoversGreyEndpoints()
    {
        // CEM 2 stores luma >> 8 and decodes (v << 8), so the low byte must be zero for exact
        // recovery. Grey endpoints ordered low <= high keep the decoder on its non-rounding branch.
        var low = new RgbaHdrColor(0x2000, 0x2000, 0x2000, AlphaOne);
        var high = new RgbaHdrColor(0x5000, 0x5000, 0x5000, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrLumaLargeRange, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_LumaSmallRange_FineDelta_RecoversGreyEndpoints()
    {
        // CEM 3 mode-clear layout: 11-bit base at step 2, delta ≤ 30. Exact recovery needs the 16-bit
        // luma's low 5 bits zero (base step 2 → << 4). base 12-bit 0x400 (→ 0x4000), delta 20 (→ high
        // 12-bit 0x414 → 0x4140), both even and within the fine-delta range.
        var low = new RgbaHdrColor(0x4000, 0x4000, 0x4000, AlphaOne);
        var high = new RgbaHdrColor(0x4140, 0x4140, 0x4140, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrLumaSmallRange, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_LumaSmallRange_WideDelta_RecoversGreyEndpoints()
    {
        // CEM 3 mode-set layout (delta > 30): 10-bit base at step 4, delta ≤ 124 at step 4. base
        // 12-bit 0x400 (→ 0x4000), delta 40 (→ high 12-bit 0x428 → 0x4280), both multiples of 4.
        var low = new RgbaHdrColor(0x4000, 0x4000, 0x4000, AlphaOne);
        var high = new RgbaHdrColor(0x4280, 0x4280, 0x4280, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrLumaSmallRange, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_LumaSmallRange_DeltaBeyondFieldMax_ClampsInsteadOfWrapping()
    {
        // A delta far past the mode-set field max (124 at step 4) must clamp to it, not wrap to an
        // arbitrary small value. base 12-bit 0x400 (→ 0x4000); source delta 200 (high 12-bit 0x4C8,
        // → 0x4C80) exceeds 124, so the recovered high is base + 124 = 0x47C (→ LNS 0x47C0), the
        // nearest representable — not the wrapped ~base+72 the pre-fix mask produced.
        var low = new RgbaHdrColor(0x4000, 0x4000, 0x4000, AlphaOne);
        var high = new RgbaHdrColor(0x4C80, 0x4C80, 0x4C80, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrLumaSmallRange, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(new RgbaHdrColor(0x47C0, 0x47C0, 0x47C0, AlphaOne), recoveredHigh);
    }

    [Fact]
    public void Encode_RgbDirect_RecoversRgbEndpoints()
    {
        // CEM 11 direct sub-mode: R/G store the top byte (low 8 bits discarded), B stores a 7-bit
        // (value >> 9) field (low 9 bits discarded). Distinct per-channel values catch a channel
        // swap; all are multiples of 0x200 so recovery is exact. Alpha decodes to 1.0.
        var low = new RgbaHdrColor(0x1000, 0x2000, 0x2200, AlphaOne);
        var high = new RgbaHdrColor(0x4000, 0x5000, 0x6200, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirect, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_RgbDirect_MaxChannelValues_StayInRange()
    {
        // The largest representable endpoints must not overflow the stored fields (B is a 7-bit
        // >> 9 field, so 0xFE00 is the largest exactly-representable blue).
        var low = new RgbaHdrColor(0x0000, 0x0000, 0x0000, AlphaOne);
        var high = new RgbaHdrColor(0xFF00, 0xFF00, 0xFE00, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirect, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_RgbBaseScale_RecoversUniformlyDarkenedEndpoints()
    {
        // CEM 7 mode 5 reconstructs high = field << 9 and low = (field - scale) << 9 with one shared
        // scale, so it is exact only when the low endpoint is the high uniformly darkened in the 7-bit
        // field domain. Fields are >> 9 (low 9 bits discarded); a uniform scale of 2 fields (0x400)
        // subtracted from each channel satisfies that. Distinct per-channel fields catch a swap.
        var low = new RgbaHdrColor(0x2000, 0x4000, 0x6000, AlphaOne);
        var high = new RgbaHdrColor(0x2400, 0x4400, 0x6400, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbBaseScale, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_RgbBaseScale_FineSubMode_RecoversEndpointsModeFiveCannot()
    {
        // The worst-block endpoints the 4×4 probe decoded from ARM: high channels (16896,15488,13952)
        // are not all multiples of 512, so mode 5 (fields >> 9) rounds green/blue away. A finer sub-mode
        // (shift 3, per-channel deltas) represents them exactly — every channel is a multiple of 8 and
        // the deltas/scale fit the sub-mode's fields — so the selector must pick it and recover exactly.
        var low = new RgbaHdrColor(0x3580, 0x3000, 0x2A00, AlphaOne);
        var high = new RgbaHdrColor(0x4200, 0x3C80, 0x3680, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbBaseScale, low, high);

        Assert.Equal(high, recoveredHigh);
        Assert.Equal(low, recoveredLow);
    }

    [Fact]
    public void Encode_RgbDirect_FinerSubMode_RecoversPrecisionModeZeroCannot()
    {
        // Tightly-clustered endpoints (the 4×4 probe's failure case): 12-bit intermediates that are even
        // but not multiples of 8, so both the passthrough (>>8 or >>9) and mode 0 (value shift 3) round
        // them away. A finer sub-mode (value shift 1) stores them exactly, every 12-bit value is even
        // and the small deltas fit its fields, so the selector must pick it and recover exactly.
        var low = new RgbaHdrColor(0x3EA0, 0x3E60, 0x3E20, AlphaOne);
        var high = new RgbaHdrColor(0x3F20, 0x3EE0, 0x3EA0, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirect, low, high);

        Assert.Equal(high, recoveredHigh);
        Assert.Equal(low, recoveredLow);
    }

    [Fact]
    public void Encode_RgbDirect_FineSubMode_RecoversAtNineBitPrecision()
    {
        // Correlated endpoints whose channels carry bit 7 (a 9-bit-precision detail the passthrough
        // sub-mode's 8-bit >> 8 R/G fields would round away). Mode 0's 9-bit anchor + deltas keeps them:
        // all channels are multiples of 0x80 (low 7 bits zero) and the per-channel spreads sit inside the
        // delta fields (red span 0x1000 -> c = 32 ≤ 63), so recovery is exact — proving the finer
        // sub-mode both engages and round-trips.
        var low = new RgbaHdrColor(0x4080, 0x3880, 0x3080, AlphaOne);
        var high = new RgbaHdrColor(0x5080, 0x4880, 0x4080, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirect, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_RgbDirect_DecorrelatedChannels_FallsBackToPassthrough()
    {
        // Channels too decorrelated for mode 0's delta fields (blue far above red) must fall back to the
        // passthrough sub-mode rather than emit an out-of-range delta. Passthrough keeps R/G at 8 bits
        // and B at 7, so multiples of 0x100 (R/G) and 0x200 (B) recover exactly.
        var low = new RgbaHdrColor(0x1000, 0x1000, 0x6000, AlphaOne);
        var high = new RgbaHdrColor(0x2000, 0x2000, 0xFE00, AlphaOne);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirect, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }

    [Fact]
    public void Encode_RgbDirectHdrAlpha_RecoversRgbAndAlpha()
    {
        // CEM 15: RGB as CEM 11 direct, plus an HDR alpha pair. Alpha is a 7-bit (value >> 9) field
        // (low 9 bits discarded), so distinct alpha multiples of 0x200 recover exactly and pin the
        // alpha slots (6/7) against an RGB mix-up.
        var low = new RgbaHdrColor(0x1000, 0x2000, 0x2200, 0x2400);
        var high = new RgbaHdrColor(0x4000, 0x5000, 0x6200, 0x7800);

        (RgbaHdrColor recoveredLow, RgbaHdrColor recoveredHigh) = RoundTrip(ColorEndpointMode.HdrRgbDirectHdrAlpha, low, high);

        Assert.Equal(low, recoveredLow);
        Assert.Equal(high, recoveredHigh);
    }
}
