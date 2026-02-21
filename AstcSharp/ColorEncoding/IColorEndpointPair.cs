using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

/// <summary>
/// Base interface for color endpoint pairs, supporting both LDR and HDR modes.
/// </summary>
internal interface IColorEndpointPair
{
    /// <summary>
    /// Indicates whether this endpoint pair represents HDR content.
    /// </summary>
    bool IsHdr { get; }
}

/// <summary>
/// Represents a pair of LDR (Low Dynamic Range) color endpoints using byte precision (0-255).
/// </summary>
internal record LdrEndpointPair(RgbaColor Low, RgbaColor High) : IColorEndpointPair
{
    public bool IsHdr => false;
}

/// <summary>
/// Represents a pair of HDR (High Dynamic Range) color endpoints using ushort precision (0-65535).
/// Values are in LNS (Log-Normalized Space) for decoded endpoints, or FP16 bit patterns for
/// void extent blocks. <see cref="AlphaIsLdr"/> indicates Mode 14 (UNORM16 alpha).
/// <see cref="ValuesAreLns"/> indicates whether LNS-to-FP16 conversion is needed after interpolation.
/// </summary>
internal record HdrEndpointPair(RgbaHdrColor Low, RgbaHdrColor High, bool AlphaIsLdr = false, bool ValuesAreLns = true) : IColorEndpointPair
{
    public bool IsHdr => true;
}
