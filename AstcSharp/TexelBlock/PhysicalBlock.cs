using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

/// <summary>
/// A physical ASTC texel block (128 bits)
/// </summary>
internal readonly struct PhysicalBlock
{
    public const int SizeInBytes = 16;

    private static readonly int[] s_weightRanges = [-1, -1, 1, 2, 3, 4, 5, 7, -1, -1, 9, 11, 15, 19, 23, 31];
    private static readonly int[] s_extraCemBitsForPartition = [0, 2, 5, 8];

    public UInt128 BlockBits { get; }
    private readonly bool _isVoidExtent;
    private readonly bool _isIllegalEncoding;

    private PhysicalBlock(UInt128 bits, bool isVoidExtent, bool isIllegalEncoding)
    {
        BlockBits = bits;
        _isVoidExtent = isVoidExtent;
        _isIllegalEncoding = isIllegalEncoding;
    }

    /// <summary>
    /// Factory method to create a PhysicalBlock from raw bits
    /// </summary>
    public static PhysicalBlock Create(UInt128 bits)
    {
        bool isVoid = DecodeBlockMode(bits) == PhysicalBlockMode.VoidExtent;
        bool isIllegal = isVoid
            ? CheckVoidExtentIsIllegal(bits)
            : IdentifyStandardIssues(bits) is not null;
        return new PhysicalBlock(bits, isVoid, isIllegal);
    }

    public static PhysicalBlock Create(ulong low) => Create((UInt128)low);

    public static PhysicalBlock Create(ulong low, ulong high) => Create(new UInt128(high, low));

    public bool IsVoidExtent => _isVoidExtent;

    public bool IsIllegalEncoding => _isIllegalEncoding;

    public bool IsDualPlane
        => !_isVoidExtent && !_isIllegalEncoding && DecodeDualPlaneBit(BlockBits);

    internal (int Width, int Height)? GetWeightGridDimensions()
    {
        if (_isVoidExtent || _isIllegalEncoding) return null;
        var (props, _) = DecodeWeightProperties(BlockBits);
        return props is not null ? (props.Value.Width, props.Value.Height) : null;
    }

    internal int? GetWeightRange()
    {
        if (_isVoidExtent || _isIllegalEncoding) return null;
        var (props, _) = DecodeWeightProperties(BlockBits);
        return props?.Range;
    }

    internal int[]? GetVoidExtentCoordinates()
    {
        if (!_isVoidExtent) return null;

        // If void extent coords are all 1's then these are not valid void extent coords
        ulong voidExtentMask = 0xFFFFFFFFFFFFFDFFUL;
        ulong constBlockMode = 0xFFFFFFFFFFFFFDFCUL;

        return !_isIllegalEncoding && (voidExtentMask & BlockBits.Low()) != constBlockMode
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
        return _isVoidExtent
            ? IdentifyVoidExtentIssues(BlockBits)
            : IdentifyStandardIssues(BlockBits);
    }

    internal int? GetWeightBitCount()
    {
        if (_isVoidExtent) return null;
        return !_isIllegalEncoding ? DecodeNumWeightBits(BlockBits) : null;
    }

    internal int? GetWeightStartBit()
    {
        if (_isVoidExtent) return null;
        return !_isIllegalEncoding ? 128 - DecodeNumWeightBits(BlockBits) : null;
    }

    internal int? GetPartitionsCount()
    {
        if (_isVoidExtent) return null;
        return !_isIllegalEncoding ? DecodePartitionsCount(BlockBits) : null;
    }

    internal int? GetPartitionId()
    {
        var partitionsCount = GetPartitionsCount();

        return partitionsCount.HasValue && partitionsCount.Value != 1
            ? (int)BitOperations.GetBits(BlockBits.Low(), 13, 10)
            : null;
    }

    internal ColorEndpointMode? GetEndpointMode(int partition)
    {
        if (_isVoidExtent) return null;
        return partition >= 0 && DecodePartitionsCount(BlockBits) > partition
            ? DecodeEndpointMode(BlockBits, partition)
            : null;
    }

    internal int? GetColorStartBit()
    {
        if (_isVoidExtent) return 64;
        var numPartitions = GetPartitionsCount();
        if (!numPartitions.HasValue) return null;
        return (numPartitions.Value == 1) ? 17 : 29;
    }

    internal int? GetColorValuesCount()
    {
        if (_isVoidExtent) return 4;
        return !_isIllegalEncoding ? DecodeNumColorValues(BlockBits) : null;
    }

    internal int? GetColorBitCount()
    {
        if (_isVoidExtent) return 64;
        if (_isIllegalEncoding) return null;
        var (colorBits, _) = GetColorValuesInfo();
        return colorBits;
    }

    internal int? GetColorValuesRange()
    {
        if (_isVoidExtent) return (1 << 16) - 1;
        if (_isIllegalEncoding) return null;
        var (_, colorRange) = GetColorValuesInfo();
        return colorRange;
    }

    // --- Private helpers ---

    private (int colorBits, int colorRange) GetColorValuesInfo()
    {
        int dualPlaneStartPos = DecodeDualPlaneBitStartPosition(BlockBits);
        var colorStartBitOpt = GetColorStartBit();
        var numColorValuesOpt = GetColorValuesCount();
        if (!colorStartBitOpt.HasValue || !numColorValuesOpt.HasValue)
        {
            return (0, 0);
        }
        int maxColorBits = dualPlaneStartPos - colorStartBitOpt.Value;
        int numColorValues = numColorValuesOpt.Value;
        for (int range = byte.MaxValue; range > byte.MinValue; --range)
        {
            int bitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numColorValues, range);
            if (bitCount <= maxColorBits)
            {
                return (bitCount, range);
            }
        }

        throw new InvalidOperationException("Not enough bits to store color values");
    }

    /// <summary>
    /// Allocation-free check for void extent illegal encoding (used in hot path)
    /// </summary>
    private static bool CheckVoidExtentIsIllegal(UInt128 bits)
    {
        if (BitOperations.GetBits(bits, 10, 2).Low() != 0x3UL)
            return true;

        ulong low_bits = bits.Low();
        int c0 = (int)BitOperations.GetBits(low_bits, 12, 13);
        int c1 = (int)BitOperations.GetBits(low_bits, 25, 13);
        int c2 = (int)BitOperations.GetBits(low_bits, 38, 13);
        int c3 = (int)BitOperations.GetBits(low_bits, 51, 13);

        const int all1s = (1 << 13) - 1;
        bool coordsAll1s = c0 == all1s && c1 == all1s && c2 == all1s && c3 == all1s;

        return !coordsAll1s && (c0 >= c1 || c2 >= c3);
    }

    /// <summary>
    /// Full error-string version for void extent issues (used for error reporting)
    /// </summary>
    private static string? IdentifyVoidExtentIssues(UInt128 bits)
    {
        if (BitOperations.GetBits(bits, 10, 2).Low() != 0x3UL)
            return "Reserved bits set for void extent block";

        ulong low_bits = bits.Low();
        int c0 = (int)BitOperations.GetBits(low_bits, 12, 13);
        int c1 = (int)BitOperations.GetBits(low_bits, 25, 13);
        int c2 = (int)BitOperations.GetBits(low_bits, 38, 13);
        int c3 = (int)BitOperations.GetBits(low_bits, 51, 13);

        const int all1s = (1 << 13) - 1;
        bool coordsAll1s = c0 == all1s && c1 == all1s && c2 == all1s && c3 == all1s;

        if (!coordsAll1s && (c0 >= c1 || c2 >= c3))
            return "Void extent texture coordinates are invalid";

        return null;
    }

    private static string? IdentifyStandardIssues(UInt128 bits)
    {
        var (props, error) = DecodeWeightProperties(bits);
        if (props == null)
        {
            return error;
        }

        int numColorVals = DecodeNumColorValues(bits);
        if (numColorVals > 18) return "Too many color values";

        int numPartitions = DecodePartitionsCount(bits);
        int dualPlaneStartPos = DecodeDualPlaneBitStartPosition(bits);
        int colorStartBit = (numPartitions == 1) ? 17 : 29;

        int requiredColorBits = ((13 * numColorVals) + 4) / 5;
        int availableColorBits = dualPlaneStartPos - colorStartBit;
        if (availableColorBits < requiredColorBits) return "Not enough color bits";

        if (numPartitions == 4 && DecodeDualPlaneBit(bits))
            return "Both four partitions and dual plane specified";

        return null;
    }

    // --- Static decode methods ---

    internal static PhysicalBlockMode? DecodeBlockMode(UInt128 astcBits)
    {
        const int kVoidExtentMaskBits = 9;
        const uint kVoidExtentMask = 0x1FC;

        // The void-extent header is found in the low 64-bit word of the
        // canonical representation.
        if (BitOperations.GetBits(astcBits.Low(), 0, kVoidExtentMaskBits) == kVoidExtentMask)
        {
            return PhysicalBlockMode.VoidExtent;
        }

        // For decoding block mode fields the relevant bits live in the low
        // 64-bit word of the canonical representation. Use the stored low
        // word for the remaining logic.
        ulong low_bits = astcBits.Low();
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

    internal static (WeightGridDimensions? dimensions, string? error) DecodeWeightProperties(UInt128 astcBits)
    {
        string? error = null;
        var blockMode = DecodeBlockMode(astcBits);
        if (blockMode is null)
        {
            error = "Reserved block mode";
            return (null, error);
        }

        var props = new WeightGridDimensions();
        uint low32 = (uint)(astcBits.Low() & 0xFFFFFFFFUL);

        switch (blockMode.Value)
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
                return (null, "Void extent block has no weight grid");
            default:
                throw new InvalidOperationException($"Error decoding weight grid for block mode {blockMode}");
        }

        uint r = (uint)BitOperations.GetBits(low32, 4, 1);
        switch (blockMode.Value)
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
                return (null, "Internal error");
        }

        uint h = (uint)BitOperations.GetBits(low32, 9, 1);
        if (blockMode.Value == PhysicalBlockMode.WidthA6HeightB6) h = 0;

        int[] kWeightRanges = s_weightRanges;
        int idx = (int)((h << 3) | r);
        if (idx < 0 || idx >= kWeightRanges.Length)
        {
            uint altLow32 = (uint)(astcBits.High() & 0xFFFFFFFFUL);
            uint alt_r = (uint)BitOperations.GetBits(altLow32, 4, 1);
            switch (blockMode.Value)
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
            if (altIdx >= 0 && altIdx < kWeightRanges.Length && kWeightRanges[altIdx] >= 0)
            {
                r = alt_r; h = alt_h; idx = altIdx; low32 = altLow32;
            }
            else
            {
                string bits = "";
                for (int i = 0; i < 16; ++i)
                {
                    bits = (BitOperations.GetBits(low32, i, 1) == 1 ? '1' : '0') + bits;
                }
                return (null, "Reserved range for weight bits");
            }
        }
        if (idx < 0 || idx >= kWeightRanges.Length) { return (null, "Reserved range for weight bits"); }
        props.Range = kWeightRanges[idx];
        if (props.Range < 0) { return (null, "Reserved range for weight bits"); }

        int numWeights = props.Width * props.Height;
        if (DecodeDualPlaneBit(astcBits)) numWeights *= 2;
        const int kMaxNumWeights = 64;
        if (kMaxNumWeights < numWeights) return (null, "Too many weights specified");

        int bitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, props.Range);
        const int kWeightGridMinBitLength = 24;
        const int kWeightGridMaxBitLength = 96;
        if (bitCount < kWeightGridMinBitLength) return (null, "Too few bits required for weight grid");
        if (kWeightGridMaxBitLength < bitCount) return (null, "Too many bits required for weight grid");

        return (props, null);
    }

    internal static int[] DecodeVoidExtentCoordinates(UInt128 astcBits)
    {
        ulong low_bits = astcBits.Low();
        var coords = new int[4];
        for (int i = 0; i < 4; ++i)
        {
            coords[i] = (int)BitOperations.GetBits(low_bits, 12 + 13 * i, 13);
        }
        return coords;
    }

    internal static bool DecodeDualPlaneBit(UInt128 astcBits)
    {
        var blockMode = DecodeBlockMode(astcBits);
        if (blockMode == PhysicalBlockMode.VoidExtent) return false;
        if (blockMode == PhysicalBlockMode.WidthA6HeightB6) return false;
        const int kDualPlaneBitPosition = 10;
        return BitOperations.GetBits(astcBits, kDualPlaneBitPosition, 1).Low() != 0UL;
    }

    internal static int DecodePartitionsCount(UInt128 astcBits)
    {
        const int kNumPartitionsBitPosition = 11;
        const int kNumPartitionsBitLength = 2;
        ulong low_bits = astcBits.Low();
        int num_partitions = 1 + (int)BitOperations.GetBits(low_bits, kNumPartitionsBitPosition, kNumPartitionsBitLength);

        ArgumentOutOfRangeException.ThrowIfLessThan(num_partitions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(num_partitions, 4);

        return num_partitions;
    }

    internal static int DecodeNumWeightBits(UInt128 astcBits)
    {
        var (maybe, _) = DecodeWeightProperties(astcBits);
        if (maybe == null) return 0;
        var props = maybe.Value;
        int numWeights = props.Width * props.Height;
        if (DecodeDualPlaneBit(astcBits)) numWeights *= 2;

        return BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, props.Range);
    }

    internal static int DecodeNumExtraCEMBits(UInt128 astcBits)
    {
        int num_partitions = DecodePartitionsCount(astcBits);
        if (num_partitions == 1) return 0;
        const int kSharedCEMBitPosition = 23;
        const int kSharedCEMBitLength = 2;
        var shared_cem = BitOperations.GetBits(astcBits, kSharedCEMBitPosition, kSharedCEMBitLength);
            if (shared_cem.Low() == 0UL) return 0;
        return s_extraCemBitsForPartition[num_partitions - 1];
    }

    internal static int DecodeDualPlaneBitStartPosition(UInt128 astcBits)
    {
        int start_pos = 128 - DecodeNumWeightBits(astcBits) - DecodeNumExtraCEMBits(astcBits);
        if (DecodeDualPlaneBit(astcBits)) return start_pos - 2;

        return start_pos;
    }

    internal static ColorEndpointMode DecodeEndpointMode(UInt128 astcBits, int partition)
    {
        int num_partitions = DecodePartitionsCount(astcBits);
        ulong low_bits = astcBits.Low();
        ArgumentOutOfRangeException.ThrowIfLessThan(partition, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, num_partitions);

        if (num_partitions == 1)
        {
            ulong cem = BitOperations.GetBits(low_bits, 13, 4);
            return (ColorEndpointMode)cem;
        }

        if (DecodeNumExtraCEMBits(astcBits) == 0)
        {
            ulong shared_cem = BitOperations.GetBits(low_bits, 25, 4);
            return (ColorEndpointMode)shared_cem;
        }

        ulong cemval = BitOperations.GetBits(low_bits, 23, 6);
        int base_cem = (int)(((cemval & 0x3) - 1) * 4);
        cemval >>= 2;

        int num_extra_cem_bits = DecodeNumExtraCEMBits(astcBits);
        int extra_cem_start_pos = 128 - num_extra_cem_bits - DecodeNumWeightBits(astcBits);
        var extra_cem = BitOperations.GetBits(astcBits, extra_cem_start_pos, num_extra_cem_bits);
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

    internal static int DecodeNumColorValues(UInt128 astcBits)
    {
        int num_color_values = 0;
        int num_partitions = DecodePartitionsCount(astcBits);
        for (int i = 0; i < num_partitions; ++i)
        {
            var endpoint_mode = DecodeEndpointMode(astcBits, i);
            num_color_values += endpoint_mode.GetColorValuesCount();
        }
        return num_color_values;
    }

    internal record struct WeightGridDimensions(int Width, int Height, int Range);
}
