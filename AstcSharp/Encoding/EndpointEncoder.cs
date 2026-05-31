using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes a pair of LDR endpoints into the quantised colour values for a given colour endpoint
/// mode (spec §C.2.14) — the inverse of <see cref="EndpointCodec.Decode"/>. Supports the LDR
/// direct modes (luma 0, luma+alpha 4, RGB 8, RGBA 12) and base+offset modes (luma 1, luma+alpha 5,
/// RGB 9, RGBA 13).
/// </summary>
/// <remarks>
/// Correctness is guaranteed by construction: the caller decodes the produced colour values back
/// through <see cref="EndpointCodec.Decode"/> to measure reconstruction error, and any in-range
/// colour values form a legal block. An imperfect inverse therefore only costs quality (the mode
/// loses the search) and can never make an illegal block. The base+offset packings avoid the
/// decoder's "blue contract" path by ordering endpoints so the high endpoint's channel sum is the
/// larger one.
/// </remarks>
internal static class EndpointEncoder
{
    // Base+offset offset field is a 6-bit two's-complement value (spec §C.2.14): range [-32, 31].
    private const int OffsetMin = -32;
    private const int OffsetMax = 31;
    private const int OffsetMask = 0x3F;

    private const int MaxChannel = 255;

    // Luma base+offset (mode 1) stores a 6-bit non-negative luma offset.
    private const int LumaOffsetMax = 63;

    /// <summary>
    /// Encodes <paramref name="low"/>/<paramref name="high"/> for <paramref name="mode"/> into
    /// quantised colour values written to <paramref name="colorValues"/> (length must be at least
    /// <c>mode.GetColorValuesCount()</c>). Endpoints are assumed ordered so the high endpoint's
    /// RGB sum is at least the low endpoint's.
    /// </summary>
    public static void Encode(ColorEndpointMode mode, RgbaColor low, RgbaColor high, int colorRange, Span<int> colorValues)
    {
        switch (mode)
        {
            case ColorEndpointMode.LdrLumaDirect: EncodeLumaDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrLumaAlphaDirect: EncodeLumaAlphaDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrRgbDirect: EncodeRgbDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrRgbaDirect: EncodeRgbaDirect(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrLumaBaseOffset: EncodeLumaBaseOffset(colorRange, colorValues, Luma(low), Luma(high)); break;
            case ColorEndpointMode.LdrLumaAlphaBaseOffset: EncodeLumaAlphaBaseOffset(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrRgbBaseOffset: EncodeRgbBaseOffset(colorRange, colorValues, low, high); break;
            case ColorEndpointMode.LdrRgbaBaseOffset: EncodeRgbaBaseOffset(colorRange, colorValues, low, high); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported endpoint mode for encoding");
        }
    }

    /// <summary>
    /// Rounded luma (RGB average) of a colour, matching the grayscale assumption of the luma modes.
    /// </summary>
    private static int Luma(RgbaColor c) => ((c.R + c.G + c.B) + 1) / 3;

    // Mode 0: two luma values.
    private static void EncodeLumaDirect(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        QuantizeInto(colorRange, colorValues, 0, Luma(low));
        QuantizeInto(colorRange, colorValues, 1, Luma(high));
    }

    // Mode 4: luma pair plus low/high alpha.
    private static void EncodeLumaAlphaDirect(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        EncodeLumaDirect(colorRange, colorValues, low, high);
        QuantizeInto(colorRange, colorValues, 2, low.A);
        QuantizeInto(colorRange, colorValues, 3, high.A);
    }

    /// <summary>
    /// Mode 8: interleaved (low, high) per RGB channel. Because the endpoints are pre-ordered so the
    /// high RGB sum is the larger one, the decoder's blue-contract swap never fires, making this the
    /// exact inverse of the decode path.
    /// </summary>
    private static void EncodeRgbDirect(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        QuantizeInto(colorRange, colorValues, 0, low.R);
        QuantizeInto(colorRange, colorValues, 1, high.R);
        QuantizeInto(colorRange, colorValues, 2, low.G);
        QuantizeInto(colorRange, colorValues, 3, high.G);
        QuantizeInto(colorRange, colorValues, 4, low.B);
        QuantizeInto(colorRange, colorValues, 5, high.B);
    }

    // Mode 12: RGB-direct plus low/high alpha.
    private static void EncodeRgbaDirect(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        EncodeRgbDirect(colorRange, colorValues, low, high);
        QuantizeInto(colorRange, colorValues, 6, low.A);
        QuantizeInto(colorRange, colorValues, 7, high.A);
    }

    // Mode 5: luma base+offset plus alpha base+offset.
    private static void EncodeLumaAlphaBaseOffset(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        PackBaseOffset(colorRange, colorValues, 0, Luma(low), Luma(high));
        PackBaseOffset(colorRange, colorValues, 1, low.A, high.A);
    }

    // Mode 9: per-channel RGB base+offset.
    private static void EncodeRgbBaseOffset(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        PackBaseOffset(colorRange, colorValues, 0, low.R, high.R);
        PackBaseOffset(colorRange, colorValues, 1, low.G, high.G);
        PackBaseOffset(colorRange, colorValues, 2, low.B, high.B);
    }

    // Mode 13: RGB base+offset plus alpha base+offset.
    private static void EncodeRgbaBaseOffset(int colorRange, Span<int> colorValues, RgbaColor low, RgbaColor high)
    {
        EncodeRgbBaseOffset(colorRange, colorValues, low, high);
        PackBaseOffset(colorRange, colorValues, 3, low.A, high.A);
    }

    /// <summary>
    /// Quantises one 8-bit component into <paramref name="colorValues"/> at <paramref name="index"/>.
    /// </summary>
    private static void QuantizeInto(int colorRange, Span<int> colorValues, int index, int value)
        => colorValues[index] = Quantization.QuantizeCEValueToRange(Math.Clamp(value, 0, MaxChannel), colorRange);

    /// <summary>
    /// Encodes a luma base+offset pair (mode 1): <c>v0</c> carries the low six bits of the base
    /// luma, <c>v1</c> the top two base bits plus a 6-bit non-negative offset (spec §C.2.14).
    /// </summary>
    private static void EncodeLumaBaseOffset(int colorRange, Span<int> colorValues, int lumaLow, int lumaHigh)
    {
        int baseLuma = Math.Clamp(lumaLow, 0, MaxChannel);
        int offset = Math.Clamp(lumaHigh - baseLuma, 0, LumaOffsetMax);
        int v0 = (baseLuma & 0x3F) << 2;
        int v1 = (baseLuma & 0xC0) | offset;
        colorValues[0] = Quantization.QuantizeCEValueToRange(v0, colorRange);
        colorValues[1] = Quantization.QuantizeCEValueToRange(v1, colorRange);
    }

    /// <summary>
    /// Packs one (base, offset) channel pair for a base+offset mode into the two colour values at
    /// <paramref name="pairIndex"/>, inverting <see cref="BitOperations.TransferPrecision"/>: the
    /// decoder reads the pair as <c>TransferPrecision(values[2i+1], values[2i])</c>.
    /// </summary>
    private static void PackBaseOffset(int colorRange, Span<int> colorValues, int pairIndex, int low, int high)
    {
        int baseValue = Math.Clamp(low, 0, MaxChannel);
        int offset = Math.Clamp(high - baseValue, OffsetMin, OffsetMax);

        int valueB = (baseValue & 0x7F) << 1;
        int valueA = ((offset & OffsetMask) << 1) | (baseValue & 0x80);

        colorValues[pairIndex * 2] = Quantization.QuantizeCEValueToRange(valueB, colorRange);
        colorValues[(pairIndex * 2) + 1] = Quantization.QuantizeCEValueToRange(valueA, colorRange);
    }
}
