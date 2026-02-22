using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp.Core;

/// <summary>
/// Batch ASTC decoder that processes B=8 blocks simultaneously using Vector256 SIMD.
/// Uses Structure-of-Arrays (SoA) layout so that weight infill and interpolation
/// operate on contiguous vectors rather than per-block scalar gathers.
/// Only handles single-partition, single-plane, LDR blocks with matching grid configs.
/// Non-batchable blocks fall back to the existing per-block decoder.
/// </summary>
internal static class BatchDecoder
{
    private const int B = 8; // Vector256<int> lane count
    private const int BytesPerPixel = 4;
    private const int MaxColorValues = 8; // max color values per single-partition endpoint

    public static bool IsSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256.IsHardwareAccelerated;
    }

    /// <summary>
    /// Processes one row of blocks, batching compatible blocks into groups of B.
    /// </summary>
    public static void DecompressRow(
        ReadOnlySpan<byte> astcData,
        int rowBlockOffset,
        int blocksInRow,
        Footprint footprint,
        int imageWidth,
        int imageHeight,
        int rowPixelY,
        Span<byte> imageBuffer)
    {
        int fW = footprint.Width;
        int blockX = 0;

        while (blockX < blocksInRow)
        {
            int remaining = blocksInRow - blockX;
            if (remaining >= B)
            {
                int processed = TryProcessBatch(
                    astcData, rowBlockOffset + blockX * PhysicalBlock.SizeInBytes,
                    footprint, imageWidth, imageHeight, rowPixelY, blockX * fW, imageBuffer);
                if (processed > 0)
                {
                    blockX += processed;
                    continue;
                }
            }

            DecompressSingleBlockToImage(
                astcData, rowBlockOffset + blockX * PhysicalBlock.SizeInBytes,
                footprint, imageWidth, imageHeight, rowPixelY, blockX * fW, imageBuffer);
            blockX++;
        }
    }

    /// <summary>
    /// Tries to batch-process B blocks starting at the given data offset.
    /// Returns B on success, 0 if the blocks can't be batched together.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int TryProcessBatch(
        ReadOnlySpan<byte> astcData,
        int dataOffset,
        Footprint footprint,
        int imageWidth,
        int imageHeight,
        int rowPixelY,
        int batchStartPixelX,
        Span<byte> imageBuffer)
    {
        int fW = footprint.Width;
        int fH = footprint.Height;
        int pixelCount = footprint.PixelCount;

        // Parse block 0 to establish reference configuration
        var pb0 = ReadPhysicalBlock(astcData, dataOffset);
        if (!IsBlockBatchable(pb0)) return 0;

        var gridDims0 = pb0.GetWeightGridDimensions()!.Value;
        int gridX = gridDims0.Width;
        int gridY = gridDims0.Height;
        int weightRange = pb0.GetWeightRange()!.Value;
        int gridSize = gridX * gridY;

        // Validate weight range is usable
        if (weightRange < 1 || weightRange > Quantization.kWeightRangeMaxValue) return 0;

        // Cache validated PhysicalBlocks to avoid re-parsing in Stage 1
        // UInt128 is 16 bytes × 8 = 128 bytes on stack
        Span<UInt128> blockBitsArr = stackalloc UInt128[B];
        blockBitsArr[0] = pb0.BlockBits;

        // Validate remaining B-1 blocks match the reference config
        for (int b = 1; b < B; b++)
        {
            var pb = ReadPhysicalBlock(astcData, dataOffset + b * PhysicalBlock.SizeInBytes);
            if (!IsBlockBatchable(pb)) return 0;

            var gd = pb.GetWeightGridDimensions()!.Value;
            if (gd.Width != gridX || gd.Height != gridY || pb.GetWeightRange()!.Value != weightRange)
                return 0;

            blockBitsArr[b] = pb.BlockBits;
        }

        // All B blocks are compatible — proceed with batch decode

        // SoA buffers on the stack
        Span<int> gridWeights = stackalloc int[gridSize * B];
        Span<int> texelWeights = stackalloc int[pixelCount * B];
        Span<int> endpointLow = stackalloc int[4 * B];
        Span<int> endpointHigh = stackalloc int[4 * B];
        Span<int> channelOut = stackalloc int[4 * pixelCount * B];

        // Temp buffers reused across blocks (outside loop to avoid stack accumulation)
        Span<int> biseWeights = stackalloc int[gridSize];
        Span<int> colors = stackalloc int[MaxColorValues];

        // ——— Stage 1: Per-block serial decode into SoA buffers ———
        for (int b = 0; b < B; b++)
        {
            var pb = PhysicalBlock.Create(blockBitsArr[b]);

            // Decode color endpoints
            int colorBitCount = pb.GetColorBitCount()!.Value;
            int colorStartBit = pb.GetColorStartBit()!.Value;
            int colorValuesRange = pb.GetColorValuesRange()!.Value;
            int colorValuesCount = pb.GetColorValuesCount()!.Value;

            var colorBitMask = UInt128Extensions.OnesMask(colorBitCount);
            var colorBits = (pb.BlockBits >> colorStartBit) & colorBitMask;
            var colorBitStream = new BitStream(colorBits, 128);

            var colorDecoder = BoundedIntegerSequenceDecoder.GetCached(colorValuesRange);
            colorDecoder.Decode(colorValuesCount, ref colorBitStream, colors);

            var cem = pb.GetEndpointMode(0)!.Value;
            var ep = EndpointCodec.DecodeColorsForModePolymorphic(
                colors.Slice(0, colorValuesCount), colorValuesRange, cem);

            // Store endpoints into SoA buffers
            endpointLow[0 * B + b] = ep.LdrLow.R;
            endpointLow[1 * B + b] = ep.LdrLow.G;
            endpointLow[2 * B + b] = ep.LdrLow.B;
            endpointLow[3 * B + b] = ep.LdrLow.A;

            endpointHigh[0 * B + b] = ep.LdrHigh.R;
            endpointHigh[1 * B + b] = ep.LdrHigh.G;
            endpointHigh[2 * B + b] = ep.LdrHigh.B;
            endpointHigh[3 * B + b] = ep.LdrHigh.A;

            // BISE decode weights
            int weightBitCount = pb.GetWeightBitCount()!.Value;
            var weightBits = UInt128Extensions.ReverseBits(pb.BlockBits) & UInt128Extensions.OnesMask(weightBitCount);
            var weightBitStream = new BitStream(weightBits, 128);

            var weightDecoder = BoundedIntegerSequenceDecoder.GetCached(weightRange);
            weightDecoder.Decode(gridSize, ref weightBitStream, biseWeights);

            // Unquantize and store into SoA gridWeights
            for (int g = 0; g < gridSize; g++)
            {
                gridWeights[g * B + b] = Quantization.UnquantizeWeightFromRange(biseWeights[g], weightRange);
            }
        }

        // ——— Stage 2: Batch InfillWeights (the main perf win) ———
        var di = DecimationTable.Get(footprint, gridX, gridY);
        BatchInfillWeights(gridWeights, di, texelWeights);

        // ——— Stage 3: Batch Interpolation ———
        BatchInterpolate(endpointLow, endpointHigh, texelWeights, pixelCount, channelOut);

        // ——— Stage 4: Output scatter ———
        ScatterOutput(channelOut, pixelCount, fW, fH,
            batchStartPixelX, rowPixelY, imageWidth, imageHeight, imageBuffer);

        return B;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBlockBatchable(PhysicalBlock pb)
    {
        if (pb.IsVoidExtent || pb.IsIllegalEncoding) return false;
        if (pb.GetPartitionsCount() != 1 || pb.IsDualPlane) return false;

        var gridDims = pb.GetWeightGridDimensions();
        var range = pb.GetWeightRange();
        if (!gridDims.HasValue || !range.HasValue) return false;

        var cem = pb.GetEndpointMode(0);
        if (!cem.HasValue || cem.Value.IsHdr()) return false;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PhysicalBlock ReadPhysicalBlock(ReadOnlySpan<byte> data, int offset)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset));
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8));
        return PhysicalBlock.Create(new UInt128(high, low));
    }

    /// <summary>
    /// Batch weight infill: for each texel, loads B grid weights contiguously and
    /// applies the shared bilinear factors via broadcast multiply.
    /// Before: 64 scalar gathers per block. After: 0 gathers (contiguous vector loads).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BatchInfillWeights(
        Span<int> gridWeights,
        DecimationInfo di,
        Span<int> texelWeights)
    {
        int texelCount = di.TexelCount;
        int padded = di.PaddedTexelCount;
        int[] weightIndices = di.WeightIndices;
        int[] weightFactors = di.WeightFactors;

        ref int gwRef = ref MemoryMarshal.GetReference(gridWeights);
        ref int twRef = ref MemoryMarshal.GetReference(texelWeights);
        var vec8 = Vector256.Create(8);

        for (int t = 0; t < texelCount; t++)
        {
            var sum = vec8;
            for (int j = 0; j < 4; j++)
            {
                int off = j * padded + t;
                int idx = weightIndices[off];    // Same index for all B blocks
                int factor = weightFactors[off]; // Same factor for all B blocks

                // Contiguous load: B weights from B blocks at grid position idx
                var gridW = Vector256.LoadUnsafe(ref Unsafe.Add(ref gwRef, idx * B));

                // Broadcast multiply: one scalar factor applied to all B blocks
                sum += gridW * Vector256.Create(factor);
            }
            (sum >> 4).StoreUnsafe(ref Unsafe.Add(ref twRef, t * B));
        }
    }

    /// <summary>
    /// Batch interpolation: for each channel and pixel, loads B texel weights
    /// contiguously and interpolates between broadcast endpoint values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BatchInterpolate(
        Span<int> endpointLow,
        Span<int> endpointHigh,
        Span<int> texelWeights,
        int pixelCount,
        Span<int> channelOut)
    {
        ref int elRef = ref MemoryMarshal.GetReference(endpointLow);
        ref int ehRef = ref MemoryMarshal.GetReference(endpointHigh);
        ref int twRef = ref MemoryMarshal.GetReference(texelWeights);
        ref int coRef = ref MemoryMarshal.GetReference(channelOut);

        var vec32 = Vector256.Create(32);
        var vec64 = Vector256.Create(64);
        var vec255 = Vector256.Create(255);
        var vec32767 = Vector256.Create(32767);

        for (int c = 0; c < 4; c++)
        {
            var low = Vector256.LoadUnsafe(ref Unsafe.Add(ref elRef, c * B));
            var high = Vector256.LoadUnsafe(ref Unsafe.Add(ref ehRef, c * B));

            // Bit-replicate endpoint bytes to 16-bit
            var c0 = (low << 8) | low;
            var c1 = (high << 8) | high;

            for (int p = 0; p < pixelCount; p++)
            {
                var w = Vector256.LoadUnsafe(ref Unsafe.Add(ref twRef, p * B));
                var interp = (c0 * (vec64 - w) + c1 * w + vec32) >> 6;
                var result = ((interp * vec255) + vec32767) >>> 16;
                result = Vector256.Min(result, vec255);
                result.StoreUnsafe(ref Unsafe.Add(ref coRef, (c * pixelCount + p) * B));
            }
        }
    }

    /// <summary>
    /// Scatters per-channel SoA results to interleaved RGBA output for each block.
    /// </summary>
    private static void ScatterOutput(
        Span<int> channelOut,
        int pixelCount,
        int fW,
        int fH,
        int batchStartPixelX,
        int rowPixelY,
        int imageWidth,
        int imageHeight,
        Span<byte> imageBuffer)
    {
        int chStride = pixelCount * B;

        for (int b = 0; b < B; b++)
        {
            int blockPixelX = batchStartPixelX + b * fW;
            int copyWidth = Math.Min(fW, imageWidth - blockPixelX);
            int copyHeight = Math.Min(fH, imageHeight - rowPixelY);

            if (copyWidth <= 0 || copyHeight <= 0) continue;

            for (int py = 0; py < copyHeight; py++)
            {
                int dstRow = ((rowPixelY + py) * imageWidth + blockPixelX) * BytesPerPixel;

                for (int px = 0; px < copyWidth; px++)
                {
                    int src = (py * fW + px) * B + b;
                    int dst = dstRow + px * BytesPerPixel;

                    imageBuffer[dst + 0] = (byte)channelOut[src];                // R
                    imageBuffer[dst + 1] = (byte)channelOut[chStride + src];     // G
                    imageBuffer[dst + 2] = (byte)channelOut[2 * chStride + src]; // B
                    imageBuffer[dst + 3] = (byte)channelOut[3 * chStride + src]; // A
                }
            }
        }
    }

    /// <summary>
    /// Fallback: decompresses a single block using the existing per-block path
    /// and copies the result directly into the image buffer with edge clipping.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // Prevent inlining to ensure stackalloc is freed per call
    internal static void DecompressSingleBlockToImage(
        ReadOnlySpan<byte> astcData,
        int dataOffset,
        Footprint footprint,
        int imageWidth,
        int imageHeight,
        int rowPixelY,
        int blockPixelX,
        Span<byte> imageBuffer)
    {
        Span<byte> decoded = stackalloc byte[footprint.Width * footprint.Height * BytesPerPixel];
        AstcDecoder.DecompressBlock(
            astcData.Slice(dataOffset, PhysicalBlock.SizeInBytes),
            footprint, decoded);

        int copyWidth = Math.Min(footprint.Width, imageWidth - blockPixelX);
        int copyHeight = Math.Min(footprint.Height, imageHeight - rowPixelY);
        int copyBytes = copyWidth * BytesPerPixel;

        for (int py = 0; py < copyHeight; py++)
        {
            int srcOff = py * footprint.Width * BytesPerPixel;
            int dstOff = ((rowPixelY + py) * imageWidth + blockPixelX) * BytesPerPixel;
            decoded.Slice(srcOff, copyBytes).CopyTo(imageBuffer.Slice(dstOff, copyBytes));
        }
    }
}
