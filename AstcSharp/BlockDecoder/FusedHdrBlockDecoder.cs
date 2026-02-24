using System.Runtime.CompilerServices;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.TexelBlock;

namespace AstcSharp.BlockDecoder;

/// <summary>
/// HDR pixel writers and entry points for the fused decode pipeline.
/// All methods handle single-partition, non-dual-plane blocks.
/// </summary>
internal static class FusedHdrBlockDecoder
{
    /// <summary>
    /// Fused HDR decode to contiguous float buffer.
    /// Handles single-partition, non-dual-plane blocks with both LDR and HDR endpoints.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedHdr(UInt128 bits, in BlockInfo info, Footprint footprint, Span<float> buffer)
    {
        Span<int> texelWeights = stackalloc int[footprint.PixelCount];
        var ep = FusedBlockDecoder.DecodeFusedCore(bits, in info, footprint, texelWeights);
        WriteHdrOutputPixels(buffer, footprint.PixelCount, in ep, texelWeights);
    }

    /// <summary>
    /// Fused HDR decode writing directly to image buffer at strided positions.
    /// Handles single-partition, non-dual-plane blocks with both LDR and HDR endpoints.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecompressBlockFusedHdrToImage(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        Span<float> imageBuffer)
    {
        Span<int> texelWeights = stackalloc int[footprint.PixelCount];
        var ep = FusedBlockDecoder.DecodeFusedCore(bits, in info, footprint, texelWeights);
        WriteHdrOutputPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHdrOutputPixels(
        Span<float> buffer, int pixelCount, in ColorEndpointPair ep, Span<int> texelWeights)
    {
        if (ep.IsHdr)
            WriteHdrPixels(buffer, pixelCount, in ep, texelWeights);
        else
            WriteLdrAsHdrPixels(buffer, pixelCount, in ep, texelWeights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHdrOutputPixelsToImage(
        Span<float> imageBuffer,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        in ColorEndpointPair ep,
        Span<int> texelWeights)
    {
        if (ep.IsHdr)
            WriteHdrPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
        else
            WriteLdrAsHdrPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLdrAsHdrPixels(Span<float> buffer, int pixelCount, in ColorEndpointPair ep, Span<int> texelWeights)
    {
        int lowR = ep.LdrLow.R, lowG = ep.LdrLow.G, lowB = ep.LdrLow.B, lowA = ep.LdrLow.A;
        int highR = ep.LdrHigh.R, highG = ep.LdrHigh.G, highB = ep.LdrHigh.B, highA = ep.LdrHigh.A;

        for (int i = 0; i < pixelCount; i++)
        {
            int w = texelWeights[i];
            int offset = i * 4;
            buffer[offset + 0] = InterpolateLdrAsFloat(lowR, highR, w);
            buffer[offset + 1] = InterpolateLdrAsFloat(lowG, highG, w);
            buffer[offset + 2] = InterpolateLdrAsFloat(lowB, highB, w);
            buffer[offset + 3] = InterpolateLdrAsFloat(lowA, highA, w);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteLdrAsHdrPixelsToImage(
        Span<float> imageBuffer,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        in ColorEndpointPair ep,
        Span<int> texelWeights)
    {
        int lowR = ep.LdrLow.R, lowG = ep.LdrLow.G, lowB = ep.LdrLow.B, lowA = ep.LdrLow.A;
        int highR = ep.LdrHigh.R, highG = ep.LdrHigh.G, highB = ep.LdrHigh.B, highA = ep.LdrHigh.A;

        const int channelsPerPixel = 4;
        int fW = footprint.Width;
        int fH = footprint.Height;
        int rowStride = imageWidth * channelsPerPixel;

        for (int py = 0; py < fH; py++)
        {
            int dstRowOffset = (dstBaseY + py) * rowStride + dstBaseX * channelsPerPixel;
            int srcRowBase = py * fW;

            for (int px = 0; px < fW; px++)
            {
                int w = texelWeights[srcRowBase + px];
                int dstOffset = dstRowOffset + px * channelsPerPixel;
                imageBuffer[dstOffset + 0] = InterpolateLdrAsFloat(lowR, highR, w);
                imageBuffer[dstOffset + 1] = InterpolateLdrAsFloat(lowG, highG, w);
                imageBuffer[dstOffset + 2] = InterpolateLdrAsFloat(lowB, highB, w);
                imageBuffer[dstOffset + 3] = InterpolateLdrAsFloat(lowA, highA, w);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHdrPixels(Span<float> buffer, int pixelCount, in ColorEndpointPair ep, Span<int> texelWeights)
    {
        bool alphaIsLdr = ep.AlphaIsLdr;
        int lowR = ep.HdrLow.R, lowG = ep.HdrLow.G, lowB = ep.HdrLow.B, lowA = ep.HdrLow.A;
        int highR = ep.HdrHigh.R, highG = ep.HdrHigh.G, highB = ep.HdrHigh.B, highA = ep.HdrHigh.A;

        for (int i = 0; i < pixelCount; i++)
        {
            int w = texelWeights[i];
            int offset = i * 4;
            buffer[offset + 0] = InterpolateHdrAsFloat(lowR, highR, w);
            buffer[offset + 1] = InterpolateHdrAsFloat(lowG, highG, w);
            buffer[offset + 2] = InterpolateHdrAsFloat(lowB, highB, w);

            if (alphaIsLdr)
            {
                int c = (lowA * (64 - w) + highA * w + 32) / 64;
                buffer[offset + 3] = (ushort)Math.Clamp(c, 0, 0xFFFF) / 65535.0f;
            }
            else
            {
                buffer[offset + 3] = InterpolateHdrAsFloat(lowA, highA, w);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHdrPixelsToImage(
        Span<float> imageBuffer,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        in ColorEndpointPair ep,
        Span<int> texelWeights)
    {
        bool alphaIsLdr = ep.AlphaIsLdr;
        int lowR = ep.HdrLow.R, lowG = ep.HdrLow.G, lowB = ep.HdrLow.B, lowA = ep.HdrLow.A;
        int highR = ep.HdrHigh.R, highG = ep.HdrHigh.G, highB = ep.HdrHigh.B, highA = ep.HdrHigh.A;

        const int channelsPerPixel = 4;
        int fW = footprint.Width;
        int fH = footprint.Height;
        int rowStride = imageWidth * channelsPerPixel;

        for (int py = 0; py < fH; py++)
        {
            int dstRowOffset = (dstBaseY + py) * rowStride + dstBaseX * channelsPerPixel;
            int srcRowBase = py * fW;

            for (int px = 0; px < fW; px++)
            {
                int w = texelWeights[srcRowBase + px];
                int dstOffset = dstRowOffset + px * channelsPerPixel;
                imageBuffer[dstOffset + 0] = InterpolateHdrAsFloat(lowR, highR, w);
                imageBuffer[dstOffset + 1] = InterpolateHdrAsFloat(lowG, highG, w);
                imageBuffer[dstOffset + 2] = InterpolateHdrAsFloat(lowB, highB, w);

                if (alphaIsLdr)
                {
                    int c = (lowA * (64 - w) + highA * w + 32) / 64;
                    imageBuffer[dstOffset + 3] = (ushort)Math.Clamp(c, 0, 0xFFFF) / 65535.0f;
                }
                else
                {
                    imageBuffer[dstOffset + 3] = InterpolateHdrAsFloat(lowA, highA, w);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InterpolateLdrAsFloat(int p0, int p1, int weight)
    {
        int c0 = (p0 << 8) | p0;
        int c1 = (p1 << 8) | p1;
        int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
        return Math.Clamp(c, 0, 0xFFFF) / 65535.0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InterpolateHdrAsFloat(int p0, int p1, int weight)
    {
        int c = (p0 * (64 - weight) + p1 * weight + 32) / 64;
        ushort clamped = (ushort)Math.Clamp(c, 0, 0xFFFF);
        ushort sf16 = LogicalBlock.LnsToSf16(clamped);
        return (float)BitConverter.UInt16BitsToHalf(sf16);
    }
}
