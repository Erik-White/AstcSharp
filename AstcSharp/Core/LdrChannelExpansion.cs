namespace AstcSharp.Core;

/// <summary>
/// Strategy for expanding an 8-bit LDR endpoint component to the 16-bit value that ASTC spec
/// §C.2.19 (Weight Application) interpolates. The two modes differ only in the low byte:
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
/// in linear decode mode, and always for the alpha channel regardless of mode.
/// </summary>
internal readonly struct LinearExpand : IChannelExpand
{
    public static int Expand(int c) => (c << 8) | c;
}

/// <summary>
/// sRGB LDR expansion (ASTC spec §C.2.19): <c>(C &lt;&lt; 8) | 0x80</c>. Used for the R, G, and B
/// channels in sRGB decode mode; alpha still uses <see cref="LinearExpand"/>.
/// </summary>
internal readonly struct SrgbExpand : IChannelExpand
{
    public static int Expand(int c) => (c << 8) | 0x80;
}
