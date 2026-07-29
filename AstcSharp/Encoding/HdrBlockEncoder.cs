using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes an HDR block by driving the colour-space-agnostic <see cref="BlockEncoderCore"/> with the
/// <see cref="HdrColorStrategy"/>. Input texels hold FP16 bit patterns; this converts them to the
/// LNS (log) domain the decoder interpolates in (via <see cref="Fp16.ToLns"/>) before the search, so
/// endpoint fitting and error measurement happen in that domain.
/// </summary>
internal static class HdrBlockEncoder
{
    /// <summary>
    /// Encodes <paramref name="fp16Texels"/> (one <see cref="RgbaHdrColor"/> of FP16 bit patterns per
    /// footprint texel, raster order) into a 128-bit block, returning whichever encoding reconstructs
    /// the block with the lowest error.
    /// </summary>
    public static UInt128 Encode(ReadOnlySpan<RgbaHdrColor> fp16Texels, Footprint footprint)
    {
        Span<RgbaHdrColor> lnsTexels = stackalloc RgbaHdrColor[fp16Texels.Length];
        for (int i = 0; i < fp16Texels.Length; i++)
        {
            RgbaHdrColor texel = fp16Texels[i];
            lnsTexels[i] = new RgbaHdrColor(
                (ushort)Fp16.ToLns(texel.R),
                (ushort)Fp16.ToLns(texel.G),
                (ushort)Fp16.ToLns(texel.B),
                (ushort)Fp16.ToLns(texel.A));
        }

        return BlockEncoderCore.Encode<RgbaHdrColor, HdrColorStrategy>(lnsTexels, footprint);
    }
}
