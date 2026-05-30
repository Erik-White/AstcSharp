namespace AstcSharp;

/// <summary>
/// Selects how the LDR decode path expands and outputs color (ASTC spec §C.2.19, §C.2.5).
/// </summary>
public enum LdrDecodeMode
{
    /// <summary>
    /// Linear decode. Each 8-bit endpoint component is bit-replicated to 16 bits
    /// (<c>(C &lt;&lt; 8) | C</c>) before interpolation.
    /// </summary>
    Linear,

    /// <summary>
    /// sRGB decode, matching the <c>COMPRESSED_SRGB8_ALPHA8_ASTC_*</c> formats. The R, G, and B
    /// endpoint components expand as <c>(C &lt;&lt; 8) | 0x80</c> instead of bit-replication;
    /// alpha is unchanged. The decoder still returns the sRGB-encoded 8-bit values — it does not
    /// apply an sRGB-to-linear transform.
    /// </summary>
    Srgb,
}
