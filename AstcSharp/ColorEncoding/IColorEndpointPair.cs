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
/// </summary>
internal record HdrEndpointPair(RgbaHdrColor Low, RgbaHdrColor High) : IColorEndpointPair
{
    public bool IsHdr => true;
}
