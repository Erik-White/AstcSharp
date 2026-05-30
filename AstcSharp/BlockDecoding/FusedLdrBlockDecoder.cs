using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.BlockDecoding;

/// <summary>
/// LDR pixel writers and entry points for the fused decode pipeline.
/// All methods handle single-partition, non-dual-plane blocks.
/// </summary>
internal static class FusedLdrBlockDecoder
{
    private const int SimdLaneCount = 4;

    /// <summary>
    /// Fused LDR decode to a contiguous buffer.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// <typeparamref name="TMode"/> selects linear vs sRGB decode (ASTC spec §C.2.19).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedLdr<TMode>(UInt128 bits, in BlockInfo info, Footprint footprint, Span<byte> buffer)
        where TMode : struct, ILdrColorMode
        => DecompressBlock<TMode>(
            bits,
            in info,
            footprint,
            buffer,
            dstBaseX: 0,
            dstBaseY: 0,
            dstRowStride: footprint.Width * BlockInfo.ChannelsPerPixel);

    /// <summary>
    /// Fused LDR decode writing directly to image buffer at strided positions.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// <typeparamref name="TMode"/> selects linear vs sRGB decode (ASTC spec §C.2.19).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedLdrToImage<TMode>(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        Span<byte> imageBuffer)
        where TMode : struct, ILdrColorMode
        => DecompressBlock<TMode>(
            bits,
            in info,
            footprint,
            imageBuffer,
            dstBaseX,
            dstBaseY,
            dstRowStride: imageWidth * BlockInfo.ChannelsPerPixel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecompressBlock<TMode>(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        Span<byte> buffer,
        int dstBaseX,
        int dstBaseY,
        int dstRowStride)
        where TMode : struct, ILdrColorMode
    {
        // Up to 12×12 = 144 ints (576 bytes) for the largest 2D footprint per spec §C.2.4.
        Span<int> texelWeights = stackalloc int[footprint.PixelCount];
        ColorEndpointPair endpointPair = FusedBlockDecoder.DecodeFusedCore(bits, in info, footprint, texelWeights);
        WriteLdrPixels<TMode>(buffer, footprint, dstBaseX, dstBaseY, dstRowStride, in endpointPair, texelWeights);
    }

    /// <summary>
    /// Writes a footprint-sized block of LDR pixels into <paramref name="buffer"/> at position
    /// (<paramref name="dstBaseX"/>, <paramref name="dstBaseY"/>) with the given row stride.
    /// Uses SIMD where hardware-accelerated; scalar otherwise. <typeparamref name="TMode"/>
    /// selects linear vs sRGB decode (ASTC spec §C.2.19).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLdrPixels<TMode>(
        Span<byte> buffer,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int dstRowStride,
        in ColorEndpointPair endpointPair,
        Span<int> texelWeights)
        where TMode : struct, ILdrColorMode
    {
        (byte lowR, byte lowG, byte lowB, byte lowA) = endpointPair.LdrLow;
        (byte highR, byte highG, byte highB, byte highA) = endpointPair.LdrHigh;

        int footprintWidth = footprint.Width;
        int footprintHeight = footprint.Height;

        for (int pixelY = 0; pixelY < footprintHeight; pixelY++)
        {
            int dstRowOffset = ((dstBaseY + pixelY) * dstRowStride) + (dstBaseX * BlockInfo.ChannelsPerPixel);
            int srcRowBase = pixelY * footprintWidth;
            int pixelX = 0;

            if (Vector128.IsHardwareAccelerated)
            {
                int limit = footprintWidth - (SimdLaneCount - 1);
                for (; pixelX < limit; pixelX += SimdLaneCount)
                {
                    int texelIndex = srcRowBase + pixelX;
                    Vector128<int> weights = Vector128.Create(
                        texelWeights[texelIndex],
                        texelWeights[texelIndex + 1],
                        texelWeights[texelIndex + 2],
                        texelWeights[texelIndex + 3]);
                    SimdHelpers.Write4PixelLdr<TMode>(
                        buffer,
                        dstRowOffset + (pixelX * BlockInfo.ChannelsPerPixel),
                        lowR,
                        lowG,
                        lowB,
                        lowA,
                        highR,
                        highG,
                        highB,
                        highA,
                        weights);
                }
            }

            for (; pixelX < footprintWidth; pixelX++)
            {
                SimdHelpers.WriteSinglePixelLdr<TMode>(
                    buffer,
                    dstRowOffset + (pixelX * BlockInfo.ChannelsPerPixel),
                    lowR,
                    lowG,
                    lowB,
                    lowA,
                    highR,
                    highG,
                    highB,
                    highA,
                    texelWeights[srcRowBase + pixelX]);
            }
        }
    }
}
