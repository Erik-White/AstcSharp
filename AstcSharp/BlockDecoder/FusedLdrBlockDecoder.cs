using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.TexelBlock;

namespace AstcSharp.BlockDecoder;

/// <summary>
/// LDR pixel writers and entry points for the fused decode pipeline.
/// All methods handle single-partition, non-dual-plane blocks.
/// </summary>
internal static class FusedLdrBlockDecoder
{
    private const int BytesPerPixelUnorm8 = 4;

    /// <summary>
    /// Fused LDR decode to contiguous buffer.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedLdr(UInt128 bits, in BlockInfo info, Footprint footprint, Span<byte> buffer)
    {
        Span<int> texelWeights = stackalloc int[footprint.PixelCount];
        var ep = FusedBlockDecoder.DecodeFusedCore(bits, in info, footprint, texelWeights);
        WriteLdrPixels(buffer, footprint.PixelCount, in ep, texelWeights);
    }

    /// <summary>
    /// Fused LDR decode writing directly to image buffer at strided positions.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedLdrToImage(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        Span<byte> imageBuffer)
    {
        Span<int> texelWeights = stackalloc int[footprint.PixelCount];
        var ep = FusedBlockDecoder.DecodeFusedCore(bits, in info, footprint, texelWeights);
        WriteLdrPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
    }

    /// <summary>
    /// Writes all pixels for a single-partition LDR block using SIMD where possible.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLdrPixels(Span<byte> buffer, int pixelCount, in ColorEndpointPair ep, Span<int> texelWeights)
    {
        int lowR = ep.LdrLow.R, lowG = ep.LdrLow.G, lowB = ep.LdrLow.B, lowA = ep.LdrLow.A;
        int highR = ep.LdrHigh.R, highG = ep.LdrHigh.G, highB = ep.LdrHigh.B, highA = ep.LdrHigh.A;

        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            int limit = pixelCount - 3;
            for (; i < limit; i += 4)
            {
                var weights = Vector128.Create(
                    texelWeights[i], texelWeights[i + 1],
                    texelWeights[i + 2], texelWeights[i + 3]);
                SimdHelpers.Write4PixelLdr(
                    buffer,
                    i * 4,
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

        for (; i < pixelCount; i++)
        {
            SimdHelpers.WriteSinglePixelLdr(
                buffer,
                i * 4,
                lowR,
                lowG,
                lowB,
                lowA,
                highR,
                highG,
                highB,
                highA,
                texelWeights[i]);
        }
    }

    /// <summary>
    /// Writes LDR pixels directly to image buffer at strided positions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLdrPixelsToImage(
        Span<byte> imageBuffer,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        in ColorEndpointPair ep,
        Span<int> texelWeights)
    {
        int lowR = ep.LdrLow.R, lowG = ep.LdrLow.G, lowB = ep.LdrLow.B, lowA = ep.LdrLow.A;
        int highR = ep.LdrHigh.R, highG = ep.LdrHigh.G, highB = ep.LdrHigh.B, highA = ep.LdrHigh.A;

        int fW = footprint.Width;
        int fH = footprint.Height;
        int rowStride = imageWidth * BytesPerPixelUnorm8;

        for (int py = 0; py < fH; py++)
        {
            int dstRowOffset = (dstBaseY + py) * rowStride + dstBaseX * BytesPerPixelUnorm8;
            int srcRowBase = py * fW;
            int px = 0;

            if (Vector128.IsHardwareAccelerated)
            {
                int limit = fW - 3;
                for (; px < limit; px += 4)
                {
                    int ti = srcRowBase + px;
                    var weights = Vector128.Create(
                        texelWeights[ti], texelWeights[ti + 1],
                        texelWeights[ti + 2], texelWeights[ti + 3]);
                    SimdHelpers.Write4PixelLdr(
                        imageBuffer, dstRowOffset + px * BytesPerPixelUnorm8,
                        lowR, lowG, lowB, lowA, highR, highG, highB, highA,
                        weights);
                }
            }

            for (; px < fW; px++)
            {
                SimdHelpers.WriteSinglePixelLdr(
                    imageBuffer, dstRowOffset + px * BytesPerPixelUnorm8,
                    lowR, lowG, lowB, lowA, highR, highG, highB, highA,
                    texelWeights[srcRowBase + px]);
            }
        }
    }
}
