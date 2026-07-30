using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes a pair of HDR endpoints into the quantised colour values for a given HDR colour endpoint
/// mode (spec §C.2.15) — the inverse of the HDR path in <see cref="EndpointCodec.Decode"/>. Endpoint
/// channels are the 16-bit LNS-domain values the decoder produces and interpolates (a 12-bit
/// intermediate shifted left by 4), so encode and decode share one representation.
/// </summary>
/// <remarks>
/// Correctness is guaranteed by construction, as for <see cref="EndpointEncoder"/>: the caller
/// decodes the produced colour values back through <see cref="EndpointCodec.Decode"/> to measure
/// reconstruction error, so an imperfect inverse only costs quality (the mode loses the search) and
/// can never make an illegal block.
/// </remarks>
internal static class HdrEndpointEncoder
{
    // CEM 11 direct sub-mode is selected when both major-component bits (v4[7], v5[7]) are set.
    private const int MajorComponentDirectFlag = 0x80;

    // CEM 11 direct blue is a 7-bit field: value = channel >> 9, low 7 bits kept.
    private const int BlueFieldMask = 0x7F;

    // CEM 15 simple alpha sub-mode is selected when both v6[7] and v7[7] are set; each alpha value is
    // then a 7-bit field (channel >> 9) in the low bits.
    private const int SimpleAlphaSelectorFlag = 0x80;
    private const int AlphaFieldMask = 0x7F;

    // CEM 7 mode 5 is selected by the 4-bit mode value 0xF: v0[7:6] = 11, v1[7] = v2[7] = 1. It is the
    // only base+scale sub-mode that stores R/G/B independently (no green = red - delta) and applies no
    // major-component swizzle, so it inverts cleanly.
    private const int BaseScaleMode5V0Selector = 0xC0;
    private const int BaseScaleModeBitFlag = 0x80;
    private const int SevenBitFieldMask = 0x7F;
    private const int SixBitFieldMask = 0x3F;

    // CEM 7 channel/scale fields are 7-bit: value = channel >> 9.
    private const int BaseScaleFieldShift = 9;
    private const int MaxSevenBitField = 0x7F;

    // CEM 3 (HDR luma, small range) packs a base luma and a delta into two values, choosing between
    // two layouts by the mode bit v0[7]. Luma is a 12-bit intermediate: value = channel >> 4.
    private const int SmallRangeLumaShift = 4;
    private const int SmallRangeModeBitFlag = 0x80;

    // Mode-clear layout (v0[7] = 0): base is stored at step 2 (finer), delta at step 2, max delta 30.
    private const int SmallRangeFineDeltaMax = 30;

    // Largest 12-bit luma the decoder produces (spec §C.2.14 clamps y1 to this).
    private const int MaxLuma12Bit = 0xFFF;

    /// <summary>
    /// Encodes <paramref name="low"/>/<paramref name="high"/> for <paramref name="mode"/> into
    /// quantised colour values written to <paramref name="colorValues"/> (length must be at least
    /// <c>mode.GetColorValuesCount()</c>). Endpoints are assumed ordered low-to-high in the LNS
    /// domain, matching the decoder's non-swapping paths.
    /// </summary>
    public static void Encode(ColorEndpointMode mode, RgbaHdrColor low, RgbaHdrColor high, int colorRange, Span<int> colorValues)
    {
        switch (mode)
        {
            case ColorEndpointMode.HdrLumaLargeRange: EncodeLumaLargeRange(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrLumaSmallRange: EncodeLumaSmallRange(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbBaseScale: EncodeRgbBaseScale(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbDirect: EncodeRgbDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.HdrRgbDirectHdrAlpha: EncodeRgbDirectHdrAlpha(colorRange, colorValues, low, high); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported HDR endpoint mode for encoding");
        }
    }

    /// <summary>
    /// CEM 2 (HDR luminance, large range): two 8-bit luma values. The decoder's <c>v1 &gt;= v0</c>
    /// branch reconstructs each channel as <c>v &lt;&lt; 8</c>, so the top byte of the LNS luma is the
    /// stored value. Endpoints ordered low-to-high keep the decoder on that exact branch.
    /// </summary>
    private static void EncodeLumaLargeRange(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        int lumaLow = LnsLuma(low);
        int lumaHigh = LnsLuma(high);
        colorValues[0] = QuantizeByte(lumaLow >> 8, colorRange);
        colorValues[1] = QuantizeByte(lumaHigh >> 8, colorRange);
    }

    /// <summary>
    /// CEM 3 (HDR luminance, small range): a base luma plus a non-negative delta, giving a much finer
    /// base step than CEM 2 (large range) over a narrow luma span. Two layouts are selected by the
    /// mode bit v0[7]: the mode-clear layout stores an 11-bit base at step 2 with delta ≤ 30, the
    /// mode-set layout a 10-bit base at step 4 with delta ≤ 124. This picks the finer (mode-clear)
    /// layout whenever the luma delta fits it, matching the decoder's reconstruction exactly.
    /// </summary>
    private static void EncodeLumaSmallRange(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        // 12-bit intermediate luma; endpoints ordered low-to-high so the delta is non-negative.
        int baseLuma = Math.Clamp(LnsLuma(low) >> SmallRangeLumaShift, 0, MaxLuma12Bit);
        int highLuma = Math.Clamp(LnsLuma(high) >> SmallRangeLumaShift, 0, MaxLuma12Bit);
        int delta = highLuma - baseLuma;

        int v0, v1;
        if (delta <= SmallRangeFineDeltaMax)
        {
            // Mode-clear: y0 = (v1 & 0xF0) << 4 | (v0 & 0x7F) << 1; d = (v1 & 0x0F) << 1.
            // Base and delta are at step 2, so drop the low bit of each.
            v0 = (baseLuma >> 1) & 0x7F;
            v1 = (((baseLuma >> 8) & 0x0F) << 4) | ((delta >> 1) & 0x0F);
        }
        else
        {
            // Mode-set: y0 = (v1 & 0xE0) << 4 | (v0 & 0x7F) << 2; d = (v1 & 0x1F) << 2.
            // Base and delta are at step 4, so drop the low two bits of each.
            v0 = SmallRangeModeBitFlag | ((baseLuma >> 2) & 0x7F);
            v1 = (((baseLuma >> 9) & 0x07) << 5) | ((delta >> 2) & 0x1F);
        }

        colorValues[0] = QuantizeByte(v0, colorRange);
        colorValues[1] = QuantizeByte(v1, colorRange);
    }

    /// <summary>
    /// CEM 11 (HDR RGB, direct) via the <c>majcomp == 3</c> direct sub-mode: R/G store the top byte
    /// of the channel (<c>v &lt;&lt; 8</c>), B stores a 7-bit field (<c>(v &amp; 0x7F) &lt;&lt; 9</c>),
    /// and both v4/v5 carry the major-component flag bit that selects this sub-mode.
    /// </summary>
    private static void EncodeRgbDirect(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        colorValues[0] = QuantizeByte(low.R >> 8, colorRange);
        colorValues[1] = QuantizeByte(high.R >> 8, colorRange);
        colorValues[2] = QuantizeByte(low.G >> 8, colorRange);
        colorValues[3] = QuantizeByte(high.G >> 8, colorRange);
        colorValues[4] = QuantizeByte(MajorComponentDirectFlag | ((low.B >> 9) & BlueFieldMask), colorRange);
        colorValues[5] = QuantizeByte(MajorComponentDirectFlag | ((high.B >> 9) & BlueFieldMask), colorRange);
    }

    /// <summary>
    /// CEM 7 (HDR RGB, base+scale) via mode 5, the only base+scale sub-mode that stores R/G/B
    /// independently and applies no channel swizzle. The decoder reconstructs the high endpoint as
    /// <c>field &lt;&lt; 9</c> per channel and the low endpoint as <c>(field − scale) &lt;&lt; 9</c>
    /// with one shared 7-bit scale. That models a uniform log-space darkening (a
    /// uniform multiplicative dim in linear space), fitting content whose dark endpoint is a dimmed
    /// version of the bright one — at 4 colour values rather than CEM 11's 6, leaving more of the
    /// 128-bit budget for weight precision.
    /// </summary>
    private static void EncodeRgbBaseScale(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        int redField = high.R >> BaseScaleFieldShift;
        int greenField = high.G >> BaseScaleFieldShift;
        int blueField = high.B >> BaseScaleFieldShift;

        // One scale serves all three channels; use the mean high-minus-low field difference, clamped
        // so no channel's low endpoint underflows the field range.
        int scale = ScaleField(low, high, redField, greenField, blueField);

        int v0 = BaseScaleMode5V0Selector | (redField & SixBitFieldMask);
        int v1 = BaseScaleModeBitFlag | (greenField & SevenBitFieldMask);
        int v2 = BaseScaleModeBitFlag | (blueField & SevenBitFieldMask);
        int v3 = ((redField >> 6) & 1) << 7 | (scale & SevenBitFieldMask);

        colorValues[0] = QuantizeByte(v0, colorRange);
        colorValues[1] = QuantizeByte(v1, colorRange);
        colorValues[2] = QuantizeByte(v2, colorRange);
        colorValues[3] = QuantizeByte(v3, colorRange);
    }

    /// <summary>
    /// The shared 7-bit base+scale factor for CEM 7 mode 5: the mean per-channel high-minus-low field
    /// difference, clamped to <c>[0, 0x7F]</c>. A larger scale can only drive a channel's low endpoint
    /// negative (the decoder clamps it to 0), so bounding by the smallest field keeps every channel
    /// representable.
    /// </summary>
    private static int ScaleField(RgbaHdrColor low, RgbaHdrColor high, int redField, int greenField, int blueField)
    {
        int lowRed = low.R >> BaseScaleFieldShift;
        int lowGreen = low.G >> BaseScaleFieldShift;
        int lowBlue = low.B >> BaseScaleFieldShift;
        int meanDifference = ((redField - lowRed) + (greenField - lowGreen) + (blueField - lowBlue) + 1) / 3;
        return Math.Clamp(meanDifference, 0, MaxSevenBitField);
    }

    /// <summary>
    /// CEM 15 (HDR RGB + HDR alpha): RGB in values 0–5 exactly as CEM 11 direct, then the alpha pair
    /// in v6/v7 via the simple alpha sub-mode. That sub-mode is selected when both v6[7] and v7[7] are
    /// set, after which each alpha value is a 7-bit <c>(channel &gt;&gt; 9)</c> field, decoded as
    /// <c>field &lt;&lt; 5</c> to the 12-bit intermediate (then <c>&lt;&lt; 4</c> to FP16). All four
    /// channels blend in the LNS domain, so this needs no special reconstruction handling.
    /// </summary>
    private static void EncodeRgbDirectHdrAlpha(int colorRange, Span<int> colorValues, RgbaHdrColor low, RgbaHdrColor high)
    {
        EncodeRgbDirect(colorRange, colorValues, low, high);
        colorValues[6] = QuantizeByte(SimpleAlphaSelectorFlag | ((low.A >> 9) & AlphaFieldMask), colorRange);
        colorValues[7] = QuantizeByte(SimpleAlphaSelectorFlag | ((high.A >> 9) & AlphaFieldMask), colorRange);
    }

    /// <summary>
    /// Rounded LNS-domain luma (mean of the R/G/B channels). Luma modes are chosen only for grey
    /// content, where the three channels are equal, so the mean is exact there.
    /// </summary>
    private static int LnsLuma(RgbaHdrColor color) => (color.R + color.G + color.B + 1) / 3;

    private static int QuantizeByte(int value, int colorRange)
        => Quantization.QuantizeCEValueToRange(Math.Clamp(value, 0, byte.MaxValue), colorRange);
}
