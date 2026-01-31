using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

/// <summary>
/// A physical ASTC texel block (128 bits)
/// </summary>
public class PhysicalBlock
{
    public const int SizeInBytes = 16;

    public UInt128 BlockBits { get; init; }

    public bool IsDualPlane
        => !IsIllegalEncoding && DecodeDualPlaneBit(BlockBits);

    public bool IsIllegalEncoding
        => IdentifyInvalidEncodingIssues() is not null;

    public bool IsVoidExtent
        => !IsIllegalEncoding && DecodeBlockMode(BlockBits) == PhysicalBlockMode.VoidExtent;

    public PhysicalBlock(ulong low) : this((UInt128)low)
    {
    }

    public PhysicalBlock(ulong low, ulong high) : this(new UInt128(high, low))
    {
    }

    public PhysicalBlock(UInt128 bits)
    {
        BlockBits = bits;
    }

    internal (int Width, int Height)? GetWeightGridDimensions()
    {
        var weightGridProperties = DecodeWeightProperties(BlockBits, out var _);

        return weightGridProperties is not null && !IsIllegalEncoding
            ? (weightGridProperties.Value.Width, weightGridProperties.Value.Height)
            : null;
    }

    internal int? GetWeightRange()
    {
        var weightGridProperties = DecodeWeightProperties(BlockBits, out var _);

        return weightGridProperties is not null && !IsIllegalEncoding
            ? weightGridProperties.Value.Range
            : null;
    }

    internal int[]? GetVoidExtentCoordinates()
    {
        // If void extent coords are all 1's then these are not valid void extent coords
        ulong voidExtentMask = 0xFFFFFFFFFFFFFDFFUL;
        ulong constBlockMode = 0xFFFFFFFFFFFFFDFCUL;

        return !IsIllegalEncoding && IsVoidExtent && (voidExtentMask & BlockBits.Low()) != constBlockMode
            ? DecodeVoidExtentCoordinates(BlockBits)
            : null;
    }

    /// <summary>
    /// Get the dual plane channel if dual plane is enabled
    /// </summary>
    /// <returns>The dual plane channel if enabled, otherwise null.</returns>
    internal int? GetDualPlaneChannel()
    {
        if (!IsDualPlane) return null;

        int dualPlaneStartPosition = DecodeDualPlaneBitStartPosition(BlockBits);
        var planeBits = BitOperations.GetBits(BlockBits, dualPlaneStartPosition, 2);

        return (int)planeBits.Low();
    }

    internal string? IdentifyInvalidEncodingIssues()
    {
        // If the block is not a void extent block, then it must have
        // weights specified. DecodeWeightProps will return the weight specifications
        // if they exist and are legal according to C.2.24, and will otherwise be
        // empty.
        var blockMode = DecodeBlockMode(BlockBits);
        if (blockMode != PhysicalBlockMode.VoidExtent)
        {
            var props = DecodeWeightProperties(BlockBits, out var error);
            if (props == null)
            {
                return error;
            }
        }

        if (blockMode == PhysicalBlockMode.VoidExtent)
        {
            // Check reserved bits at the full 128-bit level like the C++ reference.
            if (BitOperations.GetBits(BlockBits, 10, 2).Low() != 0x3UL)
            {
                return "Reserved bits set for void extent block";
            }

            var coords = DecodeVoidExtentCoordinates(BlockBits);
            bool coordsAll1s = true;
            foreach (var coord in coords) coordsAll1s &= coord == ((1 << 13) - 1);

            if (!coordsAll1s && (coords[0] >= coords[1] || coords[2] >= coords[3]))
            {
                return "Void extent texture coordinates are invalid";
            }
        }

        if (blockMode != PhysicalBlockMode.VoidExtent)
        {
            int numColorVals = DecodeNumColorValues(BlockBits);
            if (numColorVals > 18) return "Too many color values";

            int numPartitions = DecodePartitionsCount(BlockBits);
            int dualPlaneStartPos = DecodeDualPlaneBitStartPosition(BlockBits);
            int colorStartBit = (numPartitions == 1) ? 17 : 29;

            int requiredColorBits = ((13 * numColorVals) + 4) / 5;
            int availableColorBits = dualPlaneStartPos - colorStartBit;
            if (availableColorBits < requiredColorBits) return "Not enough color bits";

            if (numPartitions == 4 && DecodeDualPlaneBit(BlockBits)) return "Both four partitions and dual plane specified";
        }

        return null;
    }

    internal int? GetWeightBitCount()
        => !IsIllegalEncoding && !IsVoidExtent
            ? DecodeNumWeightBits(BlockBits)
            : null;

    internal int? GetWeightStartBit()
        => !IsIllegalEncoding && !IsVoidExtent
            ? 128 - DecodeNumWeightBits(BlockBits)
            : null;

    internal int? GetPartitionsCount()
        => !IsIllegalEncoding && !IsVoidExtent
            ? DecodePartitionsCount(BlockBits)
            : null;

    internal int? GetPartitionId()
    {
        var partitionsCount = GetPartitionsCount();

        return partitionsCount.HasValue && partitionsCount.Value != 1
            ? (int)BitOperations.GetBits(BlockBits.Low(), 13, 10)
            : null;
    }

    internal ColorEndpointMode? GetEndpointMode(int partition)
        => !IsVoidExtent && partition >= 0 && DecodePartitionsCount(BlockBits) > partition
            ? DecodeEndpointMode(BlockBits, partition)
            : null;

    internal int? GetColorStartBit()
    {
        if (IsVoidExtent) return 64;

        var numPartitions = GetPartitionsCount();
        if (!numPartitions.HasValue) return null;

        return (numPartitions.Value == 1) ? 17 : 29;
    }

    internal int? GetColorValuesCount()
    {
        if (IsVoidExtent) return 4;
        
        return !IsIllegalEncoding
            ? DecodeNumColorValues(BlockBits)
            : null;
    }

    internal int? GetColorBitCount()
    {
        if (IsIllegalEncoding) return null;
        if (IsVoidExtent) return 64;

        GetColorValuesInfo(out int colorBits, out _);

        return colorBits;
    }

    internal int? GetColorValuesRange()
    {
        if (IsIllegalEncoding) return null;
        if (IsVoidExtent) return (1 << 16) - 1;

        GetColorValuesInfo(out _, out int colorRange);

        return colorRange;
    }

    private static PhysicalBlockMode? DecodeBlockMode(UInt128 astc_bits)
    {
        const int kVoidExtentMaskBits = 9;
        const uint kVoidExtentMask = 0x1FC;

        // The void-extent header is found in the low 64-bit word of the
        // canonical representation.
        if (BitOperations.GetBits(astc_bits.Low(), 0, kVoidExtentMaskBits) == kVoidExtentMask)
        {
            return PhysicalBlockMode.VoidExtent;
        }

        // For decoding block mode fields the relevant bits live in the low
        // 64-bit word of the canonical representation. Use the stored low
        // word for the remaining logic.
        ulong low_bits = astc_bits.Low();
        if (BitOperations.GetBits(low_bits, 0, 2) != 0)
        {
            var mode_bits = BitOperations.GetBits(low_bits, 2, 2);
            switch (mode_bits)
            {
                case 0: return PhysicalBlockMode.WidthB4HeightA2;
                case 1: return PhysicalBlockMode.WidthB8HeightA2;
                case 2: return PhysicalBlockMode.WidthA2HeightB8;
                case 3:
                    return (BitOperations.GetBits(low_bits, 8, 1) != 0) ? PhysicalBlockMode.WidthB2HeightA2 : PhysicalBlockMode.WidthA2HeightB6;
            }
        }
        else
        {
            var mode_bits = BitOperations.GetBits(low_bits, 5, 4);
            if ((mode_bits & 0xC) == 0x0)
            {
                if (BitOperations.GetBits(low_bits, 0, 4) == 0) return null; // reserved
                else return PhysicalBlockMode.Width12HeightA2;
            }
            else if ((mode_bits & 0xC) == 0x4) return PhysicalBlockMode.WidthA2Height12;
            else if (mode_bits == 0xC) return PhysicalBlockMode.Width6Height10;
            else if (mode_bits == 0xD) return PhysicalBlockMode.Width10Height6;
            else if ((mode_bits & 0xC) == 0x8) return PhysicalBlockMode.WidthA6HeightB6;
        }

        return null;
    }

    private static WeightGridDimensions? DecodeWeightProperties(UInt128 astc_bits, out string? error)
    {
        error = null;
        var block_mode = DecodeBlockMode(astc_bits);
        if (block_mode is null)
        {
            error = "Reserved block mode";
            return null;
        }

        var props = new WeightGridDimensions();
            uint low32 = (uint)(astc_bits.Low() & 0xFFFFFFFFUL);

        switch (block_mode.Value)
        {
            case PhysicalBlockMode.WidthB4HeightA2:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 7, 2);
                    props.Width = b + 4; props.Height = a + 2;
                }
                break;
            case PhysicalBlockMode.WidthB8HeightA2:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 7, 2);
                    props.Width = b + 8; props.Height = a + 2;
                }
                break;
            case PhysicalBlockMode.WidthA2HeightB8:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 7, 2);
                    props.Width = a + 2; props.Height = b + 8;
                }
                break;
            case PhysicalBlockMode.WidthA2HeightB6:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 7, 1);
                    props.Width = a + 2; props.Height = b + 6;
                }
                break;
            case PhysicalBlockMode.WidthB2HeightA2:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 7, 1);
                    props.Width = b + 2; props.Height = a + 2;
                }
                break;
            case PhysicalBlockMode.Width12HeightA2:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    props.Width = 12; props.Height = a + 2;
                }
                break;
            case PhysicalBlockMode.WidthA2Height12:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    props.Width = a + 2; props.Height = 12;
                }
                break;
            case PhysicalBlockMode.Width6Height10:
                props.Width = 6; props.Height = 10; break;
            case PhysicalBlockMode.Width10Height6:
                props.Width = 10; props.Height = 6; break;
            case PhysicalBlockMode.WidthA6HeightB6:
                {
                    int a = (int)BitOperations.GetBits(low32, 5, 2);
                    int b = (int)BitOperations.GetBits(low32, 9, 2);
                    props.Width = a + 6; props.Height = b + 6;
                }
                break;
            case PhysicalBlockMode.VoidExtent:
                error = "Void extent block has no weight grid";
                return null;
            default:
                throw new InvalidOperationException($"Error decoding weight grid for block mode {block_mode}");
        }

        uint r = (uint)BitOperations.GetBits(low32, 4, 1);
        switch (block_mode.Value)
        {
            case PhysicalBlockMode.WidthB4HeightA2:
            case PhysicalBlockMode.WidthB8HeightA2:
            case PhysicalBlockMode.WidthA2HeightB8:
            case PhysicalBlockMode.WidthA2HeightB6:
            case PhysicalBlockMode.WidthB2HeightA2:
                r |= (uint)(BitOperations.GetBits(low32, 0, 2) << 1);
                break;
            case PhysicalBlockMode.Width12HeightA2:
            case PhysicalBlockMode.WidthA2Height12:
            case PhysicalBlockMode.Width6Height10:
            case PhysicalBlockMode.Width10Height6:
            case PhysicalBlockMode.WidthA6HeightB6:
                r |= (uint)(BitOperations.GetBits(low32, 2, 2) << 1);
                break;
            default:
                error = "Internal error"; return null;
        }

        uint h = (uint)BitOperations.GetBits(low32, 9, 1);
        if (block_mode.Value == PhysicalBlockMode.WidthA6HeightB6) h = 0;

        int[] kWeightRanges = new int[] { -1, -1, 1, 2, 3, 4, 5, 7, -1, -1, 9, 11, 15, 19, 23, 31 };
        int idx = (int)((h << 3) | r);
        if (idx < 0 || idx >= kWeightRanges.Length)
        {
            // reserved range detected in weight props
            // Try alternative interpretation using high 32 bits
            uint altLow32 = (uint)(astc_bits.High() & 0xFFFFFFFFUL);
            // attempting alternate low32 interpretation
            uint alt_r = (uint)BitOperations.GetBits(altLow32, 4, 1);
            switch (block_mode.Value)
            {
                case PhysicalBlockMode.WidthB4HeightA2:
                case PhysicalBlockMode.WidthB8HeightA2:
                case PhysicalBlockMode.WidthA2HeightB8:
                case PhysicalBlockMode.WidthA2HeightB6:
                case PhysicalBlockMode.WidthB2HeightA2:
                    alt_r |= (uint)(BitOperations.GetBits(altLow32, 0, 2) << 1);
                    break;
                default:
                    alt_r |= (uint)(BitOperations.GetBits(altLow32, 2, 2) << 1);
                    break;
            }
            uint alt_h = (uint)BitOperations.GetBits(altLow32, 9, 1);
            int altIdx = (int)((alt_h << 3) | alt_r);
            // computed alternate candidate
            if (altIdx >= 0 && altIdx < kWeightRanges.Length && kWeightRanges[altIdx] >= 0)
            {
                // using alternate high-derived header fields
                r = alt_r; h = alt_h; idx = altIdx; low32 = altLow32; // adopt the alternate low32 for subsequent logic
            }
            else
            {
                // print bits 0..15
                string bits = "";
                for (int i = 0; i < 16; ++i)
                {
                    bits = (BitOperations.GetBits(low32, i, 1) == 1 ? '1' : '0') + bits;
                }
                // printed low32 bits for diagnostics removed
                error = "Reserved range for weight bits"; return null;
            }
        }
        if (idx < 0 || idx >= kWeightRanges.Length) { error = "Reserved range for weight bits"; return null; }
        props.Range = kWeightRanges[idx];
        if (props.Range < 0) { error = "Reserved range for weight bits"; return null; }

        int numWeights = props.Width * props.Height;
        if (DecodeDualPlaneBit(astc_bits)) numWeights *= 2;
        const int kMaxNumWeights = 64;
        if (kMaxNumWeights < numWeights) { error = "Too many weights specified"; return null; }

        int bitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, props.Range);
        const int kWeightGridMinBitLength = 24;
        const int kWeightGridMaxBitLength = 96;
        if (bitCount < kWeightGridMinBitLength) { error = "Too few bits required for weight grid"; return null; }
        if (kWeightGridMaxBitLength < bitCount) { error = "Too many bits required for weight grid"; return null; }

        return props;
    }

    private static int[] DecodeVoidExtentCoordinates(UInt128 astc_bits)
    {
        ulong low_bits = astc_bits.Low();
        var coords = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            coords[i] = (int)BitOperations.GetBits(low_bits, 12 + 13 * i, 13);
        }
        return coords;
    }

    private static bool DecodeDualPlaneBit(UInt128 astc_bits)
    {
        var block_mode = DecodeBlockMode(astc_bits);
        if (block_mode == PhysicalBlockMode.VoidExtent) return false;
        if (block_mode == PhysicalBlockMode.WidthA6HeightB6) return false;
        const int kDualPlaneBitPosition = 10;
        return BitOperations.GetBits(astc_bits, kDualPlaneBitPosition, 1).Low() != 0UL;
    }

    private static int DecodePartitionsCount(UInt128 astc_bits)
    {
        const int kNumPartitionsBitPosition = 11;
        const int kNumPartitionsBitLength = 2;
        ulong low_bits = astc_bits.Low();
        int num_partitions = 1 + (int)BitOperations.GetBits(low_bits, kNumPartitionsBitPosition, kNumPartitionsBitLength);
        
        ArgumentOutOfRangeException.ThrowIfLessThan(num_partitions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(num_partitions, 4);

        return num_partitions;
    }

    private static int DecodeNumWeightBits(UInt128 astc_bits)
    {
        var maybe = DecodeWeightProperties(astc_bits, out var _);
        if (maybe == null) return 0;
        var props = maybe.Value;
        int numWeights = props.Width * props.Height;
        if (DecodeDualPlaneBit(astc_bits)) numWeights *= 2;

        return BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, props.Range);
    }

    private static int DecodeNumExtraCEMBits(UInt128 astc_bits)
    {
        int num_partitions = DecodePartitionsCount(astc_bits);
        if (num_partitions == 1) return 0;
        const int kSharedCEMBitPosition = 23;
        const int kSharedCEMBitLength = 2;
        var shared_cem = BitOperations.GetBits(astc_bits, kSharedCEMBitPosition, kSharedCEMBitLength);
            if (shared_cem.Low() == 0UL) return 0;
        int[] extra_cem_bits_for_partition = new int[] { 0, 2, 5, 8 };
        return extra_cem_bits_for_partition[num_partitions - 1];
    }

    private static int DecodeDualPlaneBitStartPosition(UInt128 astc_bits)
    {
        int start_pos = 128 - DecodeNumWeightBits(astc_bits) - DecodeNumExtraCEMBits(astc_bits);
        if (DecodeDualPlaneBit(astc_bits)) return start_pos - 2;

        return start_pos;
    }

    private static ColorEndpointMode DecodeEndpointMode(UInt128 astc_bits, int partition)
    {
        int num_partitions = DecodePartitionsCount(astc_bits);
        ulong low_bits = astc_bits.Low();
        ArgumentOutOfRangeException.ThrowIfLessThan(partition, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, num_partitions);
        
        if (num_partitions == 1)
        {
            ulong cem = BitOperations.GetBits(low_bits, 13, 4);
            return (ColorEndpointMode)cem;
        }

        if (DecodeNumExtraCEMBits(astc_bits) == 0)
        {
            ulong shared_cem = BitOperations.GetBits(low_bits, 25, 4);
            return (ColorEndpointMode)shared_cem;
        }

        ulong cemval = BitOperations.GetBits(low_bits, 23, 6);
        int base_cem = (int)(((cemval & 0x3) - 1) * 4);
        cemval >>= 2;

        int num_extra_cem_bits = DecodeNumExtraCEMBits(astc_bits);
        int extra_cem_start_pos = 128 - num_extra_cem_bits - DecodeNumWeightBits(astc_bits);
        var extra_cem = BitOperations.GetBits(astc_bits, extra_cem_start_pos, num_extra_cem_bits);
            ulong combined = cemval | (extra_cem.Low() << 4);
        ulong cembits = combined;

        int c = -1, m = -1;
        for (int i = 0; i < num_partitions; ++i)
        {
            if (i == partition) c = (int)(cembits & 0x1);
            cembits >>= 1;
        }
        for (int i = 0; i < num_partitions; ++i)
        {
            if (i == partition) m = (int)(cembits & 0x3);
            cembits >>= 2;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(c, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(m, 0);
        int mode = base_cem + 4 * c + m;
        ArgumentOutOfRangeException.ThrowIfLessThan(mode, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mode, (int)ColorEndpointMode.ColorEndpointModeCount);
        
        return (ColorEndpointMode)mode;
    }

    private static int DecodeNumColorValues(UInt128 astc_bits)
    {
        int num_color_values = 0;
        int num_partitions = DecodePartitionsCount(astc_bits);
        for (int i = 0; i < num_partitions; ++i)
        {
            var endpoint_mode = DecodeEndpointMode(astc_bits, i);
            num_color_values += endpoint_mode.GetColorValuesCount();
        }
        return num_color_values;
    }

    private void GetColorValuesInfo(out int color_bits, out int color_range)
    {
        int dualPlaneStartPos = DecodeDualPlaneBitStartPosition(BlockBits);
        var colorStartBitOpt = GetColorStartBit();
        var numColorValuesOpt = GetColorValuesCount();
        if (!colorStartBitOpt.HasValue || !numColorValuesOpt.HasValue)
        {
            color_bits = 0; color_range = 0;
            return;
        }
        int maxColorBits = dualPlaneStartPos - colorStartBitOpt.Value;
        int numColorValues = numColorValuesOpt.Value;
        for (int range = byte.MaxValue; range > byte.MinValue; --range)
        {
            int bitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numColorValues, range);
            if (bitCount <= maxColorBits)
            {
                color_bits = bitCount;
                color_range = range;
                return;
            }
        }

        throw new InvalidOperationException("Not enough bits to store color values");
    }

    private record struct WeightGridDimensions(int Width, int Height, int Range);
}
