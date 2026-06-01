namespace AstcSharp.Core;

/// <summary>
/// LDR 32-bit RGBA pixel: four byte channels in R, G, B, A order.
/// </summary>
internal readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public const int BytesPerPixel = 4;
}
