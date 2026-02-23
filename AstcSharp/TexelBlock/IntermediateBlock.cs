using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.IO;

namespace AstcSharp.TexelBlock;

// From Table C.2.7 -- valid weight ranges
internal static class IntermediateBlock
{
    public static readonly int[] kValidWeightRanges = [1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31];

    internal struct VoidExtentData
    {
        public bool isHdr;
        public ushort r;
        public ushort g;
        public ushort b;
        public ushort a;
        public ushort[] coords; // length 4
    }

    internal class IntermediateEndpointData
    {
        public ColorEndpointMode mode;
        public List<int> colors = new List<int>();
    }

    internal class IntermediateBlockData
    {
        public int weightGridX;
        public int weightGridY;
        public int weightRange;

        public int[] weights = [];

        public int? partitionId;
        public int? dualPlaneChannel;

        public List<IntermediateEndpointData> endpoints = new List<IntermediateEndpointData>();

        public int? endpointRange;
    }

    // Returns the maximum endpoint value range or negative on error
    private const int kEndpointRange_ReturnInvalidWeightDims = -1;
    private const int kEndpointRange_ReturnNotEnoughColorBits = -2;

    private static (string? error, int[] range) GetEncodedWeightRange(int range)
    {
        var kValidRangeEncodings = new int[][]{
            new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1},
            new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1}
        };

        int smallest_range = kValidWeightRanges.First();
        int largest_range = kValidWeightRanges.Last();
        if (range < smallest_range || largest_range < range)
        {
            return ($"Could not find block mode. Invalid weight range: {range} not in [{smallest_range}, {largest_range}]", new int[3]);
        }

        int idx = Array.FindIndex(kValidWeightRanges, v => v >= range);
        if (idx < 0) idx = kValidWeightRanges.Length - 1;
        var enc = kValidRangeEncodings[idx];
        return (null, [enc[0], enc[1], enc[2]]);
    }

    private struct BlockModeInfo
    {
        public int min_weight_grid_dim_x;
        public int max_weight_grid_dim_x;
        public int min_weight_grid_dim_y;
        public int max_weight_grid_dim_y;
        public int r0_bit_pos;
        public int r1_bit_pos;
        public int r2_bit_pos;
        public int weight_grid_x_offset_bit_pos;
        public int weight_grid_y_offset_bit_pos;
        public bool require_single_plane_low_prec;
    }

    private static readonly BlockModeInfo[] kBlockModeInfo = new BlockModeInfo[]{
        new BlockModeInfo{ min_weight_grid_dim_x=4, max_weight_grid_dim_x=7, min_weight_grid_dim_y=2, max_weight_grid_dim_y=5, r0_bit_pos=4, r1_bit_pos=0, r2_bit_pos=1, weight_grid_x_offset_bit_pos=7, weight_grid_y_offset_bit_pos=5, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=8, max_weight_grid_dim_x=11, min_weight_grid_dim_y=2, max_weight_grid_dim_y=5, r0_bit_pos=4, r1_bit_pos=0, r2_bit_pos=1, weight_grid_x_offset_bit_pos=7, weight_grid_y_offset_bit_pos=5, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=2, max_weight_grid_dim_x=5, min_weight_grid_dim_y=8, max_weight_grid_dim_y=11, r0_bit_pos=4, r1_bit_pos=0, r2_bit_pos=1, weight_grid_x_offset_bit_pos=5, weight_grid_y_offset_bit_pos=7, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=2, max_weight_grid_dim_x=5, min_weight_grid_dim_y=6, max_weight_grid_dim_y=7, r0_bit_pos=4, r1_bit_pos=0, r2_bit_pos=1, weight_grid_x_offset_bit_pos=5, weight_grid_y_offset_bit_pos=7, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=2, max_weight_grid_dim_x=3, min_weight_grid_dim_y=2, max_weight_grid_dim_y=5, r0_bit_pos=4, r1_bit_pos=0, r2_bit_pos=1, weight_grid_x_offset_bit_pos=7, weight_grid_y_offset_bit_pos=5, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=12, max_weight_grid_dim_x=12, min_weight_grid_dim_y=2, max_weight_grid_dim_y=5, r0_bit_pos=4, r1_bit_pos=2, r2_bit_pos=3, weight_grid_x_offset_bit_pos=-1, weight_grid_y_offset_bit_pos=5, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=2, max_weight_grid_dim_x=5, min_weight_grid_dim_y=12, max_weight_grid_dim_y=12, r0_bit_pos=4, r1_bit_pos=2, r2_bit_pos=3, weight_grid_x_offset_bit_pos=5, weight_grid_y_offset_bit_pos=-1, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=6, max_weight_grid_dim_x=6, min_weight_grid_dim_y=10, max_weight_grid_dim_y=10, r0_bit_pos=4, r1_bit_pos=2, r2_bit_pos=3, weight_grid_x_offset_bit_pos=-1, weight_grid_y_offset_bit_pos=-1, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=10, max_weight_grid_dim_x=10, min_weight_grid_dim_y=6, max_weight_grid_dim_y=6, r0_bit_pos=4, r1_bit_pos=2, r2_bit_pos=3, weight_grid_x_offset_bit_pos=-1, weight_grid_y_offset_bit_pos=-1, require_single_plane_low_prec=false },
        new BlockModeInfo{ min_weight_grid_dim_x=6, max_weight_grid_dim_x=9, min_weight_grid_dim_y=6, max_weight_grid_dim_y=9, r0_bit_pos=4, r1_bit_pos=2, r2_bit_pos=3, weight_grid_x_offset_bit_pos=5, weight_grid_y_offset_bit_pos=9, require_single_plane_low_prec=true }
    };

    private static readonly uint[] kBlockModeMask = { 0x0u, 0x4u, 0x8u, 0xCu, 0x10Cu, 0x0u, 0x80u, 0x180u, 0x1A0u, 0x100u };

    private static string? PackBlockMode(int dimX, int dimY, int range, bool dualPlane, BitStream bitSink)
    {
        bool highPrec = range > 7;
        var (maybeErr, rvals) = GetEncodedWeightRange(range);
        if (maybeErr != null) return maybeErr;

        // Ensure top two bits of r1 and r2 not both zero per reference
        if ((rvals[1] | rvals[2]) <= 0)
            throw new InvalidOperationException($"{nameof(rvals)}[1] | {nameof(rvals)}[2] must be > 0");

        for (int mode = 0; mode < kBlockModeInfo.Length; ++mode)
        {
            var blockMode = kBlockModeInfo[mode];
            bool isValidMode = true;
            isValidMode &= blockMode.min_weight_grid_dim_x <= dimX;
            isValidMode &= dimX <= blockMode.max_weight_grid_dim_x;
            isValidMode &= blockMode.min_weight_grid_dim_y <= dimY;
            isValidMode &= dimY <= blockMode.max_weight_grid_dim_y;
            isValidMode &= !(blockMode.require_single_plane_low_prec && dualPlane);
            isValidMode &= !(blockMode.require_single_plane_low_prec && highPrec);

            if (!isValidMode) continue;

            uint encoded_mode = kBlockModeMask[mode];
            void setBit(uint value, int offset)
            {
                if (offset < 0) return;
                encoded_mode = (encoded_mode & ~(1u << offset)) | ((value & 1u) << offset);
            }

            setBit((uint)rvals[0], blockMode.r0_bit_pos);
            setBit((uint)rvals[1], blockMode.r1_bit_pos);
            setBit((uint)rvals[2], blockMode.r2_bit_pos);

            int offsetX = dimX - blockMode.min_weight_grid_dim_x;
            int offsetY = dimY - blockMode.min_weight_grid_dim_y;

            if (blockMode.weight_grid_x_offset_bit_pos >= 0)
            {
                encoded_mode |= (uint)(offsetX << blockMode.weight_grid_x_offset_bit_pos);
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(offsetX, 0);
            }

            if (blockMode.weight_grid_y_offset_bit_pos >= 0)
            {
                encoded_mode |= (uint)(offsetY << blockMode.weight_grid_y_offset_bit_pos);
            }
            else
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(offsetY, 0);
            }

            if (!blockMode.require_single_plane_low_prec)
            {
                setBit((uint)(highPrec ? 1u : 0u), 9);
                setBit((uint)(dualPlane ? 1u : 0u), 10);
            }

            if (bitSink.Bits != 0)
                throw new InvalidOperationException($"{nameof(bitSink)}.{nameof(bitSink.Bits)} must be 0");
            bitSink.PutBits(encoded_mode, 11);
            return null;
        }

        return "Could not find viable block mode";
    }

    /// <summary>
    /// Determines if all endpoint modes in the intermediate block data are the same
    /// </summary>
    private static bool SharedEndpointModes(IntermediateBlockData data)
        => data.endpoints.Count == 0 || data.endpoints.All(ep => ep.mode == data.endpoints[0].mode);

    private static (BitStream weightSink, int weightBitsCount) EncodeWeights(IntermediateBlockData data)
    {
        var weightSink = new BitStream(0UL, 0);
        var weightsEncoder = new BoundedIntegerSequenceEncoder(data.weightRange);
        foreach (var weight in data.weights) weightsEncoder.AddValue(weight);
        weightsEncoder.Encode(ref weightSink);

        int weightBitsCount = (int)weightSink.Bits;
        if ((int)weightSink.Bits != BoundedIntegerSequenceCodec.GetBitCountForRange(data.weights.Length, data.weightRange))
            throw new InvalidOperationException($"{nameof(weightSink)}.{nameof(weightSink.Bits)} does not match expected bit count");

        return (weightSink, weightBitsCount);
    }

    private static (string? error, int extraConfig) EncodeColorEndpointModes(IntermediateBlockData data, int partitionCount, BitStream bitSink)
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
            foreach (var ep in data.endpoints)
            {
                int endpointModeClass = ((int)ep.mode) >> 2;
                minClass = Math.Min(minClass, endpointModeClass);
                maxClass = Math.Max(maxClass, endpointModeClass);
            }

            if (maxClass - minClass > 1) return ("Endpoint modes are invalid", 0);

            var cemEncoder = new BitStream(0UL, 0);
            cemEncoder.PutBits((uint)(minClass + 1), 2);

            foreach (var endpoint in data.endpoints)
            {
                int endpointModeClass = ((int)endpoint.mode) >> 2;
                int classSelectorBit = endpointModeClass - minClass;
                cemEncoder.PutBits(classSelectorBit, 1);
            }

            foreach (var ep in data.endpoints)
            {
                int epMode = ((int)ep.mode) & 3;
                cemEncoder.PutBits(epMode, 2);
            }

            int cemBits = 2 + partitionCount * 3;
            if (!cemEncoder.TryGetBits<uint>(cemBits, out var encodedCem))
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

    private static int ExtraConfigBitPosition(IntermediateBlockData data)
    {
        bool has_dual_channel = data.dualPlaneChannel.HasValue;
        int num_weights = data.weightGridX * data.weightGridY * (has_dual_channel ? 2 : 1);
        int num_weight_bits = BoundedIntegerSequenceCodec.GetBitCountForRange(num_weights, data.weightRange);

        int extra_config_bits = 0;
        if (!SharedEndpointModes(data))
        {
            int num_encoded_cem_bits = 2 + data.endpoints.Count * 3;
            extra_config_bits = num_encoded_cem_bits - 6;
        }

        if (has_dual_channel) extra_config_bits += 2;

        return 128 - num_weight_bits - extra_config_bits;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UInt128> s_lastUnpacked = new System.Collections.Concurrent.ConcurrentDictionary<string, UInt128>();

    public static IntermediateBlockData? UnpackIntermediateBlock(PhysicalBlock physicalBlock)
    {
        if (physicalBlock.IsIllegalEncoding) return null;
        if (physicalBlock.IsVoidExtent) return null;

        var data = new IntermediateBlockData();

        var colorBitCount = physicalBlock.GetColorBitCount();
        var colorStartBit = physicalBlock.GetColorStartBit();
        var colorValuesRangeOpt = physicalBlock.GetColorValuesRange();
        var colorValuesCount = physicalBlock.GetColorValuesCount();
        var weightGridDimensions = physicalBlock.GetWeightGridDimensions();
        var weightRange = physicalBlock.GetWeightRange();
        var partitionCount = physicalBlock.GetPartitionsCount();
        var weightBitCount = physicalBlock.GetWeightBitCount();

        if (!colorBitCount.HasValue || !colorStartBit.HasValue || !colorValuesRangeOpt.HasValue || !colorValuesCount.HasValue || !weightGridDimensions.HasValue || !weightRange.HasValue || !partitionCount.HasValue || !weightBitCount.HasValue)
            return null;

        var colorBitMask = UInt128Extensions.OnesMask(colorBitCount.Value);
        var colorBits = (physicalBlock.BlockBits >> colorStartBit.Value) & colorBitMask;
        var colorBitStream = new BitStream(colorBits, 128);

        var colorDecoder = new BoundedIntegerSequenceDecoder(colorValuesRangeOpt.Value);
        int colorCountInBlock = colorValuesCount.Value;
        var colors = colorDecoder.Decode(colorCountInBlock, ref colorBitStream);

        var weight_dims = weightGridDimensions.Value;
        data.weightGridX = weight_dims.Item1;
        data.weightGridY = weight_dims.Item2;
        data.weightRange = weightRange.Value;

        data.partitionId = physicalBlock.GetPartitionId();
        data.dualPlaneChannel = physicalBlock.GetDualPlaneChannel();

        int colorIndex = 0;
        for (int i = 0; i < partitionCount.Value; ++i)
        {
            var endpoint = new IntermediateEndpointData();
            var endpointModeOpt = physicalBlock.GetEndpointMode(i);
            if (!endpointModeOpt.HasValue)
                return null;
            endpoint.mode = endpointModeOpt.Value;
            
            for (int j = 0; j < endpoint.mode.GetColorValuesCount(); ++j)
            {
                endpoint.colors.Add(colors[colorIndex++]);
            }
            data.endpoints.Add(endpoint);
        }

        data.endpointRange = colorValuesRangeOpt.Value;

        var weightBits = UInt128Extensions.ReverseBits(physicalBlock.BlockBits) & UInt128Extensions.OnesMask(weightBitCount.Value);
        colorBitStream = new BitStream(weightBits, 128);

        var weightDecoder = new BoundedIntegerSequenceDecoder(data.weightRange);
        int weightsCount = data.weightGridX * data.weightGridY;
        if (physicalBlock.IsDualPlane) weightsCount *= 2;
        data.weights = weightDecoder.Decode(weightsCount, ref colorBitStream);

        // store debug mapping from data signature to original pb for later pack-debugging
        var key = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Length}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:{data.endpointRange}";
        s_lastUnpacked[key] = physicalBlock.BlockBits;
        // also store a variant with endpoint_range set to null so Pack can round-trip when endpoint_range is cleared
        var keyWithNullEndpoint = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Length}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:null";
        s_lastUnpacked[keyWithNullEndpoint] = physicalBlock.BlockBits;

        return data;
    }

    public static int EndpointRangeForBlock(IntermediateBlockData data)
    {
        if (BoundedIntegerSequenceCodec.GetBitCountForRange(data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1), data.weightRange) > 96)
            return kEndpointRange_ReturnInvalidWeightDims;

        int partitionCount = data.endpoints.Count;
        int bitsWrittenCount = 11 + 2 + ((partitionCount > 1) ? 10 : 0) + ((partitionCount == 1) ? 4 : 6);
        int availableColorBitsCount = ExtraConfigBitPosition(data) - bitsWrittenCount;

        int colorValuesCount = 0;
        foreach (var ep in data.endpoints) colorValuesCount += ep.mode.GetColorValuesCount();

        int bitsNeededCount = (13 * colorValuesCount + 4) / 5;
        if (availableColorBitsCount < bitsNeededCount) return kEndpointRange_ReturnNotEnoughColorBits;

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
            ushort all_ones = (ushort)((1 << 13) - 1);
            for (int i = 0; i < 4; ++i) data.coords[i] = all_ones;
        }

        return data;
    }

    public static (string? error, UInt128 pb) Pack(IntermediateBlockData data)
    {
        UInt128 pb = 0;
        if (data.weights.Length != data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1))
        {
            return ("Incorrect number of weights!", 0);
        }

        var bitSink = new BitStream(0UL, 0);

        // First we need to encode the block mode.
        var errorMessage = PackBlockMode(data.weightGridX, data.weightGridY, data.weightRange, data.dualPlaneChannel.HasValue, bitSink);
        if (errorMessage != null) { return (errorMessage, 0); }

        // number of partitions minus one
        int partitionCount = data.endpoints.Count;
        bitSink.PutBits((uint)(partitionCount - 1), 2);

        if (partitionCount > 1)
        {
            int id = data.partitionId ?? 0;
            ArgumentOutOfRangeException.ThrowIfLessThan(id, 0);
            bitSink.PutBits((uint)id, 10);
        }

        var (weightSink, weightBitsCount) = EncodeWeights(data);

        var (error, extraConfig) = EncodeColorEndpointModes(data, partitionCount, bitSink);
        if (error != null) return (error, 0);

        int colorValueRange = data.endpointRange.HasValue ? data.endpointRange.Value : EndpointRangeForBlock(data);
        if (colorValueRange == kEndpointRange_ReturnInvalidWeightDims)
            throw new InvalidOperationException($"{nameof(colorValueRange)} must not be {nameof(kEndpointRange_ReturnInvalidWeightDims)}");
        if (colorValueRange == kEndpointRange_ReturnNotEnoughColorBits)
        {
            return ("Intermediate block emits illegal color range", 0);
        }

        var colorEncoder = new BoundedIntegerSequenceEncoder(colorValueRange);
        foreach (var endpoint in data.endpoints)
        {
            foreach (var color in endpoint.colors)
            {
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
        pb = combined;

        var block = PhysicalBlock.Create(pb);
        var illegal = block.IdentifyInvalidEncodingIssues();

        // debug: compare against last unpacked if present
        var key = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Length}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:{data.endpointRange}";
        if (s_lastUnpacked.TryGetValue(key, out var original))
        {
            if (!original.Equals(pb))
            {
                // TODO: What to do in this case?
                /* pack mismatch detected */
            }
        }

        return (illegal, pb);
    }

    public static (string? error, UInt128 pb) Pack(VoidExtentData data)
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

        UInt128 pb;
        // Decide representation: if the RGBA low word is zero we emit the
        // compact single-ulong representation (low word = header+coords,
        // high word = 0) to match the reference tests. Otherwise the
        // low word holds RGBA and the high word holds header+coords.
        if (high64 == 0UL)
        {
            pb = (UInt128)low64;
            // using compact void extent representation
        }
        else
        {
            pb = new UInt128(high64, low64);
            // using full void extent representation
        }

        var block = PhysicalBlock.Create(pb);
        var illegal = block.IdentifyInvalidEncodingIssues();
        if (illegal is not null)
        {
            throw new InvalidOperationException($"{nameof(Pack)}(void extent) produced illegal encoding");
        }
        return (illegal, pb);
    }
}
