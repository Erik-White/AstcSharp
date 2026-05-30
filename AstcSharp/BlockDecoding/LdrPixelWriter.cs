using System.Runtime.CompilerServices;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.BlockDecoding;

/// <summary>
/// LDR <see cref="IPixelWriter{T}"/> — writes UNORM8 RGBA bytes via the scalar SIMD helpers.
/// <typeparamref name="TExpand"/> selects linear vs sRGB channel expansion (ASTC spec §C.2.19).
/// </summary>
internal readonly struct LdrPixelWriter<TExpand> : IPixelWriter<byte>
    where TExpand : struct, IChannelExpand
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePixel(Span<byte> buffer, int offset, in ColorEndpointPair endpoint, int weight)
        => SimdHelpers.WriteSinglePixelLdr<TExpand>(
            buffer,
            offset,
            endpoint.LdrLow.R,
            endpoint.LdrLow.G,
            endpoint.LdrLow.B,
            endpoint.LdrLow.A,
            endpoint.LdrHigh.R,
            endpoint.LdrHigh.G,
            endpoint.LdrHigh.B,
            endpoint.LdrHigh.A,
            weight);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePixelDualPlane(
        Span<byte> buffer,
        int offset,
        in ColorEndpointPair endpoint,
        int primaryWeight,
        int dualPlaneChannel,
        int dualPlaneWeight)
        => SimdHelpers.WriteSinglePixelLdrDualPlane<TExpand>(
            buffer,
            offset,
            endpoint.LdrLow.R,
            endpoint.LdrLow.G,
            endpoint.LdrLow.B,
            endpoint.LdrLow.A,
            endpoint.LdrHigh.R,
            endpoint.LdrHigh.G,
            endpoint.LdrHigh.B,
            endpoint.LdrHigh.A,
            primaryWeight,
            dualPlaneChannel,
            dualPlaneWeight);
}
