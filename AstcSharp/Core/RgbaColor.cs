namespace AstcSharp.Core;

/// <summary>
/// LDR 32-bit RGBA pixel: four byte channels in R, G, B, A order.
/// </summary>
internal readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Indexes the channels in R, G, B, A order (0..3), so callers can iterate channels without
    /// materialising a span of the four values.
    /// </summary>
    public byte this[int channel] => channel switch
    {
        0 => this.R,
        1 => this.G,
        2 => this.B,
        3 => this.A,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}
