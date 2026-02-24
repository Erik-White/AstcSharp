using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp;

/// <summary>
/// Provides methods to decode ASTC-compressed texture data into uncompressed pixel formats.
/// </summary>
public static class AstcDecoder
{
    private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private const int BytesPerPixelUnorm8 = 4;

    internal static Span<byte> DecompressImage(AstcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        
        return DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint);
    }

    internal static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        
        return DecompressImage(astcData, width, height, footPrint);
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to uncompressed RGBA8 format (4 bytes per pixel).
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>Array of bytes in RGBA8 format (width * height * 4 bytes total)</returns>
    /// <exception cref="InvalidOperationException">If decompression fails for any block</exception>
    public static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        var imageBuffer = new byte[width * height * BytesPerPixelUnorm8];

        return DecompressImage(astcData, width, height, footprint, imageBuffer)
            ? imageBuffer
            : [];
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to uncompressed RGBA8 format into a caller-provided buffer.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <param name="imageBuffer">Output buffer. Must be at least width * height * 4 bytes.</param>
    /// <returns>True if decompression succeeded, false if input was invalid.</returns>
    /// <exception cref="InvalidOperationException">If decompression fails for any block</exception>
    public static bool DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<byte> imageBuffer)
    {
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;

        if (blockWidth == 0 || blockHeight == 0 || width == 0 || height == 0)
            return false;

        int blocksWide = (width + blockWidth - 1) / blockWidth;
        if (blocksWide == 0)
            return false;

        int expectedBlockCount = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight);
        if (astcData.Length % PhysicalBlock.SizeInBytes != 0 || astcData.Length / PhysicalBlock.SizeInBytes != expectedBlockCount)
            return false;

        var decodedBlock = Array.Empty<byte>();

        try
        {
            // Create a buffer once for fallback blocks; fast path writes directly to image
            decodedBlock = _arrayPool.Rent(footprint.Width * footprint.Height * BytesPerPixelUnorm8);
            var decodedPixels = decodedBlock.AsSpan();
            int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
            int blockIndex = 0;
            int fW = footprint.Width;
            int fH = footprint.Height;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex++ * PhysicalBlock.SizeInBytes;
                    if (blockDataOffset + PhysicalBlock.SizeInBytes > astcData.Length)
                        continue;

                    ulong low = BinaryPrimitives.ReadUInt64LittleEndian(astcData.Slice(blockDataOffset));
                    ulong high = BinaryPrimitives.ReadUInt64LittleEndian(astcData.Slice(blockDataOffset + 8));
                    var blockBits = new UInt128(high, low);

                    int dstBaseX = blockX * fW;
                    int dstBaseY = blockY * fH;
                    int copyWidth = Math.Min(fW, width - dstBaseX);
                    int copyHeight = Math.Min(fH, height - dstBaseY);

                    var info = BlockInfo.Decode(blockBits);
                    if (!info.IsValid) continue;

                    // Fast path: fuse decode directly into image buffer for interior full blocks
                    if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane
                        && !info.EndpointMode0.IsHdr()
                        && copyWidth == fW && copyHeight == fH)
                    {
                        DecompressBlockFusedLdrToImage(
                            blockBits, in info, footprint,
                            dstBaseX, dstBaseY, width, imageBuffer);
                        continue;
                    }

                    // Fallback: decode to temp buffer, then copy
                    if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane
                        && !info.EndpointMode0.IsHdr())
                    {
                        DecompressBlockFusedLdr(blockBits, in info, footprint, decodedPixels);
                    }
                    else
                    {
                        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, blockBits, in info);
                        if (logicalBlock is null) continue;
                        logicalBlock.WriteAllPixelsLdr(footprint, decodedPixels);
                    }

                    int copyBytes = copyWidth * BytesPerPixelUnorm8;
                    for (int pixelY = 0; pixelY < copyHeight; pixelY++)
                    {
                        int srcOffset = pixelY * fW * BytesPerPixelUnorm8;
                        int dstOffset = ((dstBaseY + pixelY) * width + dstBaseX) * BytesPerPixelUnorm8;
                        decodedPixels.Slice(srcOffset, copyBytes)
                            .CopyTo(imageBuffer.Slice(dstOffset, copyBytes));
                    }
                }
            }
        }
        finally
        {
            _arrayPool.Return(decodedBlock);
        }

        return true;
    }

    /// <summary>
    /// Decompress a single ASTC block to RGBA8 pixel data
    /// </summary>
    /// <param name="blockData">The data to decode</param>
    /// <param name="footprint">The type of ASTC block footprint e.g. 4x4, 5x5, etc.</param>
    /// <returns>The decoded block of pixels as RGBA values</returns>
    public static Span<byte> DecompressBlock(ReadOnlySpan<byte> blockData, Footprint footprint)
    {
        var decodedPixels = Array.Empty<byte>();
        try
        {
            decodedPixels = _arrayPool.Rent(footprint.Width * footprint.Height * BytesPerPixelUnorm8);
            var decodedPixelBuffer = decodedPixels.AsSpan();

            DecompressBlock(blockData, footprint, decodedPixelBuffer);
        }
        
        finally
        {
            _arrayPool.Return(decodedPixels);
        }

        return decodedPixels;
    }

    /// <summary>
    /// Decompresses a single ASTC block to RGBA8 pixel data
    /// </summary>
    /// <param name="blockData">The data to decode</param>
    /// <param name="footprint">The type of ASTC block footprint e.g. 4x4, 5x5, etc.</param>
    /// <param name="buffer">The buffer to write the decoded pixels into</param>
    /// <returns>The decoded block of pixels as RGBA values</returns>
    public static void DecompressBlock(ReadOnlySpan<byte> blockData, Footprint footprint, Span<byte> buffer)
    {
        // Read the 16 bytes that make up the ASTC block as a 128-bit value
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(blockData);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(blockData.Slice(8));
        var blockBits = new UInt128(high, low);

        var info = BlockInfo.Decode(blockBits);
        if (!info.IsValid) return;

        // Fully fused fast path for single-partition, non-dual-plane, LDR blocks
        if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane
            && !info.EndpointMode0.IsHdr())
        {
            DecompressBlockFusedLdr(blockBits, in info, footprint, buffer);
            return;
        }

        // Fallback for void extent, multi-partition, dual plane, HDR
        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, blockBits, in info);
        if (logicalBlock is null) return;
        logicalBlock.WriteAllPixelsLdr(footprint, buffer);
    }

    /// <summary>
    /// Fully fused LDR decode: BISE decode → unquantize → infill → interpolate → output.
    /// Zero heap allocations. Entire decode happens on the stack.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecompressBlockFusedLdr(UInt128 bits, in BlockInfo info, Footprint footprint, Span<byte> buffer)
    {
        // 1. BISE decode color endpoint values
        int colorCount = info.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount, info.ColorValuesRange, colorCount, colors);

        // 2. Batch unquantize color values, then decode endpoint pair
        Quantization.UnquantizeCEValuesBatch(colors, colorCount, info.ColorValuesRange);
        var ep = EndpointCodec.DecodeColorsForModeUnquantized(colors, info.EndpointMode0);

        // 3. BISE decode weights
        int gridSize = info.GridWidth * info.GridHeight;
        Span<int> gridWeights = stackalloc int[gridSize];
        DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange, gridSize, gridWeights);

        // 4. Batch unquantize weights
        Quantization.UnquantizeWeightsBatch(gridWeights, gridSize, info.WeightRange);

        // 5. Infill weights from grid to texels (or skip if identity mapping)
        if (info.GridWidth == footprint.Width && info.GridHeight == footprint.Height)
        {
            // Grid matches footprint: each texel maps 1-to-1 to a grid point
            WriteLdrPixels(buffer, footprint.PixelCount, in ep, gridWeights);
        }
        else
        {
            var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
            Span<int> texelWeights = stackalloc int[footprint.PixelCount];
            DecimationTable.InfillWeights(gridWeights, di, texelWeights);
            WriteLdrPixels(buffer, footprint.PixelCount, in ep, texelWeights);
        }
    }

    /// <summary>
    /// Fully fused LDR decode writing directly to image buffer at strided positions.
    /// Avoids the intermediate block buffer + row-by-row copy for interior full blocks.
    /// Only handles single-partition, non-dual-plane, LDR blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecompressBlockFusedLdrToImage(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        Span<byte> imageBuffer)
    {
        // 1. BISE decode color endpoint values
        int colorCount = info.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount, info.ColorValuesRange, colorCount, colors);

        // 2. Batch unquantize color values, then decode endpoint pair
        Quantization.UnquantizeCEValuesBatch(colors, colorCount, info.ColorValuesRange);
        var ep = EndpointCodec.DecodeColorsForModeUnquantized(colors, info.EndpointMode0);

        // 3. BISE decode weights
        int gridSize = info.GridWidth * info.GridHeight;
        Span<int> gridWeights = stackalloc int[gridSize];
        DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange, gridSize, gridWeights);

        // 4. Batch unquantize weights
        Quantization.UnquantizeWeightsBatch(gridWeights, gridSize, info.WeightRange);

        // 5+6. Infill weights and write pixels to image buffer
        if (info.GridWidth == footprint.Width && info.GridHeight == footprint.Height)
        {
            // Grid matches footprint: each texel maps 1-to-1, skip bilinear infill
            WriteLdrPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, gridWeights);
        }
        else
        {
            Span<int> texelWeights = stackalloc int[footprint.PixelCount];
            var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
            DecimationTable.InfillWeights(gridWeights, di, texelWeights);
            WriteLdrPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
        }
    }

    /// <summary>
    /// Decodes BISE-encoded values from the specified bit region of the block.
    /// For bit-only encoding with small total bit count, extracts directly from ulong
    /// without creating a BitStream (avoids per-value ShiftBuffer overhead).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBiseValues(UInt128 bits, int startBit, int bitCount, int range, int valuesCount, Span<int> result)
    {
        var (encMode, bitsPerValue) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(range);

        if (encMode == BiseEncodingMode.BitEncoding)
        {
            // Fast path: extract N-bit values directly via shifts
            int totalBits = valuesCount * bitsPerValue;
            ulong mask = (1UL << bitsPerValue) - 1;

            if (startBit + totalBits <= 64)
            {
                // All color data fits in the low 64 bits
                ulong data = bits.Low() >> startBit;
                for (int i = 0; i < valuesCount; i++)
                {
                    result[i] = (int)(data & mask);
                    data >>= bitsPerValue;
                }
            }
            else
            {
                // Spans both halves — use UInt128 shift then extract from low
                var shifted = (bits >> startBit) & UInt128Extensions.OnesMask(totalBits);
                ulong lo = shifted.Low();
                ulong hi = shifted.High();
                int bitPos = 0;
                for (int i = 0; i < valuesCount; i++)
                {
                    if (bitPos < 64)
                    {
                        ulong val = (lo >> bitPos) & mask;
                        if (bitPos + bitsPerValue > 64)
                            val |= (hi << (64 - bitPos)) & mask;
                        result[i] = (int)val;
                    }
                    else
                    {
                        result[i] = (int)((hi >> (bitPos - 64)) & mask);
                    }
                    bitPos += bitsPerValue;
                }
            }
            return;
        }

        // Trit/quint encoding: fall back to full BISE decoder
        var colorBitMask = UInt128Extensions.OnesMask(bitCount);
        var colorBits = (bits >> startBit) & colorBitMask;
        var colorBitStream = new BitStream(colorBits, 128);
        var decoder = BoundedIntegerSequenceDecoder.GetCached(range);
        decoder.Decode(valuesCount, ref colorBitStream, result);
    }

    /// <summary>
    /// Decodes BISE-encoded weight values from the reversed high-end of the block.
    /// For bit-only encoding, extracts directly from the reversed bits without BitStream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeBiseWeights(UInt128 bits, int weightBitCount, int weightRange, int gridSize, Span<int> result)
    {
        var (encMode, bitsPerValue) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(weightRange);
        var weightBits = UInt128Extensions.ReverseBits(bits) & UInt128Extensions.OnesMask(weightBitCount);

        if (encMode == BiseEncodingMode.BitEncoding)
        {
            // Fast path: extract N-bit values directly via shifts
            int totalBits = gridSize * bitsPerValue;
            ulong mask = (1UL << bitsPerValue) - 1;

            if (totalBits <= 64)
            {
                ulong data = weightBits.Low();
                for (int i = 0; i < gridSize; i++)
                {
                    result[i] = (int)(data & mask);
                    data >>= bitsPerValue;
                }
            }
            else
            {
                ulong lo = weightBits.Low();
                ulong hi = weightBits.High();
                int bitPos = 0;
                for (int i = 0; i < gridSize; i++)
                {
                    if (bitPos < 64)
                    {
                        ulong val = (lo >> bitPos) & mask;
                        if (bitPos + bitsPerValue > 64)
                            val |= (hi << (64 - bitPos)) & mask;
                        result[i] = (int)val;
                    }
                    else
                    {
                        result[i] = (int)((hi >> (bitPos - 64)) & mask);
                    }
                    bitPos += bitsPerValue;
                }
            }
            return;
        }

        // Trit/quint encoding: fall back to full BISE decoder
        var weightBitStream = new BitStream(weightBits, 128);
        var decoder = BoundedIntegerSequenceDecoder.GetCached(weightRange);
        decoder.Decode(gridSize, ref weightBitStream, result);
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

    /// <summary>
    /// Fully fused HDR decode: BISE decode → unquantize → infill → interpolate → float output.
    /// Zero heap allocations. Entire decode happens on the stack.
    /// Handles single-partition, non-dual-plane blocks with both LDR and HDR endpoints.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecompressBlockFusedHdr(UInt128 bits, in BlockInfo info, Footprint footprint, Span<float> buffer)
    {
        // 1. BISE decode color endpoint values
        int colorCount = info.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount, info.ColorValuesRange, colorCount, colors);

        // 2. Batch unquantize color values, then decode endpoint pair (LDR or HDR)
        Quantization.UnquantizeCEValuesBatch(colors, colorCount, info.ColorValuesRange);
        var ep = EndpointCodec.DecodeColorsForModePolymorphicUnquantized(colors, info.EndpointMode0);

        // 3. BISE decode weights
        int gridSize = info.GridWidth * info.GridHeight;
        Span<int> gridWeights = stackalloc int[gridSize];
        DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange, gridSize, gridWeights);

        // 4. Batch unquantize weights
        Quantization.UnquantizeWeightsBatch(gridWeights, gridSize, info.WeightRange);

        // 5. Infill weights from grid to texels (or skip if identity mapping)
        if (info.GridWidth == footprint.Width && info.GridHeight == footprint.Height)
        {
            WriteHdrOutputPixels(buffer, footprint.PixelCount, in ep, gridWeights);
        }
        else
        {
            var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
            Span<int> texelWeights = stackalloc int[footprint.PixelCount];
            DecimationTable.InfillWeights(gridWeights, di, texelWeights);
            WriteHdrOutputPixels(buffer, footprint.PixelCount, in ep, texelWeights);
        }
    }

    /// <summary>
    /// Fully fused HDR decode writing directly to image buffer at strided positions.
    /// Avoids the intermediate block buffer + row-by-row copy for interior full blocks.
    /// Handles single-partition, non-dual-plane blocks with both LDR and HDR endpoints.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecompressBlockFusedHdrToImage(
        UInt128 bits,
        in BlockInfo info,
        Footprint footprint,
        int dstBaseX,
        int dstBaseY,
        int imageWidth,
        Span<float> imageBuffer)
    {
        // 1. BISE decode color endpoint values
        int colorCount = info.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount, info.ColorValuesRange, colorCount, colors);

        // 2. Batch unquantize color values, then decode endpoint pair (LDR or HDR)
        Quantization.UnquantizeCEValuesBatch(colors, colorCount, info.ColorValuesRange);
        var ep = EndpointCodec.DecodeColorsForModePolymorphicUnquantized(colors, info.EndpointMode0);

        // 3. BISE decode weights
        int gridSize = info.GridWidth * info.GridHeight;
        Span<int> gridWeights = stackalloc int[gridSize];
        DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange, gridSize, gridWeights);

        // 4. Batch unquantize weights
        Quantization.UnquantizeWeightsBatch(gridWeights, gridSize, info.WeightRange);

        // 5+6. Infill weights and write pixels to image buffer
        if (info.GridWidth == footprint.Width && info.GridHeight == footprint.Height)
        {
            WriteHdrOutputPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, gridWeights);
        }
        else
        {
            Span<int> texelWeights = stackalloc int[footprint.PixelCount];
            var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
            DecimationTable.InfillWeights(gridWeights, di, texelWeights);
            WriteHdrOutputPixelsToImage(imageBuffer, footprint, dstBaseX, dstBaseY, imageWidth, in ep, texelWeights);
        }
    }

    /// <summary>
    /// Dispatches HDR float output based on endpoint type (LDR or HDR).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHdrOutputPixels(
        Span<float> buffer, int pixelCount, in ColorEndpointPair ep, Span<int> texelWeights)
    {
        if (ep.IsHdr)
            WriteHdrPixels(buffer, pixelCount, in ep, texelWeights);
        else
            WriteLdrAsHdrPixels(buffer, pixelCount, in ep, texelWeights);
    }

    /// <summary>
    /// Dispatches HDR float output to image buffer based on endpoint type.
    /// </summary>
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

    /// <summary>
    /// Writes LDR endpoints as normalized float output.
    /// Interpolates to UNORM16, then normalizes to [0, 1].
    /// </summary>
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

    /// <summary>
    /// Writes LDR endpoints as normalized float output directly to image buffer.
    /// </summary>
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

    /// <summary>
    /// Writes HDR endpoints as float output with LNS-to-FP16 conversion.
    /// </summary>
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

    /// <summary>
    /// Writes HDR endpoints as float output directly to image buffer.
    /// </summary>
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

    /// <summary>
    /// Interpolates an LDR channel and returns a normalized float [0, 1].
    /// Bit-replicates 8-bit endpoints to 16-bit, interpolates, then normalizes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InterpolateLdrAsFloat(int p0, int p1, int weight)
    {
        int c0 = (p0 << 8) | p0;
        int c1 = (p1 << 8) | p1;
        int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
        return Math.Clamp(c, 0, 0xFFFF) / 65535.0f;
    }

    /// <summary>
    /// Interpolates an HDR channel (LNS values) and converts to float via FP16.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float InterpolateHdrAsFloat(int p0, int p1, int weight)
    {
        int c = (p0 * (64 - weight) + p1 * weight + 32) / 64;
        ushort clamped = (ushort)Math.Clamp(c, 0, 0xFFFF);
        ushort sf16 = LogicalBlock.LnsToSf16(clamped);
        return (float)BitConverter.UInt16BitsToHalf(sf16);
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        const int channelsPerPixel = 4;
        var imageBuffer = new float[width * height * channelsPerPixel];
        if (!DecompressHdrImage(astcData, width, height, footprint, imageBuffer))
            return [];
        return imageBuffer;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to RGBA float values into a caller-provided buffer.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <param name="imageBuffer">Output buffer. Must be at least width * height * 4 floats.</param>
    /// <returns>True if decompression succeeded, false if input was invalid.</returns>
    /// <exception cref="InvalidOperationException">If decompression fails for any block</exception>
    public static bool DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<float> imageBuffer)
    {
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;

        if (blockWidth == 0 || blockHeight == 0 || width == 0 || height == 0)
            return false;

        int blocksWide = (width + blockWidth - 1) / blockWidth;
        if (blocksWide == 0)
            return false;

        int expectedBlockCount = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight);
        if (astcData.Length % PhysicalBlock.SizeInBytes != 0 || astcData.Length / PhysicalBlock.SizeInBytes != expectedBlockCount)
            return false;

        const int channelsPerPixel = 4;
        var decodedBlock = Array.Empty<float>();

        try
        {
            // Create a buffer once for fallback blocks; fast path writes directly to image
            decodedBlock = ArrayPool<float>.Shared.Rent(footprint.Width * footprint.Height * channelsPerPixel);
            var decodedPixels = decodedBlock.AsSpan();
            int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
            int blockIndex = 0;
            int fW = footprint.Width;
            int fH = footprint.Height;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex++ * PhysicalBlock.SizeInBytes;
                    if (blockDataOffset + PhysicalBlock.SizeInBytes > astcData.Length)
                        continue;

                    ulong low = BinaryPrimitives.ReadUInt64LittleEndian(astcData.Slice(blockDataOffset));
                    ulong high = BinaryPrimitives.ReadUInt64LittleEndian(astcData.Slice(blockDataOffset + 8));
                    var blockBits = new UInt128(high, low);

                    int dstBaseX = blockX * fW;
                    int dstBaseY = blockY * fH;
                    int copyWidth = Math.Min(fW, width - dstBaseX);
                    int copyHeight = Math.Min(fH, height - dstBaseY);

                    var info = BlockInfo.Decode(blockBits);
                    if (!info.IsValid) continue;

                    // Fast path: fuse decode directly into image buffer for interior full blocks
                    if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane
                        && copyWidth == fW && copyHeight == fH)
                    {
                        DecompressBlockFusedHdrToImage(
                            blockBits, in info, footprint,
                            dstBaseX, dstBaseY, width, imageBuffer);
                        continue;
                    }

                    // Fused decode to temp buffer for single-partition non-dual-plane
                    if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane)
                    {
                        DecompressBlockFusedHdr(blockBits, in info, footprint, decodedPixels);
                    }
                    else
                    {
                        // Fallback: LogicalBlock path for void extent, multi-partition, dual plane
                        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, blockBits, in info);
                        if (logicalBlock is null) continue;
                        for (int row = 0; row < fH; row++)
                        {
                            for (int column = 0; column < fW; ++column)
                            {
                                var pixelOffset = (fW * row * channelsPerPixel) + (column * channelsPerPixel);
                                logicalBlock.WriteHdrPixel(column, row, decodedPixels.Slice(pixelOffset, channelsPerPixel));
                            }
                        }
                    }

                    int copyFloats = copyWidth * channelsPerPixel;
                    for (int pixelY = 0; pixelY < copyHeight; pixelY++)
                    {
                        int srcOffset = pixelY * fW * channelsPerPixel;
                        int dstOffset = ((dstBaseY + pixelY) * width + dstBaseX) * channelsPerPixel;
                        decodedPixels.Slice(srcOffset, copyFloats)
                            .CopyTo(imageBuffer.Slice(dstOffset, copyFloats));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(decodedBlock);
        }

        return true;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint type</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        return DecompressHdrImage(astcData, width, height, footPrint);
    }

    /// <summary>
    /// Decompresses a single ASTC block to float RGBA values.
    /// </summary>
    /// <param name="blockData">The 16-byte ASTC block to decode</param>
    /// <param name="footprint">The ASTC block footprint</param>
    /// <param name="buffer">The buffer to write decoded values into (must be at least footprint.Width * footprint.Height * 4 elements)</param>
    public static void DecompressHdrBlock(ReadOnlySpan<byte> blockData, Footprint footprint, Span<float> buffer)
    {
        // Read the 16 bytes that make up the ASTC block as a 128-bit value
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(blockData);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(blockData.Slice(8));
        var blockBits = new UInt128(high, low);

        var info = BlockInfo.Decode(blockBits);
        if (!info.IsValid) return;

        // Fused fast path for single-partition, non-dual-plane blocks
        if (!info.IsVoidExtent && info.PartitionCount == 1 && !info.IsDualPlane)
        {
            DecompressBlockFusedHdr(blockBits, in info, footprint, buffer);
            return;
        }

        // Fallback for void extent, multi-partition, dual plane
        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, blockBits, in info);
        if (logicalBlock is null) return;

        const int channelsPerPixel = 4;
        for (int row = 0; row < footprint.Height; row++)
        {
            for (int column = 0; column < footprint.Width; ++column)
            {
                var pixelOffset = (footprint.Width * row * channelsPerPixel) + (column * channelsPerPixel);
                logicalBlock.WriteHdrPixel(column, row, buffer.Slice(pixelOffset, channelsPerPixel));
            }
        }
    }
}
