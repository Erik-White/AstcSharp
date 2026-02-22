using System.Runtime.CompilerServices;
using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

/// <summary>
/// Fused block info computed in a single pass from raw ASTC block bits.
/// Replaces ~25-30 redundant DecodeBlockMode calls per block with exactly 1.
/// Used by the hot decode path; existing PhysicalBlock API remains unchanged
/// for tests, encoding, and non-hot paths.
/// </summary>
internal struct BlockInfo
{
    private static readonly int[] s_weightRanges =
        [-1, -1, 1, 2, 3, 4, 5, 7, -1, -1, 9, 11, 15, 19, 23, 31];

    private static readonly int[] s_extraCemBitsForPartition = [0, 2, 5, 8];

    public bool IsValid;
    public bool IsVoidExtent;

    // Weight grid
    public int GridWidth;
    public int GridHeight;
    public int WeightRange;
    public int WeightBitCount;

    // Partitions
    public int PartitionCount;

    // Dual plane
    public bool IsDualPlane;
    public int DualPlaneChannel; // only valid if IsDualPlane

    // Color endpoints
    public int ColorStartBit;
    public int ColorBitCount;
    public int ColorValuesRange;
    public int ColorValuesCount;

    // Endpoint modes (up to 4 partitions)
    public ColorEndpointMode EndpointMode0;
    public ColorEndpointMode EndpointMode1;
    public ColorEndpointMode EndpointMode2;
    public ColorEndpointMode EndpointMode3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ColorEndpointMode GetEndpointMode(int partition)
    {
        return partition switch
        {
            0 => EndpointMode0,
            1 => EndpointMode1,
            2 => EndpointMode2,
            3 => EndpointMode3,
            _ => EndpointMode0
        };
    }

    /// <summary>
    /// Decode all block info from raw 128-bit ASTC block data in a single pass.
    /// Returns a BlockInfo with IsValid=false if the block is illegal or reserved.
    /// Returns a BlockInfo with IsVoidExtent=true for void extent blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static BlockInfo Decode(UInt128 bits)
    {
        ulong low_bits = bits.Low();

        // ---- Step 1: Check void extent ----
        // Void extent: bits[0:9] == 0x1FC (9 bits)
        if ((low_bits & 0x1FF) == 0x1FC)
        {
            return new BlockInfo
            {
                IsVoidExtent = true,
                IsValid = !CheckVoidExtentIsIllegal(bits, low_bits)
            };
        }

        // ---- Step 2: Decode block mode, grid dims, weight range in ONE pass ----
        // This inlines DecodeBlockMode + DecodeWeightProperties
        int gridWidth, gridHeight;
        bool isWidthA6HeightB6 = false;
        uint rBits; // 3-bit range index component

        if ((low_bits & 0x3) != 0) // bits[0:2] != 0
        {
            ulong mode_bits = (low_bits >> 2) & 0x3; // bits[2:4]
            int a = (int)((low_bits >> 5) & 0x3); // bits[5:7]

            switch (mode_bits)
            {
                case 0: // WidthB4HeightA2
                {
                    int b = (int)((low_bits >> 7) & 0x3); // bits[7:9]
                    gridWidth = b + 4;
                    gridHeight = a + 2;
                    break;
                }
                case 1: // WidthB8HeightA2
                {
                    int b = (int)((low_bits >> 7) & 0x3);
                    gridWidth = b + 8;
                    gridHeight = a + 2;
                    break;
                }
                case 2: // WidthA2HeightB8
                {
                    int b = (int)((low_bits >> 7) & 0x3);
                    gridWidth = a + 2;
                    gridHeight = b + 8;
                    break;
                }
                case 3: // WidthB2HeightA2 or WidthA2HeightB6
                {
                    int b = (int)((low_bits >> 7) & 0x1); // 1 bit only!
                    if (((low_bits >> 8) & 1) != 0)
                    {
                        gridWidth = b + 2;
                        gridHeight = a + 2;
                    }
                    else
                    {
                        gridWidth = a + 2;
                        gridHeight = b + 6;
                    }
                    break;
                }
                default:
                    return default; // unreachable
            }

            // Range r[2:0] = {bit4, bit1, bit0} for these modes
            rBits = (uint)(((low_bits >> 4) & 1) | (((low_bits >> 0) & 0x3) << 1));
        }
        else // bits[0:2] == 0
        {
            ulong mode_bits = (low_bits >> 5) & 0xF; // bits[5:9]
            int a = (int)((low_bits >> 5) & 0x3); // bits[5:7]

            if ((mode_bits & 0xC) == 0x0)
            {
                if ((low_bits & 0xF) == 0)
                    return default; // reserved block mode

                // Width12HeightA2
                gridWidth = 12;
                gridHeight = a + 2;
            }
            else if ((mode_bits & 0xC) == 0x4)
            {
                // WidthA2Height12
                gridWidth = a + 2;
                gridHeight = 12;
            }
            else if (mode_bits == 0xC)
            {
                // Width6Height10
                gridWidth = 6;
                gridHeight = 10;
            }
            else if (mode_bits == 0xD)
            {
                // Width10Height6
                gridWidth = 10;
                gridHeight = 6;
            }
            else if ((mode_bits & 0xC) == 0x8)
            {
                // WidthA6HeightB6
                int b = (int)((low_bits >> 9) & 0x3); // bits[9:11]
                gridWidth = a + 6;
                gridHeight = b + 6;
                isWidthA6HeightB6 = true;
            }
            else
            {
                return default; // reserved
            }

            // Range r[2:0] = {bit4, bit3, bit2} for these modes
            rBits = (uint)(((low_bits >> 4) & 1) | (((low_bits >> 2) & 0x3) << 1));
        }

        // ---- Step 3: Compute weight range from r and h bits ----
        uint hBit = isWidthA6HeightB6 ? 0u : (uint)((low_bits >> 9) & 1);
        int rangeIdx = (int)((hBit << 3) | rBits);
        if ((uint)rangeIdx >= (uint)s_weightRanges.Length)
            return default;
        int weightRange = s_weightRanges[rangeIdx];
        if (weightRange < 0)
            return default;

        // ---- Step 4: Dual plane ----
        // WidthA6HeightB6 mode never has dual plane; otherwise check bit 10
        bool isDualPlane = !isWidthA6HeightB6 && ((low_bits >> 10) & 1) != 0;

        // ---- Step 5: Partition count ----
        int partitionCount = 1 + (int)((low_bits >> 11) & 0x3);

        // ---- Step 6: Validate weight count ----
        int numWeights = gridWidth * gridHeight;
        if (isDualPlane) numWeights *= 2;
        if (numWeights > 64)
            return default;

        // 4 partitions + dual plane is illegal
        if (partitionCount == 4 && isDualPlane)
            return default;

        // ---- Step 7: Weight bit count ----
        int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, weightRange);
        if (weightBitCount < 24 || weightBitCount > 96)
            return default;

        // ---- Step 8: Endpoint modes + extra CEM bits ----
        ColorEndpointMode cem0 = default, cem1 = default, cem2 = default, cem3 = default;
        int colorValuesCount = 0;
        int numExtraCEMBits = 0;

        if (partitionCount == 1)
        {
            cem0 = (ColorEndpointMode)((low_bits >> 13) & 0xF);
            colorValuesCount = (((int)cem0 / 4) + 1) * 2;
        }
        else
        {
            // Multi-partition CEM decode
            ulong sharedCemMarker = (low_bits >> 23) & 0x3;

            if (sharedCemMarker == 0)
            {
                // Shared CEM: all partitions use the same mode
                var sharedCem = (ColorEndpointMode)((low_bits >> 25) & 0xF);
                cem0 = cem1 = cem2 = cem3 = sharedCem;
                for (int i = 0; i < partitionCount; i++)
                    colorValuesCount += sharedCem.GetColorValuesCount();
            }
            else
            {
                // Non-shared CEM: per-partition modes
                numExtraCEMBits = s_extraCemBitsForPartition[partitionCount - 1];

                int extraCemStartPos = 128 - numExtraCEMBits - weightBitCount;
                var extraCem = BitOperations.GetBits(bits, extraCemStartPos, numExtraCEMBits);

                ulong cemval = (low_bits >> 23) & 0x3F; // 6 bits starting at bit 23
                int baseCem = (int)(((cemval & 0x3) - 1) * 4);
                cemval >>= 2;

                ulong combined = cemval | (extraCem.Low() << 4);
                ulong cembits = combined;

                // Extract c bits (1 bit per partition)
                Span<int> c = stackalloc int[4];
                for (int i = 0; i < partitionCount; i++)
                {
                    c[i] = (int)(cembits & 0x1);
                    cembits >>= 1;
                }
                // Extract m bits (2 bits per partition)
                for (int i = 0; i < partitionCount; i++)
                {
                    int m = (int)(cembits & 0x3);
                    cembits >>= 2;
                    var mode = (ColorEndpointMode)(baseCem + 4 * c[i] + m);
                    switch (i)
                    {
                        case 0: cem0 = mode; break;
                        case 1: cem1 = mode; break;
                        case 2: cem2 = mode; break;
                        case 3: cem3 = mode; break;
                    }
                    colorValuesCount += mode.GetColorValuesCount();
                }
            }
        }

        if (colorValuesCount > 18)
            return default;

        // ---- Step 9: Dual plane start position and channel ----
        int dualPlaneBitStartPos = 128 - weightBitCount - numExtraCEMBits;
        if (isDualPlane) dualPlaneBitStartPos -= 2;

        int dualPlaneChannel = isDualPlane
            ? (int)BitOperations.GetBits(bits, dualPlaneBitStartPos, 2).Low()
            : -1;

        // ---- Step 10: Color values info ----
        int colorStartBit = (partitionCount == 1) ? 17 : 29;
        int maxColorBits = dualPlaneBitStartPos - colorStartBit;

        // Minimum bits needed check
        int requiredColorBits = ((13 * colorValuesCount) + 4) / 5;
        if (maxColorBits < requiredColorBits)
            return default;

        // Find max color range that fits
        int colorValuesRange = 0, colorBitCount = 0;
        for (int rv = 255; rv > 0; --rv)
        {
            int bitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(colorValuesCount, rv);
            if (bitCount <= maxColorBits)
            {
                colorValuesRange = rv;
                colorBitCount = bitCount;
                break;
            }
        }

        if (colorValuesRange == 0)
            return default;

        // ---- Step 11: Validate endpoint modes are not HDR for batchable checks ----
        // (HDR blocks are still valid, just flagged for downstream use)

        return new BlockInfo
        {
            IsValid = true,
            IsVoidExtent = false,
            GridWidth = gridWidth,
            GridHeight = gridHeight,
            WeightRange = weightRange,
            WeightBitCount = weightBitCount,
            PartitionCount = partitionCount,
            IsDualPlane = isDualPlane,
            DualPlaneChannel = dualPlaneChannel,
            ColorStartBit = colorStartBit,
            ColorBitCount = colorBitCount,
            ColorValuesRange = colorValuesRange,
            ColorValuesCount = colorValuesCount,
            EndpointMode0 = cem0,
            EndpointMode1 = cem1,
            EndpointMode2 = cem2,
            EndpointMode3 = cem3,
        };
    }

    /// <summary>
    /// Inline void extent validation (replaces PhysicalBlock.CheckVoidExtentIsIllegal).
    /// </summary>
    private static bool CheckVoidExtentIsIllegal(UInt128 bits, ulong low_bits)
    {
        if (BitOperations.GetBits(bits, 10, 2).Low() != 0x3UL)
            return true;

        int c0 = (int)BitOperations.GetBits(low_bits, 12, 13);
        int c1 = (int)BitOperations.GetBits(low_bits, 25, 13);
        int c2 = (int)BitOperations.GetBits(low_bits, 38, 13);
        int c3 = (int)BitOperations.GetBits(low_bits, 51, 13);

        const int all1s = (1 << 13) - 1;
        bool coordsAll1s = c0 == all1s && c1 == all1s && c2 == all1s && c3 == all1s;

        return !coordsAll1s && (c0 >= c1 || c2 >= c3);
    }
}
