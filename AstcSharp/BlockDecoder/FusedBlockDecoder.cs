using System.Runtime.CompilerServices;
using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp.BlockDecoder;

/// <summary>
/// Shared decode core for the fused (zero-allocation) ASTC block decode pipeline.
/// Contains BISE extraction and weight infill used by both LDR and HDR decoders.
/// </summary>
internal static class FusedBlockDecoder
{
    /// <summary>
    /// Shared decode core: BISE decode, unquantize, and infill.
    /// Populates <paramref name="texelWeights"/> and returns the decoded endpoint pair.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static ColorEndpointPair DecodeFusedCore(
        UInt128 bits, in BlockInfo info, Footprint footprint, Span<int> texelWeights)
    {
        // 1. BISE decode color endpoint values
        int colorCount = info.EndpointMode0.GetColorValuesCount();
        Span<int> colors = stackalloc int[colorCount];
        DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount, info.ColorValuesRange, colorCount, colors);

        // 2. Batch unquantize color values, then decode endpoint pair
        Quantization.UnquantizeCEValuesBatch(colors, colorCount, info.ColorValuesRange);
        var ep = EndpointCodec.DecodeColorsForModePolymorphicUnquantized(colors, info.EndpointMode0);

        // 3. BISE decode weights
        int gridSize = info.GridWidth * info.GridHeight;
        Span<int> gridWeights = stackalloc int[gridSize];
        DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange, gridSize, gridWeights);

        // 4. Batch unquantize weights
        Quantization.UnquantizeWeightsBatch(gridWeights, gridSize, info.WeightRange);

        // 5. Infill weights from grid to texels (or pass through if identity mapping)
        if (info.GridWidth == footprint.Width && info.GridHeight == footprint.Height)
        {
            gridWeights[..footprint.PixelCount].CopyTo(texelWeights);
        }
        else
        {
            var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
            DecimationTable.InfillWeights(gridWeights, di, texelWeights);
        }

        return ep;
    }

    /// <summary>
    /// Decodes BISE-encoded values from the specified bit region of the block.
    /// For bit-only encoding with small total bit count, extracts directly from ulong
    /// without creating a BitStream (avoids per-value ShiftBuffer overhead).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DecodeBiseValues(UInt128 bits, int startBit, int bitCount, int range, int valuesCount, Span<int> result)
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
    internal static void DecodeBiseWeights(UInt128 bits, int weightBitCount, int weightRange, int count, Span<int> result)
    {
        var (encMode, bitsPerValue) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(weightRange);
        var weightBits = UInt128Extensions.ReverseBits(bits) & UInt128Extensions.OnesMask(weightBitCount);

        if (encMode == BiseEncodingMode.BitEncoding)
        {
            // Fast path: extract N-bit values directly via shifts
            int totalBits = count * bitsPerValue;
            ulong mask = (1UL << bitsPerValue) - 1;

            if (totalBits <= 64)
            {
                ulong data = weightBits.Low();
                for (int i = 0; i < count; i++)
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
                for (int i = 0; i < count; i++)
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
        decoder.Decode(count, ref weightBitStream, result);
    }
}
