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
