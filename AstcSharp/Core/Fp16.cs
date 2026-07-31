using System.Runtime.CompilerServices;

namespace AstcSharp.Core;

/// <summary>
/// IEEE 754 half-precision (FP16) constants and helpers used by the HDR decoder and encoder.
/// </summary>
internal static class Fp16
{
    /// <summary>
    /// The 16-bit HDR endpoint-domain value for 1.0: the 12-bit intermediate <c>0x780</c> (the spec's
    /// "1.0", §C.2.14) shifted left by 4.
    /// </summary>
    public const ushort One = 0x7800;

    /// <summary>FP16 bit pattern for the largest finite value (sign 0, exponent 30, mantissa all ones).</summary>
    public const ushort MaxFinite = 0x7BFF;

    /// <summary>
    /// Converts a 16-bit LNS (Log-Normalized Space) value to a 16-bit SF16 (FP16) bit pattern
    /// per ASTC spec §C.2.15.
    /// </summary>
    /// <remarks>
    /// The LNS value encodes a 5-bit exponent in the upper bits and an 11-bit mantissa
    /// in the lower bits. The piecewise-linear mantissa transform (slope 3 / 4 / 5 across
    /// the [0, 512), [512, 1536), [1536, 2048) intervals) and the +Inf/NaN clamp to
    /// <see cref="MaxFinite"/> are taken verbatim from §C.2.15.
    /// </remarks>
    public static ushort FromLns(int lns)
    {
        int mantissaComponent = lns & 0x7FF;       // Lower 11 bits: mantissa component
        int exponentComponent = (lns >> 11) & 0x1F; // Upper 5 bits: exponent component

        // Spec §C.2.15: piecewise-linear log approximation, inflection at M = 512 and M = 1536.
        int mantissaTransformed;
        if (mantissaComponent < 512)
        {
            mantissaTransformed = mantissaComponent * 3;
        }
        else if (mantissaComponent < 1536)
        {
            mantissaTransformed = (mantissaComponent * 4) - 512;
        }
        else
        {
            mantissaTransformed = (mantissaComponent * 5) - 2048;
        }

        int result = (exponentComponent << 10) | (mantissaTransformed >> 3);
        return (ushort)Math.Min(result, MaxFinite);
    }

    /// <summary>
    /// Maps a non-negative finite FP16 bit pattern back to a 16-bit LNS value — a right inverse of
    /// <see cref="FromLns"/>: <c>FromLns(ToLns(y)) == y</c> for every <paramref name="fp16Bits"/> in
    /// [0, <see cref="MaxFinite"/>]. Negative, infinite, and NaN inputs are clamped into that range
    /// first, so the result is always a valid LNS value.
    /// </summary>
    /// <remarks>
    /// <see cref="FromLns"/> is many-to-one (its <c>&gt;&gt; 3</c> discards the low mantissa bits), so
    /// no exact two-sided inverse exists; this returns the representative LNS value whose forward
    /// transform reproduces the FP16 pattern exactly. The FP16 exponent maps straight to the LNS
    /// exponent; the 10-bit FP16 mantissa is lifted back through whichever of the three linear
    /// mantissa pieces (slope 3 / 4 / 5, spec §C.2.15) covers it, by ceiling division so the forward
    /// <c>&gt;&gt; 3</c> lands in the intended bucket.
    /// </remarks>
    public static int ToLns(ushort fp16Bits)
    {
        // Sanitise out-of-domain inputs to the [0, MaxFinite] range the inverse is defined on.
        bool negative = (fp16Bits & 0x8000) != 0;
        if (negative)
        {
            return 0;
        }

        if (fp16Bits > MaxFinite)
        {
            // +Inf / NaN: the largest finite magnitude.
            fp16Bits = MaxFinite;
        }

        int exponentComponent = (fp16Bits >> 10) & 0x1F;
        int mantissaFp16 = fp16Bits & 0x3FF;

        // The forward pieces switch at mantissa components 512 and 1536; carried through the forward
        // transform and >> 3, those map to these FP16-mantissa boundaries.
        const int firstPieceEnd = 192;   // FromLns M=512  → (512*3) >> 3
        const int secondPieceEnd = 704;  // FromLns M=1536 → (1536*4 - 512) >> 3

        // The forward transform quantises the mantissa as (transformed >> 3), so invert to the
        // smallest mantissa component whose transform floors back to mantissaFp16: undo the >> 3, undo
        // the piece's affine map, and round up so the forward floor lands in this bucket.
        int transformedLow = mantissaFp16 << 3;
        int mantissaComponent;
        if (mantissaFp16 < firstPieceEnd)
        {
            mantissaComponent = (transformedLow + 2) / 3;
        }
        else if (mantissaFp16 < secondPieceEnd)
        {
            mantissaComponent = (transformedLow + 512 + 3) / 4;
        }
        else
        {
            mantissaComponent = (transformedLow + 2048 + 4) / 5;
        }

        return (exponentComponent << 11) | mantissaComponent;
    }

    /// <summary>
    /// Decodes a 16-bit LNS value to a single-precision float by converting through FP16,
    /// per ASTC spec §C.2.15. The LNS value is passed through <see cref="FromLns"/>, reinterpreted
    /// as FP16 bits, and widened to <see cref="float"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LnsToFloat(int lns) => (float)BitConverter.UInt16BitsToHalf(FromLns(lns));

    /// <summary>
    /// Widens an FP16 bit pattern (already in SF16 form, no LNS conversion) to <see cref="float"/>.
    /// Used for HDR void-extent blocks (ASTC spec §C.2.23), whose channel values are stored as
    /// FP16 bit patterns directly rather than as LNS values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Fp16ToFloat(ushort fp16Bits) => (float)BitConverter.UInt16BitsToHalf(fp16Bits);
}
