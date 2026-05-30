namespace AstcSharp.Core;

/// <summary>
/// Strategy for expanding an 8-bit LDR endpoint component to the 16-bit value that ASTC spec
/// §C.2.19 (Weight Application) interpolates. The two expansions differ only in the low byte:
/// linear bit-replicates the component, sRGB substitutes a fixed <c>0x80</c>. Implemented as a
/// generic strategy (static abstract member) so the JIT specialises and inlines the chosen
/// expansion with no per-pixel branch — the <see cref="LinearExpand"/> specialisation compiles
/// to the same code as a hard-coded replication.
/// </summary>
internal interface IChannelExpand
{
    /// <summary>Expands an 8-bit component <paramref name="c"/> to its 16-bit form.</summary>
    static abstract int Expand(int c);
}

/// <summary>
/// Linear LDR expansion (ASTC spec §C.2.19): <c>(C &lt;&lt; 8) | C</c>. Used for every channel
/// in linear decode mode, for the alpha channel in every mode, and for LDR channels borrowed by
/// the HDR output path.
/// </summary>
internal readonly struct LinearExpand : IChannelExpand
{
    public static int Expand(int c) => (c << 8) | c;
}

/// <summary>
/// sRGB LDR expansion (ASTC spec §C.2.19): <c>(C &lt;&lt; 8) | 0x80</c>. Used for the R, G, and B
/// channels in sRGB decode mode only.
/// </summary>
internal readonly struct SrgbExpand : IChannelExpand
{
    public static int Expand(int c) => (c << 8) | 0x80;
}

/// <summary>
/// An LDR decode mode's per-channel endpoint expansion (ASTC spec §C.2.19). Bundling the colour
/// and alpha expansions into one type keeps the "alpha is always linear, even in sRGB" rule in a
/// single place: pixel writers call <see cref="ExpandColor"/> for R/G/B and <see cref="ExpandAlpha"/>
/// for A, so the asymmetry can't be lost by threading one expansion across all four channels.
/// Implemented as a generic strategy so the JIT monomorphises each mode with no per-pixel branch.
/// </summary>
internal interface ILdrColorMode
{
    /// <summary>Expands an 8-bit R, G, or B endpoint component to 16 bits.</summary>
    static abstract int ExpandColor(int c);

    /// <summary>Expands an 8-bit alpha endpoint component to 16 bits.</summary>
    static abstract int ExpandAlpha(int c);
}

/// <summary>Linear LDR decode mode: every channel uses <see cref="LinearExpand"/>.</summary>
internal readonly struct LinearMode : ILdrColorMode
{
    public static int ExpandColor(int c) => LinearExpand.Expand(c);

    public static int ExpandAlpha(int c) => LinearExpand.Expand(c);
}

/// <summary>
/// sRGB LDR decode mode: R, G, B use <see cref="SrgbExpand"/>; alpha stays
/// <see cref="LinearExpand"/> (ASTC spec §C.2.19 — only the colour channels take the sRGB low byte).
/// </summary>
internal readonly struct SrgbMode : ILdrColorMode
{
    public static int ExpandColor(int c) => SrgbExpand.Expand(c);

    public static int ExpandAlpha(int c) => LinearExpand.Expand(c);
}
