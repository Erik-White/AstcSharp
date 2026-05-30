using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace AstcSharp.Core;

internal static class SimdHelpers
{
    private static readonly Vector128<int> Vec32 = Vector128.Create(32);
    private static readonly Vector128<int> Vec64 = Vector128.Create(64);
    private static readonly Vector128<int> Vec255 = Vector128.Create(255);

    /// <summary>
    /// Interpolates one channel for 4 pixels simultaneously, expanding the 8-bit endpoints to
    /// 16 bits via <typeparamref name="TExpand"/> (ASTC spec §C.2.19). All 4 pixels share the
    /// same endpoint values but have different weights. Returns 4 byte results packed into the
    /// lower bytes of a <see cref="Vector128{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> Interpolate4ChannelPixels<TExpand>(int p0, int p1, Vector128<int> weights)
        where TExpand : struct, IChannelExpand
    {
        // Expand endpoint bytes to 16-bit per the selected mode (§C.2.19).
        Vector128<int> c0 = Vector128.Create(TExpand.Expand(p0));
        Vector128<int> c1 = Vector128.Create(TExpand.Expand(p1));

        // c = (c0 * (64 - w) + c1 * w + 32) >> 6
        // NOTE: Using >> 6 instead of / 64 because Vector128<int> division
        // has no hardware support and decomposes to scalar operations.
        Vector128<int> w64 = Vec64 - weights;
        Vector128<int> c = ((c0 * w64) + (c1 * weights) + Vec32) >> 6;

        // Spec §C.2.19 (Weight Application): for LDR-mode UNORM8 output the final
        // 8-bit result is the top 8 bits of the UNORM16 interpolation. Mask
        // to [0, 255] to defend against malformed endpoints producing c outside
        // [0, 0xFFFF]; well-formed input is already in range.
        return (c >>> 8) & Vec255;
    }

    /// <summary>
    /// Writes 4 LDR pixels directly to output buffer using SIMD. R, G, and B expand via
    /// <typeparamref name="TExpand"/>; alpha always uses linear bit-replication (ASTC spec
    /// §C.2.19). Processes each channel across 4 pixels in parallel, then interleaves to RGBA output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write4PixelLdr<TExpand>(
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
        where TExpand : struct, IChannelExpand
    {
        Vector128<int> r = Interpolate4ChannelPixels<TExpand>(lowR, highR, weights);
        Vector128<int> g = Interpolate4ChannelPixels<TExpand>(lowG, highG, weights);
        Vector128<int> b = Interpolate4ChannelPixels<TExpand>(lowB, highB, weights);
        Vector128<int> a = Interpolate4ChannelPixels<LinearExpand>(lowA, highA, weights);

        // Pack 4 RGBA pixels into 16 bytes via vector OR+shift.
        // Each int element has its channel value in bits [0:7].
        // Combine: element[i] = R[i] | (G[i] << 8) | (B[i] << 16) | (A[i] << 24)
        // On little-endian, storing this int32 writes bytes [R, G, B, A].
        Vector128<int> rgba = r | (g << 8) | (b << 16) | (a << 24);
        rgba.AsByte().CopyTo(output.Slice(offset, 16));
    }

    /// <summary>
    /// Scalar single-pixel LDR interpolation, writing directly to buffer. R, G, and B expand via
    /// <typeparamref name="TExpand"/>; alpha always uses linear bit-replication (ASTC spec §C.2.19).
    /// No RgbaColor allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSinglePixelLdr<TExpand>(
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
        where TExpand : struct, IChannelExpand
    {
        output[offset + 0] = (byte)InterpolateChannelScalar<TExpand>(lowR, highR, weight);
        output[offset + 1] = (byte)InterpolateChannelScalar<TExpand>(lowG, highG, weight);
        output[offset + 2] = (byte)InterpolateChannelScalar<TExpand>(lowB, highB, weight);
        output[offset + 3] = (byte)InterpolateChannelScalar<LinearExpand>(lowA, highA, weight);
    }

    /// <summary>
    /// Scalar single-pixel dual-plane LDR interpolation, writing directly to buffer. R, G, and B
    /// expand via <typeparamref name="TExpand"/>; alpha always uses linear bit-replication
    /// (ASTC spec §C.2.19).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSinglePixelLdrDualPlane<TExpand>(
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
        where TExpand : struct, IChannelExpand
    {
        output[offset + 0] = (byte)InterpolateChannelScalar<TExpand>(
            lowR,
            highR,
            dpChannel == 0 ? dpWeight : weight);
        output[offset + 1] = (byte)InterpolateChannelScalar<TExpand>(
            lowG,
            highG,
            dpChannel == 1 ? dpWeight : weight);
        output[offset + 2] = (byte)InterpolateChannelScalar<TExpand>(
            lowB,
            highB,
            dpChannel == 2 ? dpWeight : weight);
        output[offset + 3] = (byte)InterpolateChannelScalar<LinearExpand>(
            lowA,
            highA,
            dpChannel == 3 ? dpWeight : weight);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int InterpolateChannelScalar<TExpand>(int p0, int p1, int weight)
        where TExpand : struct, IChannelExpand
    {
        // Spec §C.2.19 (Weight Application): for LDR-mode UNORM8 output the final
        // 8-bit result is the top 8 bits of the UNORM16 interpolation.
        int c = Interpolation.BlendExpanded<TExpand>(p0, p1, weight);
        return (c >> 8) & 0xFF;
    }
}
