using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace AstcSharp.Core;

internal static class SimdHelpers
{
    private static readonly Vector128<int> Vec32 = Vector128.Create(32);
    private static readonly Vector128<int> Vec64 = Vector128.Create(64);
    private static readonly Vector128<int> Vec255 = Vector128.Create(255);

    /// <summary>
    /// Interpolates one channel for 4 pixels simultaneously from two already-expanded 16-bit
    /// endpoints (ASTC spec §C.2.19). Callers apply the per-channel expansion (linear or sRGB) to
    /// <paramref name="c0"/>/<paramref name="c1"/> first. All 4 pixels share the same endpoints but
    /// have different weights. Returns 4 byte results packed into the lower bytes of a
    /// <see cref="Vector128{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Interpolate4ExpandedPixels(int c0, int c1, Vector128<int> weights)
    {
        Vector128<int> v0 = Vector128.Create(c0);
        Vector128<int> v1 = Vector128.Create(c1);

        // Vectorised form of the ASTC spec §C.2.19 weighted blend (see Interpolation.BlendWeighted).
        // NOTE: >> 6 rather than / 64 — Vector128<int> has no hardware division and decomposes to scalar ops.
        Vector128<int> w64 = Vec64 - weights;
        Vector128<int> c = ((v0 * w64) + (v1 * weights) + Vec32) >> 6;

        // Spec §C.2.19 (Weight Application): for LDR-mode UNORM8 output the final
        // 8-bit result is the top 8 bits of the UNORM16 interpolation. Mask
        // to [0, 255] to defend against malformed endpoints producing c outside
        // [0, 0xFFFF]; well-formed input is already in range.
        return (c >>> 8) & Vec255;
    }

    /// <summary>
    /// Writes 4 LDR pixels directly to output buffer using SIMD. R, G, and B expand via
    /// <typeparamref name="TMode"/>'s colour expansion; alpha via its alpha expansion (always
    /// linear — ASTC spec §C.2.19). Processes each channel across 4 pixels in parallel, then
    /// interleaves to RGBA output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write4PixelLdr<TMode>(
        Span<byte> output,
        int offset,
        int lowR,
        int lowG,
        int lowB,
        int lowA,
        int highR,
        int highG,
        int highB,
        int highA,
        Vector128<int> weights)
        where TMode : struct, ILdrColorMode
    {
        Vector128<int> r = Interpolate4ExpandedPixels(TMode.ExpandColor(lowR), TMode.ExpandColor(highR), weights);
        Vector128<int> g = Interpolate4ExpandedPixels(TMode.ExpandColor(lowG), TMode.ExpandColor(highG), weights);
        Vector128<int> b = Interpolate4ExpandedPixels(TMode.ExpandColor(lowB), TMode.ExpandColor(highB), weights);
        Vector128<int> a = Interpolate4ExpandedPixels(TMode.ExpandAlpha(lowA), TMode.ExpandAlpha(highA), weights);

        // Pack 4 RGBA pixels into 16 bytes via vector OR+shift.
        // Each int element has its channel value in bits [0:7].
        // Combine: element[i] = R[i] | (G[i] << 8) | (B[i] << 16) | (A[i] << 24)
        // On little-endian, storing this int32 writes bytes [R, G, B, A].
        Vector128<int> rgba = r | (g << 8) | (b << 16) | (a << 24);
        rgba.AsByte().CopyTo(output.Slice(offset, 16));
    }

    /// <summary>
    /// Scalar single-pixel LDR interpolation, writing directly to buffer. R, G, and B expand via
    /// <typeparamref name="TMode"/>'s colour expansion; alpha via its alpha expansion (always
    /// linear — ASTC spec §C.2.19). No RgbaColor allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSinglePixelLdr<TMode>(
        Span<byte> output,
        int offset,
        int lowR,
        int lowG,
        int lowB,
        int lowA,
        int highR,
        int highG,
        int highB,
        int highA,
        int weight)
        where TMode : struct, ILdrColorMode
    {
        output[offset + 0] = (byte)InterpolateColorScalar<TMode>(lowR, highR, weight);
        output[offset + 1] = (byte)InterpolateColorScalar<TMode>(lowG, highG, weight);
        output[offset + 2] = (byte)InterpolateColorScalar<TMode>(lowB, highB, weight);
        output[offset + 3] = (byte)InterpolateAlphaScalar<TMode>(lowA, highA, weight);
    }

    /// <summary>
    /// Scalar single-pixel dual-plane LDR interpolation, writing directly to buffer. R, G, and B
    /// expand via <typeparamref name="TMode"/>'s colour expansion; alpha via its alpha expansion
    /// (always linear — ASTC spec §C.2.19).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSinglePixelLdrDualPlane<TMode>(
        Span<byte> output,
        int offset,
        int lowR,
        int lowG,
        int lowB,
        int lowA,
        int highR,
        int highG,
        int highB,
        int highA,
        int weight,
        int dpChannel,
        int dpWeight)
        where TMode : struct, ILdrColorMode
    {
        output[offset + 0] = (byte)InterpolateColorScalar<TMode>(
            lowR,
            highR,
            dpChannel == 0 ? dpWeight : weight);
        output[offset + 1] = (byte)InterpolateColorScalar<TMode>(
            lowG,
            highG,
            dpChannel == 1 ? dpWeight : weight);
        output[offset + 2] = (byte)InterpolateColorScalar<TMode>(
            lowB,
            highB,
            dpChannel == 2 ? dpWeight : weight);
        output[offset + 3] = (byte)InterpolateAlphaScalar<TMode>(
            lowA,
            highA,
            dpChannel == 3 ? dpWeight : weight);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InterpolateColorScalar<TMode>(int p0, int p1, int weight)
        where TMode : struct, ILdrColorMode
        => TopByte(Interpolation.BlendWeighted(TMode.ExpandColor(p0), TMode.ExpandColor(p1), weight));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InterpolateAlphaScalar<TMode>(int p0, int p1, int weight)
        where TMode : struct, ILdrColorMode
        => TopByte(Interpolation.BlendWeighted(TMode.ExpandAlpha(p0), TMode.ExpandAlpha(p1), weight));

    /// <summary>
    /// Spec §C.2.19 (Weight Application): for LDR-mode UNORM8 output the final 8-bit result is the
    /// top 8 bits of the UNORM16 interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TopByte(int unorm16) => (unorm16 >> 8) & 0xFF;
}
