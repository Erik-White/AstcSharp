namespace AstcSharp.Core;

/// <summary>
/// Represents an HDR (High Dynamic Range) color with 16-bit per-channel precision.
/// </summary>
/// <remarks>
/// HDR colors use ushort values (0-65535) for each channel, allowing representation
/// of values beyond the standard 0-255 LDR range. This enables encoding of High Dynamic
/// Range content that can represent brightness values exceeding the typical white point.
/// </remarks>
internal record RgbaHdrColor
{
    public static int BytesPerPixel => 8; // 4 channels × 2 bytes per ushort

    public ushort R { get; }
    public ushort G { get; }
    public ushort B { get; }
    public ushort A { get; }

    public RgbaHdrColor(ushort r, ushort g, ushort b, ushort a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>
    /// Indexer to access channels by index: 0=R, 1=G, 2=B, 3=A
    /// </summary>
    public ushort this[int i] => i switch
    {
        0 => R,
        1 => G,
        2 => B,
        3 => A,
        _ => throw new ArgumentOutOfRangeException(nameof(i), $"Index must be between 0 and 3. Actual value: {i}.")
    };

    public static RgbaHdrColor Empty => new(0, 0, 0, 0);

    /// <summary>
    /// Converts an LDR color (0-255) to HDR range (0-65535).
    /// </summary>
    public static RgbaHdrColor FromRgba(RgbaColor ldr)
        => new((ushort)(ldr.R * 257), (ushort)(ldr.G * 257), (ushort)(ldr.B * 257), (ushort)(ldr.A * 257));

    /// <summary>
    /// Converts an HDR color (0-65535) to LDR range (0-255).
    /// </summary>
    /// <remarks>
    /// Values are clamped to 0-255 range, so HDR values exceeding
    /// the standard white point will be clipped.
    /// </remarks>
    public RgbaColor ToLowDynamicRange()
        => new((byte)(R >> 8), (byte)(G >> 8), (byte)(B >> 8), (byte)(A >> 8));

    /// <summary>
    /// Converts this HDR color to an array of Half values (FP16).
    /// </summary>
    /// <returns>Array of 4 Half values [R, G, B, A] normalized to 0.0-1.0+ range</returns>
    public Half[] ToHalfArray() =>
    [
        UshortToHalf(R),
        UshortToHalf(G),
        UshortToHalf(B),
        UshortToHalf(A)
    ];

    public bool IsCloseTo(RgbaHdrColor other, int tolerance)
        => Math.Abs(R - other.R) <= tolerance &&
           Math.Abs(G - other.G) <= tolerance &&
           Math.Abs(B - other.B) <= tolerance &&
           Math.Abs(A - other.A) <= tolerance;

    /// <summary>
    /// Creates an HDR color from an array of Half values (FP16).
    /// </summary>
    internal static RgbaHdrColor FromHalfArray(Half[] values)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, 4);

        return new RgbaHdrColor(
            HalfToUshort(values[0]),
            HalfToUshort(values[1]),
            HalfToUshort(values[2]),
            HalfToUshort(values[3])
        );
    }

    /// <summary>
    /// Converts HDR ushort value (0-65535) to Half (FP16) normalized to 0.0-1.0.
    /// </summary>
    internal static Half UshortToHalf(ushort value)
        => Half.CreateSaturating(value / 65535.0f);

    /// <summary>
    /// Converts a Half value (FP16) to HDR ushort (0-65535).
    /// </summary>
    internal static ushort HalfToUshort(Half value)
    {
        float normalized = Math.Clamp((float)value, 0.0f, 1.0f);
        return (ushort)(normalized * 65535.0f);
    }
}
