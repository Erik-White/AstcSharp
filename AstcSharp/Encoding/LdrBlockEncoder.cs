using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes an LDR block by driving the colour-space-agnostic <see cref="BlockEncoderCore"/> with the
/// <see cref="LdrColorStrategy"/> (byte channels, LDR endpoint modes, and the decoder's LDR
/// interpolation). See <see cref="BlockEncoderCore"/> for the search it performs.
/// </summary>
internal static class LdrBlockEncoder
{
    /// <summary>
    /// Encodes <paramref name="texels"/> (one <see cref="RgbaColor"/> per footprint texel, raster
    /// order) into a 128-bit block, returning whichever encoding reconstructs the block with the
    /// lowest error.
    /// </summary>
    public static UInt128 Encode(ReadOnlySpan<RgbaColor> texels, Footprint footprint)
        => BlockEncoderCore.Encode<RgbaColor, LdrColorStrategy>(texels, footprint);
}
