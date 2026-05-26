using System.Runtime.CompilerServices;
using AstcSharp.Core;

namespace AstcSharp.ColorEncoding;

/// <summary>
/// ASTC-specific extension methods and helpers for <see cref="RgbaHdrColor"/>.
/// </summary>
internal static class RgbaHdrColorExtensions
{
    /// <summary>
    /// Gets the channel value at the specified index: 0=R, 1=G, 2=B, 3=A.
    /// </summary>
    /// <remarks>
    /// Reads the sequential [R, G, B, A] ushort layout of <see cref="RgbaHdrColor"/> directly.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort GetChannel(this in RgbaHdrColor color, int i)
    {
        if ((uint)i >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(i), $"Index must be between 0 and 3. Actual value: {i}.");
        }

        return Unsafe.Add(ref Unsafe.As<RgbaHdrColor, ushort>(ref Unsafe.AsRef(in color)), i);
    }

    /// <summary>
    /// Returns true if all four channels are within the specified tolerance of the other color.
    /// </summary>
    public static bool IsCloseTo(this RgbaHdrColor color, RgbaHdrColor other, int tolerance)
        => Math.Abs(color.R - other.R) <= tolerance &&
           Math.Abs(color.G - other.G) <= tolerance &&
           Math.Abs(color.B - other.B) <= tolerance &&
           Math.Abs(color.A - other.A) <= tolerance;
}
