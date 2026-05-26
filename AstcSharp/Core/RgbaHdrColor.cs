namespace AstcSharp.Core;

/// <summary>
/// HDR 64-bit RGBA pixel: four ushort channels in R, G, B, A order.
/// Holds either UNORM16 values (LDR void-extent) or FP16 bit patterns (HDR).
/// </summary>
internal readonly record struct RgbaHdrColor(ushort R, ushort G, ushort B, ushort A);
