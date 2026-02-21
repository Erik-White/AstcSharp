using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace AstcSharp.Core;

internal static class SimdHelpers
{
    private static readonly Vector128<int> Vec32 = Vector128.Create(32);
    private static readonly Vector128<int> Vec64 = Vector128.Create(64);
    private static readonly Vector128<int> Vec255 = Vector128.Create(255);
    private static readonly Vector128<int> Vec32767 = Vector128.Create(32767);

    /// <summary>
    /// Interpolates one channel for 4 pixels simultaneously.
    /// All 4 pixels share the same endpoint values but have different weights.
    /// Returns 4 byte results packed into the lower bytes of a Vector128&lt;int&gt;.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> InterpolateChannel4Pixels(int p0, int p1, Vector128<int> weights)
    {
        // Bit-replicate endpoint bytes to 16-bit
        var c0 = Vector128.Create((p0 << 8) | p0);
        var c1 = Vector128.Create((p1 << 8) | p1);

        // c = (c0 * (64 - w) + c1 * w + 32) >> 6
        // NOTE: Using >> 6 instead of / 64 because Vector128<int> division
        // has no hardware support and decomposes to scalar operations.
        var w64 = Vec64 - weights;
        var c = (c0 * w64 + c1 * weights + Vec32) >> 6;

        // Quantize: (c * 255 + 32767) >> 16, clamped to [0, 255]
        var result = (c * Vec255 + Vec32767) >>> 16;
        return Vector128.Min(Vector128.Max(result, Vector128<int>.Zero), Vec255);
    }

    /// <summary>
    /// Writes 4 LDR pixels directly to output buffer using SIMD.
    /// Processes each channel across 4 pixels in parallel, then interleaves to RGBA output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixels4Ldr(
        Span<byte> output, int offset,
        int lowR, int lowG, int lowB, int lowA,
        int highR, int highG, int highB, int highA,
        Vector128<int> weights)
    {
        var r = InterpolateChannel4Pixels(lowR, highR, weights);
        var g = InterpolateChannel4Pixels(lowG, highG, weights);
        var b = InterpolateChannel4Pixels(lowB, highB, weights);
        var a = InterpolateChannel4Pixels(lowA, highA, weights);

        // Interleave RGBA and write 16 bytes (4 pixels × 4 bytes)
        // r = [R0, R1, R2, R3], g = [G0, G1, G2, G3], etc. as int vectors
        // We need: [R0,G0,B0,A0, R1,G1,B1,A1, R2,G2,B2,A2, R3,G3,B3,A3]

        // Narrow int32 → byte via truncation (values are already 0-255)
        var rb = Vector128.Narrow(r.AsInt16(), g.AsInt16()); // interleaves shorts
        var ba = Vector128.Narrow(b.AsInt16(), a.AsInt16());

        // At this point we have bytes but in wrong order. Use scalar write which
        // the JIT will optimize - the real gain is avoiding per-pixel method calls
        // and heap allocations, not the final byte write.
        output[offset +  0] = (byte)r.GetElement(0);
        output[offset +  1] = (byte)g.GetElement(0);
        output[offset +  2] = (byte)b.GetElement(0);
        output[offset +  3] = (byte)a.GetElement(0);
        output[offset +  4] = (byte)r.GetElement(1);
        output[offset +  5] = (byte)g.GetElement(1);
        output[offset +  6] = (byte)b.GetElement(1);
        output[offset +  7] = (byte)a.GetElement(1);
        output[offset +  8] = (byte)r.GetElement(2);
        output[offset +  9] = (byte)g.GetElement(2);
        output[offset + 10] = (byte)b.GetElement(2);
        output[offset + 11] = (byte)a.GetElement(2);
        output[offset + 12] = (byte)r.GetElement(3);
        output[offset + 13] = (byte)g.GetElement(3);
        output[offset + 14] = (byte)b.GetElement(3);
        output[offset + 15] = (byte)a.GetElement(3);
    }

    /// <summary>
    /// Scalar single-pixel LDR interpolation, writing directly to buffer.
    /// No RgbaColor allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixel1Ldr(
        Span<byte> output, int offset,
        int lowR, int lowG, int lowB, int lowA,
        int highR, int highG, int highB, int highA,
        int weight)
    {
        output[offset + 0] = (byte)InterpolateChannelScalar(lowR, highR, weight);
        output[offset + 1] = (byte)InterpolateChannelScalar(lowG, highG, weight);
        output[offset + 2] = (byte)InterpolateChannelScalar(lowB, highB, weight);
        output[offset + 3] = (byte)InterpolateChannelScalar(lowA, highA, weight);
    }

    /// <summary>
    /// Scalar single-pixel dual-plane LDR interpolation, writing directly to buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixel1LdrDualPlane(
        Span<byte> output, int offset,
        int lowR, int lowG, int lowB, int lowA,
        int highR, int highG, int highB, int highA,
        int weight, int dpChannel, int dpWeight)
    {
        output[offset + 0] = (byte)InterpolateChannelScalar(lowR, highR, dpChannel == 0 ? dpWeight : weight);
        output[offset + 1] = (byte)InterpolateChannelScalar(lowG, highG, dpChannel == 1 ? dpWeight : weight);
        output[offset + 2] = (byte)InterpolateChannelScalar(lowB, highB, dpChannel == 2 ? dpWeight : weight);
        output[offset + 3] = (byte)InterpolateChannelScalar(lowA, highA, dpChannel == 3 ? dpWeight : weight);
    }

    /// <summary>
    /// Vectorized bilinear weight computation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BilinearWeight(int w0, int w1, int w2, int w3, int f0, int f1, int f2, int f3)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            var wv = Vector128.Create(w0, w1, w2, w3);
            var fv = Vector128.Create(f0, f1, f2, f3);
            return (Vector128.Sum(wv * fv) + 8) >> 4;
        }

        return (w0 * f0 + w1 * f1 + w2 * f2 + w3 * f3 + 8) >> 4;
    }

    // Keep the old API for ColorAt() (used by tests and non-hot paths)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RgbaColor InterpolateColorLdr(RgbaColor low, RgbaColor high, int weight)
    {
        return new RgbaColor(
            r: InterpolateChannelScalar(low.R, high.R, weight),
            g: InterpolateChannelScalar(low.G, high.G, weight),
            b: InterpolateChannelScalar(low.B, high.B, weight),
            a: InterpolateChannelScalar(low.A, high.A, weight));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RgbaColor InterpolateColorLdrDualPlane(
        RgbaColor low, RgbaColor high,
        int weight, int dualPlaneChannel, int dualPlaneWeight)
    {
        return new RgbaColor(
            r: InterpolateChannelScalar(low.R, high.R, dualPlaneChannel == 0 ? dualPlaneWeight : weight),
            g: InterpolateChannelScalar(low.G, high.G, dualPlaneChannel == 1 ? dualPlaneWeight : weight),
            b: InterpolateChannelScalar(low.B, high.B, dualPlaneChannel == 2 ? dualPlaneWeight : weight),
            a: InterpolateChannelScalar(low.A, high.A, dualPlaneChannel == 3 ? dualPlaneWeight : weight));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int InterpolateChannelScalar(int p0, int p1, int weight)
    {
        int c0 = (p0 << 8) | p0;
        int c1 = (p1 << 8) | p1;
        int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
        int quantized = ((c * 255) + 32767) / 65536;
        return Math.Clamp(quantized, 0, 255);
    }
}
