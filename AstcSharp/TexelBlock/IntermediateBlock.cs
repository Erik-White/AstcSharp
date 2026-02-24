using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.IO;

namespace AstcSharp.TexelBlock;

internal static class IntermediateBlock
{
    // From Table C.2.7 -- valid weight ranges
    public static readonly int[] ValidWeightRanges = [1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31];

    // Returns the maximum endpoint value range or negative on error
    private const int EndpointRangeInvalidWeightDimensions = -1;
    private const int EndpointRangeNotEnoughColorBits = -2;

    private static readonly BlockModeInfo[] _blockModeInfoTable = new BlockModeInfo[]{
        new BlockModeInfo{ minWeightGridDimX=4, maxWeightGridDimX=7, minWeightGridDimY=2, maxWeightGridDimY=5, r0BitPos=4, r1BitPos=0, r2BitPos=1, weightGridXOffsetBitPos=7, weightGridYOffsetBitPos=5, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=8, maxWeightGridDimX=11, minWeightGridDimY=2, maxWeightGridDimY=5, r0BitPos=4, r1BitPos=0, r2BitPos=1, weightGridXOffsetBitPos=7, weightGridYOffsetBitPos=5, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=2, maxWeightGridDimX=5, minWeightGridDimY=8, maxWeightGridDimY=11, r0BitPos=4, r1BitPos=0, r2BitPos=1, weightGridXOffsetBitPos=5, weightGridYOffsetBitPos=7, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=2, maxWeightGridDimX=5, minWeightGridDimY=6, maxWeightGridDimY=7, r0BitPos=4, r1BitPos=0, r2BitPos=1, weightGridXOffsetBitPos=5, weightGridYOffsetBitPos=7, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=2, maxWeightGridDimX=3, minWeightGridDimY=2, maxWeightGridDimY=5, r0BitPos=4, r1BitPos=0, r2BitPos=1, weightGridXOffsetBitPos=7, weightGridYOffsetBitPos=5, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=12, maxWeightGridDimX=12, minWeightGridDimY=2, maxWeightGridDimY=5, r0BitPos=4, r1BitPos=2, r2BitPos=3, weightGridXOffsetBitPos=-1, weightGridYOffsetBitPos=5, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=2, maxWeightGridDimX=5, minWeightGridDimY=12, maxWeightGridDimY=12, r0BitPos=4, r1BitPos=2, r2BitPos=3, weightGridXOffsetBitPos=5, weightGridYOffsetBitPos=-1, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=6, maxWeightGridDimX=6, minWeightGridDimY=10, maxWeightGridDimY=10, r0BitPos=4, r1BitPos=2, r2BitPos=3, weightGridXOffsetBitPos=-1, weightGridYOffsetBitPos=-1, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=10, maxWeightGridDimX=10, minWeightGridDimY=6, maxWeightGridDimY=6, r0BitPos=4, r1BitPos=2, r2BitPos=3, weightGridXOffsetBitPos=-1, weightGridYOffsetBitPos=-1, requireSinglePlaneLowPrec=false },
        new BlockModeInfo{ minWeightGridDimX=6, maxWeightGridDimX=9, minWeightGridDimY=6, maxWeightGridDimY=9, r0BitPos=4, r1BitPos=2, r2BitPos=3, weightGridXOffsetBitPos=5, weightGridYOffsetBitPos=9, requireSinglePlaneLowPrec=true }
    };

    private static readonly uint[] _blockModeMasks = { 0x0u, 0x4u, 0x8u, 0xCu, 0x10Cu, 0x0u, 0x80u, 0x180u, 0x1A0u, 0x100u };

    public static IntermediateBlockData? UnpackIntermediateBlock(PhysicalBlock physicalBlock)
    {
        if (physicalBlock.IsIllegalEncoding || physicalBlock.IsVoidExtent)
            return null;

        var info = BlockInfo.Decode(physicalBlock.BlockBits);
        if (!info.IsValid || info.IsVoidExtent)
            return null;

        return UnpackIntermediateBlock(physicalBlock.BlockBits, in info);
    }

    /// <summary>
    /// Fast overload that uses pre-computed BlockInfo instead of calling PhysicalBlock getters.
    /// </summary>
    public static IntermediateBlockData? UnpackIntermediateBlock(UInt128 bits, in BlockInfo info)
    {
        if (!info.IsValid || info.IsVoidExtent) return null;

        var data = new IntermediateBlockData();

        // Use cached values from BlockInfo instead of PhysicalBlock getters
        var colorBitMask = UInt128Extensions.OnesMask(info.ColorBitCount);
        var colorBits = (bits >> info.ColorStartBit) & colorBitMask;
        var colorBitStream = new BitStream(colorBits, 128);

        var colorDecoder = BoundedIntegerSequenceDecoder.GetCached(info.ColorValuesRange);
        Span<int> colors = stackalloc int[info.ColorValuesCount];
        colorDecoder.Decode(info.ColorValuesCount, ref colorBitStream, colors);

        data.weightGridX = info.GridWidth;
        data.weightGridY = info.GridHeight;
        data.weightRange = info.WeightRange;

        data.partitionId = info.PartitionCount > 1
            ? (int)BitOperations.GetBits(bits.Low(), 13, 10)
            : null;

        data.dualPlaneChannel = info.IsDualPlane ? info.DualPlaneChannel : null;

        int colorIndex = 0;
        data.endpointCount = info.PartitionCount;
        for (int i = 0; i < info.PartitionCount; ++i)
        {
            var mode = info.GetEndpointMode(i);
            int colorCount = mode.GetColorValuesCount();
            var ep = new IntermediateEndpointData { mode = mode, colorCount = colorCount };
            for (int j = 0; j < colorCount; ++j)
            {
                ep.colors[j] = colors[colorIndex++];
            }
            data.endpoints[i] = ep;
        }

        data.endpointRange = info.ColorValuesRange;

        var weightBits = UInt128Extensions.ReverseBits(bits) & UInt128Extensions.OnesMask(info.WeightBitCount);
        var weightBitStream = new BitStream(weightBits, 128);

        var weightDecoder = BoundedIntegerSequenceDecoder.GetCached(data.weightRange);
        int weightsCount = data.weightGridX * data.weightGridY;
        if (info.IsDualPlane) weightsCount *= 2;
        data.weights = new int[weightsCount];
        data.weightsCount = weightsCount;
        weightDecoder.Decode(weightsCount, ref weightBitStream, data.weights);

        return data;
    }

    public static int EndpointRangeForBlock(in IntermediateBlockData data)
    {
        if (BoundedIntegerSequenceCodec.GetBitCountForRange(data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1), data.weightRange) > 96)
            return EndpointRangeInvalidWeightDimensions;

        int partitionCount = data.endpointCount;
        int bitsWrittenCount = 11 + 2 + ((partitionCount > 1) ? 10 : 0) + ((partitionCount == 1) ? 4 : 6);
        int availableColorBitsCount = ExtraConfigBitPosition(data) - bitsWrittenCount;

        int colorValuesCount = 0;
        for (int i = 0; i < data.endpointCount; i++) colorValuesCount += data.endpoints[i].mode.GetColorValuesCount();

        int bitsNeededCount = (13 * colorValuesCount + 4) / 5;
        if (availableColorBitsCount < bitsNeededCount) return EndpointRangeNotEnoughColorBits;

        int colorValueRange = byte.MaxValue;
        for (; colorValueRange > 1; --colorValueRange)
        {
            int bitCountForRange = BoundedIntegerSequenceCodec.GetBitCountForRange(colorValuesCount, colorValueRange);
            if (bitCountForRange <= availableColorBitsCount) break;
        }
        return colorValueRange;
    }

    public static VoidExtentData? UnpackVoidExtent(PhysicalBlock physicalBlock)
    {
        var colorStartBit = physicalBlock.GetColorStartBit();
        var colorBitCount = physicalBlock.GetColorBitCount();
        if (physicalBlock.IsIllegalEncoding || !physicalBlock.IsVoidExtent || colorStartBit is null || colorBitCount is null)
            return null;

        var colorBits = (physicalBlock.BlockBits >> colorStartBit.Value) & UInt128Extensions.OnesMask(colorBitCount.Value);
        // We expect low 64 bits contain the 4x16-bit channels
        var low = colorBits.Low();

        var data = new VoidExtentData();
        // Bit 9 of the block mode indicates HDR (1) vs LDR (0) void extent
        data.isHdr = (physicalBlock.BlockBits.Low() & (1UL << 9)) != 0;
        data.r = (ushort)((low >> 0) & 0xFFFF);
        data.g = (ushort)((low >> 16) & 0xFFFF);
        data.b = (ushort)((low >> 32) & 0xFFFF);
        data.a = (ushort)((low >> 48) & 0xFFFF);

        var coords = physicalBlock.GetVoidExtentCoordinates();
        data.coords = new ushort[4];
        if (coords != null)
        {
            data.coords[0] = (ushort)coords[0];
            data.coords[1] = (ushort)coords[1];
            data.coords[2] = (ushort)coords[2];
            data.coords[3] = (ushort)coords[3];
        }
        else
        {
            ushort allOnes = (ushort)((1 << 13) - 1);
            for (int i = 0; i < 4; ++i) data.coords[i] = allOnes;
        }

        return data;
    }

    public static (string? error, UInt128 physicalBlockBits) Pack(in IntermediateBlockData data)
    {
        UInt128 physicalBlockBits = 0;
        int expectedWeightsCount = data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1);
        int actualWeightsCount = data.weightsCount > 0 ? data.weightsCount : (data.weights?.Length ?? 0);
        if (actualWeightsCount != expectedWeightsCount)
        {
            return ("Incorrect number of weights!", 0);
        }

        var bitSink = new BitStream(0UL, 0);

        // First we need to encode the block mode.
        var errorMessage = PackBlockMode(data.weightGridX, data.weightGridY, data.weightRange, data.dualPlaneChannel.HasValue, ref bitSink);
        if (errorMessage != null) { return (errorMessage, 0); }

        // number of partitions minus one
        int partitionCount = data.endpointCount;
        bitSink.PutBits((uint)(partitionCount - 1), 2);

        if (partitionCount > 1)
        {
            int id = data.partitionId ?? 0;
            ArgumentOutOfRangeException.ThrowIfLessThan(id, 0);
            bitSink.PutBits((uint)id, 10);
        }

        var (weightSink, weightBitsCount) = EncodeWeights(data);

        var (error, extraConfig) = EncodeColorEndpointModes(data, partitionCount, ref bitSink);
        if (error != null) return (error, 0);

        int colorValueRange = data.endpointRange.HasValue ? data.endpointRange.Value : EndpointRangeForBlock(data);
        if (colorValueRange == EndpointRangeInvalidWeightDimensions)
            throw new InvalidOperationException($"{nameof(colorValueRange)} must not be {nameof(EndpointRangeInvalidWeightDimensions)}");
        if (colorValueRange == EndpointRangeNotEnoughColorBits)
        {
            return ("Intermediate block emits illegal color range", 0);
        }

        var colorEncoder = new BoundedIntegerSequenceEncoder(colorValueRange);
        for (int i = 0; i < data.endpointCount; i++)
        {
            var ep = data.endpoints[i];
            for (int j = 0; j < ep.colorCount; j++)
            {
                int color = ep.colors[j];
                if (color > colorValueRange) return ("Color outside available color range!", 0);
                colorEncoder.AddValue(color);
            }
        }
        colorEncoder.Encode(ref bitSink);

        int extraConfigBitPosition = ExtraConfigBitPosition(data);
        int extraConfigBits = 128 - weightBitsCount - extraConfigBitPosition;

        ArgumentOutOfRangeException.ThrowIfNegative(extraConfigBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(extraConfig, 1 << extraConfigBits);

        int bitsToSkip = extraConfigBitPosition - (int)bitSink.Bits;
        ArgumentOutOfRangeException.ThrowIfNegative(bitsToSkip);
        while (bitsToSkip > 0)
        {
            int skipping = Math.Min(32, bitsToSkip);
            bitSink.PutBits(0u, skipping);
            bitsToSkip -= skipping;
        }

        if (extraConfigBits > 0)
        {
            bitSink.PutBits((uint)extraConfig, extraConfigBits);
        }

        ArgumentOutOfRangeException.ThrowIfNotEqual(bitSink.Bits, (uint)128 - weightBitsCount);

        // Flush out the bit writer
        if (!bitSink.TryGetBits<UInt128>(128 - weightBitsCount, out var astcBits))
            throw new InvalidOperationException();
        if (!weightSink.TryGetBits<UInt128>(weightBitsCount, out var revWeightBits))
            throw new InvalidOperationException();

        var combined = astcBits | UInt128Extensions.ReverseBits(revWeightBits);
        physicalBlockBits = combined;

        var block = PhysicalBlock.Create(physicalBlockBits);
        var illegal = block.IdentifyInvalidEncodingIssues();

        return (illegal, physicalBlockBits);
    }

    public static (string? error, UInt128 physicalBlockBits) Pack(VoidExtentData data)
    {
        // Pack void extent
        // Assemble the 128-bit value explicitly: low 64 bits = RGBA (4x16)
        // high 64 bits = 12-bit header (0xDFC) followed by four 13-bit coords.
        ulong high64 = ((ulong)data.a << 48) | ((ulong)data.b << 32) | ((ulong)data.g << 16) | (ulong)data.r;
        ulong low64 = 0UL;
        // Header occupies lowest 12 bits of the high word
        low64 |= 0xDFCu;
        for (int i = 0; i < 4; ++i)
        {
            low64 |= ((ulong)(data.coords[i] & 0x1FFF)) << (12 + 13 * i);
        }

        UInt128 physicalBlockBits;
        // Decide representation: if the RGBA low word is zero we emit the
        // compact single-ulong representation (low word = header+coords,
        // high word = 0) to match the reference tests. Otherwise the
        // low word holds RGBA and the high word holds header+coords.
        if (high64 == 0UL)
        {
            physicalBlockBits = (UInt128)low64;
            // using compact void extent representation
        }
        else
        {
            physicalBlockBits = new UInt128(high64, low64);
            // using full void extent representation
        }

        var block = PhysicalBlock.Create(physicalBlockBits);
        var illegal = block.IdentifyInvalidEncodingIssues();
        if (illegal is not null)
        {
            throw new InvalidOperationException($"{nameof(Pack)}(void extent) produced illegal encoding");
        }
        return (illegal, physicalBlockBits);
    }

    private static (string? error, int[] range) GetEncodedWeightRange(int range)
    {
        var kValidRangeEncodings = new int[][]{
            new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1},
            new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1}
        };

        int smallestRange = ValidWeightRanges.First();
        int largestRange = ValidWeightRanges.Last();
        if (range < smallestRange || largestRange < range)
        {
            return ($"Could not find block mode. Invalid weight range: {range} not in [{smallestRange}, {largestRange}]", new int[3]);
        }

        int index = Array.FindIndex(ValidWeightRanges, v => v >= range);
        if (index < 0) index = ValidWeightRanges.Length - 1;
        var encoding = kValidRangeEncodings[index];
        return (null, [encoding[0], encoding[1], encoding[2]]);
    }

    private static string? PackBlockMode(int dimX, int dimY, int range, bool dualPlane, ref BitStream bitSink)
    {
        bool highPrec = range > 7;
        var (maybeErr, rangeValues) = GetEncodedWeightRange(range);
        if (maybeErr != null) return maybeErr;

        // Ensure top two bits of r1 and r2 not both zero per reference
        if ((rangeValues[1] | rangeValues[2]) <= 0)
            throw new InvalidOperationException($"{nameof(rangeValues)}[1] | {nameof(rangeValues)}[2] must be > 0");

        for (int mode = 0; mode < _blockModeInfoTable.Length; ++mode)
        {
            var blockMode = _blockModeInfoTable[mode];
            bool isValidMode = true;
            isValidMode &= blockMode.minWeightGridDimX <= dimX;
            isValidMode &= dimX <= blockMode.maxWeightGridDimX;
            isValidMode &= blockMode.minWeightGridDimY <= dimY;
            isValidMode &= dimY <= blockMode.maxWeightGridDimY;
            isValidMode &= !(blockMode.requireSinglePlaneLowPrec && dualPlane);
            isValidMode &= !(blockMode.requireSinglePlaneLowPrec && highPrec);

            if (!isValidMode) continue;

            uint encodedMode = _blockModeMasks[mode];
            void setBit(uint value, int offset)
            {
                if (offset < 0) return;
                encodedMode = (encodedMode & ~(1u << offset)) | ((value & 1u) << offset);
            }

            setBit((uint)rangeValues[0], blockMode.r0BitPos);
            setBit((uint)rangeValues[1], blockMode.r1BitPos);
            setBit((uint)rangeValues[2], blockMode.r2BitPos);

            int offsetX = dimX - blockMode.minWeightGridDimX;
            int offsetY = dimY - blockMode.minWeightGridDimY;

            if (blockMode.weightGridXOffsetBitPos >= 0)
            {
                encodedMode |= (uint)(offsetX << blockMode.weightGridXOffsetBitPos);
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(offsetX, 0);
            }

            if (blockMode.weightGridYOffsetBitPos >= 0)
            {
                encodedMode |= (uint)(offsetY << blockMode.weightGridYOffsetBitPos);
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(offsetY, 0);
            }

            if (!blockMode.requireSinglePlaneLowPrec)
            {
                setBit((uint)(highPrec ? 1u : 0u), 9);
                setBit((uint)(dualPlane ? 1u : 0u), 10);
            }

            if (bitSink.Bits != 0)
                throw new InvalidOperationException($"{nameof(bitSink)}.{nameof(bitSink.Bits)} must be 0");
            bitSink.PutBits(encodedMode, 11);
            return null;
        }

        return "Could not find viable block mode";
    }

    /// <summary>
    /// Determines if all endpoint modes in the intermediate block data are the same
    /// </summary>
    private static bool SharedEndpointModes(in IntermediateBlockData data)
    {
        if (data.endpointCount == 0) return true;
        var first = data.endpoints[0].mode;
        for (int i = 1; i < data.endpointCount; i++)
            if (data.endpoints[i].mode != first) return false;
        return true;
    }

    private static (BitStream weightSink, int weightBitsCount) EncodeWeights(in IntermediateBlockData data)
    {
        var weightSink = new BitStream(0UL, 0);
        var weightsEncoder = new BoundedIntegerSequenceEncoder(data.weightRange);
        int weightCount = data.weightsCount > 0 ? data.weightsCount : (data.weights?.Length ?? 0);
        if (data.weights is null)
            throw new InvalidOperationException($"{nameof(data.weights)} is null in {nameof(EncodeWeights)}");
        for (var i = 0; i < weightCount; i++) weightsEncoder.AddValue(data.weights[i]);
        weightsEncoder.Encode(ref weightSink);

        int weightBitsCount = (int)weightSink.Bits;
        if ((int)weightSink.Bits != BoundedIntegerSequenceCodec.GetBitCountForRange(weightCount, data.weightRange))
            throw new InvalidOperationException($"{nameof(weightSink)}.{nameof(weightSink.Bits)} does not match expected bit count");

        return (weightSink, weightBitsCount);
    }

    private static (string? error, int extraConfig) EncodeColorEndpointModes(in IntermediateBlockData data, int partitionCount, ref BitStream bitSink)
    {
        int extraConfig = 0;
        bool sharedEndpointMode = SharedEndpointModes(data);

        if (sharedEndpointMode)
        {
            if (partitionCount > 1) bitSink.PutBits(0u, 2);
            bitSink.PutBits((uint)data.endpoints[0].mode, 4);
        }
        else
        {
            // compute min_class, max_class
            int minClass = 2; int maxClass = 0;
            for (int i = 0; i < data.endpointCount; i++)
            {
                int endpointModeClass = ((int)data.endpoints[i].mode) >> 2;
                minClass = Math.Min(minClass, endpointModeClass);
                maxClass = Math.Max(maxClass, endpointModeClass);
            }

            if (maxClass - minClass > 1) return ("Endpoint modes are invalid", 0);

            var cemEncoder = new BitStream(0UL, 0);
            cemEncoder.PutBits((uint)(minClass + 1), 2);

            for (int i = 0; i < data.endpointCount; i++)
            {
                int endpointModeClass = ((int)data.endpoints[i].mode) >> 2;
                int classSelectorBit = endpointModeClass - minClass;
                cemEncoder.PutBits(classSelectorBit, 1);
            }

            for (int i = 0; i < data.endpointCount; i++)
            {
                int epMode = ((int)data.endpoints[i].mode) & 3;
                cemEncoder.PutBits(epMode, 2);
            }

            int cemBits = 2 + partitionCount * 3;
            if (!cemEncoder.TryGetBits(cemBits, out uint encodedCem))
                throw new InvalidOperationException();

            extraConfig = (int)(encodedCem >> 6);

            bitSink.PutBits(encodedCem, Math.Min(6, cemBits));
        }

        // dual plane channel
        if (data.dualPlaneChannel.HasValue)
        {
            int channel = data.dualPlaneChannel.Value;
            ArgumentOutOfRangeException.ThrowIfLessThan(channel, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 3);
            extraConfig = (extraConfig << 2) | channel;
        }

        return (null, extraConfig);
    }

    private static int ExtraConfigBitPosition(in IntermediateBlockData data)
    {
        bool hasDualChannel = data.dualPlaneChannel.HasValue;
        int weightCount = data.weightGridX * data.weightGridY * (hasDualChannel ? 2 : 1);
        int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(weightCount, data.weightRange);

        int extraConfigBitCount = 0;
        if (!SharedEndpointModes(data))
        {
            int encodedCemBitCount = 2 + data.endpointCount * 3;
            extraConfigBitCount = encodedCemBitCount - 6;
        }

        if (hasDualChannel) extraConfigBitCount += 2;

        return 128 - weightBitCount - extraConfigBitCount;
    }

    internal struct VoidExtentData
    {
        public bool isHdr;
        public ushort r;
        public ushort g;
        public ushort b;
        public ushort a;
        public ushort[] coords; // length 4
    }

    [System.Runtime.CompilerServices.InlineArray(MaxColorValues)]
    internal struct EndpointColorValues
    {
        public const int MaxColorValues = 8;
#pragma warning disable CS0169, S1144 // Accessed by runtime via [InlineArray]
        private int _element0;
#pragma warning restore CS0169, S1144
    }

    internal struct IntermediateBlockData
    {
        public int weightGridX;
        public int weightGridY;
        public int weightRange;

        public int[] weights;
        public int weightsCount;

        public int? partitionId;
        public int? dualPlaneChannel;

        public IntermediateEndpointBuffer endpoints;
        public int endpointCount;

        public int? endpointRange;
    }

    internal struct IntermediateEndpointData
    {
        public ColorEndpointMode mode;
        public EndpointColorValues colors;
        public int colorCount;
    }

    [System.Runtime.CompilerServices.InlineArray(MaxPartitions)]
    internal struct IntermediateEndpointBuffer
    {
        public const int MaxPartitions = 4;
#pragma warning disable CS0169, S1144 // Accessed by runtime via [InlineArray]
        private IntermediateEndpointData _element0;
#pragma warning restore CS0169, S1144
    }

    private struct BlockModeInfo
    {
        public int minWeightGridDimX;
        public int maxWeightGridDimX;
        public int minWeightGridDimY;
        public int maxWeightGridDimY;
        public int r0BitPos;
        public int r1BitPos;
        public int r2BitPos;
        public int weightGridXOffsetBitPos;
        public int weightGridYOffsetBitPos;
        public bool requireSinglePlaneLowPrec;
    }
}
