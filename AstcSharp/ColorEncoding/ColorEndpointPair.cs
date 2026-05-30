using System.Runtime.InteropServices;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

/// <summary>
/// A value-type discriminated union representing either an LDR or HDR color endpoint pair.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct ColorEndpointPair
{
    public bool IsHdr { get; private init; }

    // LDR fields (used when IsHdr == false)
    public RgbaColor LdrLow { get; private init; }
    public RgbaColor LdrHigh { get; private init; }

    // HDR fields (used when IsHdr == true)
    public RgbaHdrColor HdrLow { get; private init; }
    public RgbaHdrColor HdrHigh { get; private init; }
    public bool AlphaIsLdr { get; private init; }
    public bool ValuesAreLns { get; private init; }

    public static ColorEndpointPair Ldr(RgbaColor low, RgbaColor high)
        => new() { IsHdr = false, LdrLow = low, LdrHigh = high };

    public static ColorEndpointPair Hdr(RgbaHdrColor low, RgbaHdrColor high, bool alphaIsLdr = false, bool valuesAreLns = true)
        => new() { IsHdr = true, HdrLow = low, HdrHigh = high, AlphaIsLdr = alphaIsLdr, ValuesAreLns = valuesAreLns };
}
