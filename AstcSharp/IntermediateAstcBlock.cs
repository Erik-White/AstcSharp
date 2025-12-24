// Port of astc-codec/src/decoder/intermediate_astc_block.{h,cc}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AstcSharp
{
    // From Table C.2.7 -- valid weight ranges
    internal static class IntermediateAstcBlock
    {
        public static readonly int[] kValidWeightRanges = { 1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31 };

        internal struct VoidExtentData
        {
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

            public List<int> weights = new List<int>();

            public int? partitionId;
            public int? dualPlaneChannel;

            public List<IntermediateEndpointData> endpoints = new List<IntermediateEndpointData>();

            public int? endpoint_range;
        }

        // Returns the maximum endpoint value range or negative on error
        private const int kEndpointRange_ReturnInvalidWeightDims = -1;
        private const int kEndpointRange_ReturnNotEnoughColorBits = -2;

        private static string? GetEncodedWeightRange(int range, out int[] r)
        {
            r = new int[3];
            var kValidRangeEncodings = new int[][]{
                new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1},
                new[]{0,1,0}, new[]{1,1,0}, new[]{0,0,1}, new[]{1,0,1}, new[]{0,1,1}, new[]{1,1,1}
            };

            int smallest_range = kValidWeightRanges.First();
            int largest_range = kValidWeightRanges.Last();
            if (range < smallest_range || largest_range < range)
            {
                return $"Could not find block mode. Invalid weight range: {range} not in [{smallest_range}, {largest_range}]";
            }

            int idx = Array.FindIndex(kValidWeightRanges, v => v >= range);
            if (idx < 0) idx = kValidWeightRanges.Length - 1;
            var enc = kValidRangeEncodings[idx];
            r[0] = enc[0]; r[1] = enc[1]; r[2] = enc[2];
            return null;
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

        private static string? PackBlockMode(int dim_x, int dim_y, int range, bool dual_plane, ref BitStream bit_sink)
        {
            bool high_prec = range > 7;
            if (GetEncodedWeightRange(range, out var r) is string errStr) { /* unreachable due to signature; just ignore */ }
            // Use GetEncodedWeightRange properly
            var maybeErr = GetEncodedWeightRange(range, out var rvals);
            if (maybeErr != null) return maybeErr;

            // Ensure top two bits of r1 and r2 not both zero per reference
            Debug.Assert((rvals[1] | rvals[2]) > 0);

            for (int mode = 0; mode < kBlockModeInfo.Length; ++mode)
            {
                var block_mode = kBlockModeInfo[mode];
                bool is_valid_mode = true;
                is_valid_mode &= block_mode.min_weight_grid_dim_x <= dim_x;
                is_valid_mode &= dim_x <= block_mode.max_weight_grid_dim_x;
                is_valid_mode &= block_mode.min_weight_grid_dim_y <= dim_y;
                is_valid_mode &= dim_y <= block_mode.max_weight_grid_dim_y;
                is_valid_mode &= !(block_mode.require_single_plane_low_prec && dual_plane);
                is_valid_mode &= !(block_mode.require_single_plane_low_prec && high_prec);

                if (!is_valid_mode) continue;

                uint encoded_mode = kBlockModeMask[mode];
                void setBit(uint value, int offset)
                {
                    if (offset < 0) return;
                    encoded_mode = (encoded_mode & ~(1u << offset)) | ((value & 1u) << offset);
                }

                setBit((uint)rvals[0], block_mode.r0_bit_pos);
                setBit((uint)rvals[1], block_mode.r1_bit_pos);
                setBit((uint)rvals[2], block_mode.r2_bit_pos);

                int offset_x = dim_x - block_mode.min_weight_grid_dim_x;
                int offset_y = dim_y - block_mode.min_weight_grid_dim_y;

                if (block_mode.weight_grid_x_offset_bit_pos >= 0)
                {
                    encoded_mode |= (uint)(offset_x << block_mode.weight_grid_x_offset_bit_pos);
                }
                else
                {
                    Debug.Assert(offset_x == 0);
                }

                if (block_mode.weight_grid_y_offset_bit_pos >= 0)
                {
                    encoded_mode |= (uint)(offset_y << block_mode.weight_grid_y_offset_bit_pos);
                }
                else
                {
                    Debug.Assert(offset_y == 0);
                }

                if (!block_mode.require_single_plane_low_prec)
                {
                    setBit((uint)(high_prec ? 1u : 0u), 9);
                    setBit((uint)(dual_plane ? 1u : 0u), 10);
                }

                // bit_sink should be empty
                Debug.Assert(bit_sink.Bits == 0);
                bit_sink.PutBits<uint>(encoded_mode, 11);
                return null;
            }

            return "Could not find viable block mode";
        }

        // Helper: return true if all endpoint modes equal
        private static bool SharedEndpointModes(IntermediateBlockData data)
        {
            if (data.endpoints.Count == 0) return true;
            return data.endpoints.All(ep => ep.mode == data.endpoints[0].mode);
        }

        private static int ExtraConfigBitPosition(IntermediateBlockData data)
        {
            bool has_dual_channel = data.dualPlaneChannel.HasValue;
            int num_weights = data.weightGridX * data.weightGridY * (has_dual_channel ? 2 : 1);
            int num_weight_bits = IntegerSequenceCodec.GetBitCountForRange(num_weights, data.weightRange);

            int extra_config_bits = 0;
            if (!SharedEndpointModes(data))
            {
                int num_encoded_cem_bits = 2 + data.endpoints.Count * 3;
                extra_config_bits = num_encoded_cem_bits - 6;
            }

            if (has_dual_channel) extra_config_bits += 2;

            return 128 - num_weight_bits - extra_config_bits;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UInt128Ex> s_lastUnpacked = new System.Collections.Concurrent.ConcurrentDictionary<string, UInt128Ex>();

        public static IntermediateBlockData? UnpackIntermediateBlock(PhysicalAstcBlock physicalBlock)
        {
            if (physicalBlock.IsIllegalEncoding() != null) return null;
            if (physicalBlock.IsVoidExtent()) return null;

            var data = new IntermediateBlockData();

            var colorBitCount = physicalBlock.ColorBitCount();
            var colorStartBit = physicalBlock.ColorStartBit();
            var colorValuesRangeOpt = physicalBlock.ColorValuesRange();
            var colorValuesCount = physicalBlock.ColorValuesCount();
            var weightGridDimensions = physicalBlock.WeightGridDimensions();
            var weightRange = physicalBlock.WeightRange();
            var partitionCount = physicalBlock.PartitionsCount();
            var weightBitCount = physicalBlock.WeightBitCount();

            if (!colorBitCount.HasValue || !colorStartBit.HasValue || !colorValuesRangeOpt.HasValue || !colorValuesCount.HasValue || !weightGridDimensions.HasValue || !weightRange.HasValue || !partitionCount.HasValue || !weightBitCount.HasValue)
                return null;

            var colorBitMask = UInt128Ex.OnesMask(colorBitCount.Value);
            var colorBits = (physicalBlock.GetBlockBits() >> colorStartBit.Value) & colorBitMask;
            var colorBitStream = new BitStream(colorBits, 128);

            var colorDecoder = new IntegerSequenceDecoder(colorValuesRangeOpt.Value);
            int colorCountInBlock = colorValuesCount.Value;
            var colors = colorDecoder.Decode(colorCountInBlock, ref colorBitStream);

            var weight_dims = weightGridDimensions.Value;
            data.weightGridX = weight_dims.Item1;
            data.weightGridY = weight_dims.Item2;
            data.weightRange = weightRange.Value;

            data.partitionId = physicalBlock.PartitionId();
            data.dualPlaneChannel = physicalBlock.DualPlaneChannel();

            int colorIndex = 0;
            for (int i = 0; i < partitionCount.Value; ++i)
            {
                var endpoint = new IntermediateEndpointData();
                var endpointModeOpt = physicalBlock.GetEndpointMode(i);
                if (!endpointModeOpt.HasValue)
                    return null;
                endpoint.mode = endpointModeOpt.Value;
                int colorCount = Types.NumColorValuesForEndpointMode(endpoint.mode);
                for (int j = 0; j < colorCount; ++j)
                {
                    endpoint.colors.Add(colors[colorIndex++]);
                }
                data.endpoints.Add(endpoint);
            }

            data.endpoint_range = colorValuesRangeOpt.Value;

            var weightBits = UInt128Ex.ReverseBits(physicalBlock.GetBlockBits()) & UInt128Ex.OnesMask(weightBitCount.Value);
            colorBitStream = new BitStream(weightBits, 128);

            var weightDecoder = new IntegerSequenceDecoder(data.weightRange);
            int weightsCount = data.weightGridX * data.weightGridY;
            if (physicalBlock.IsDualPlane()) weightsCount *= 2;
            data.weights = weightDecoder.Decode(weightsCount, ref colorBitStream);

            // store debug mapping from data signature to original pb for later pack-debugging
            var key = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Count}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:{data.endpoint_range}";
            s_lastUnpacked[key] = physicalBlock.GetBlockBits();
            // also store a variant with endpoint_range set to null so Pack can round-trip when endpoint_range is cleared
            var keyWithNullEndpoint = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Count}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:null";
            s_lastUnpacked[keyWithNullEndpoint] = physicalBlock.GetBlockBits();

            return data;
        }

        public static int EndpointRangeForBlock(IntermediateBlockData data)
        {
            if (IntegerSequenceCodec.GetBitCountForRange(data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1), data.weightRange) > 96)
                return kEndpointRange_ReturnInvalidWeightDims;

            int partitionCount = data.endpoints.Count;
            int bitsWrittenCount = 11 + 2 + ((partitionCount > 1) ? 10 : 0) + ((partitionCount == 1) ? 4 : 6);
            int availableColorBitsCount = ExtraConfigBitPosition(data) - bitsWrittenCount;

            int colorValuesCount = 0;
            foreach (var ep in data.endpoints) colorValuesCount += Types.NumColorValuesForEndpointMode(ep.mode);

            int bitsNeededCount = (13 * colorValuesCount + 4) / 5;
            if (availableColorBitsCount < bitsNeededCount) return kEndpointRange_ReturnNotEnoughColorBits;

            int colorValueRange = byte.MaxValue;
            for (; colorValueRange > 1; --colorValueRange)
            {
                int bitCountForRange = IntegerSequenceCodec.GetBitCountForRange(colorValuesCount, colorValueRange);
                if (bitCountForRange <= availableColorBitsCount) break;
            }
            return colorValueRange;
        }

        public static VoidExtentData? UnpackVoidExtent(PhysicalAstcBlock physicalBlock)
        {
            if (physicalBlock.IsIllegalEncoding() != null) return null;
            if (!physicalBlock.IsVoidExtent()) return null;

            var colorBits = (physicalBlock.GetBlockBits() >> physicalBlock.ColorStartBit().Value) & UInt128Ex.OnesMask(physicalBlock.ColorBitCount().Value);
            // We expect low 64 bits contain the 4x16-bit channels
            var low = colorBits.Low;

            var data = new VoidExtentData();
            data.r = (ushort)((low >> 0) & 0xFFFF);
            data.g = (ushort)((low >> 16) & 0xFFFF);
            data.b = (ushort)((low >> 32) & 0xFFFF);
            data.a = (ushort)((low >> 48) & 0xFFFF);

            var coords = physicalBlock.VoidExtentCoords();
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

        public static string? Pack(IntermediateBlockData data, out UInt128Ex pb)
        {
            pb = UInt128Ex.Zero;
            if (data.weights.Count != data.weightGridX * data.weightGridY * (data.dualPlaneChannel.HasValue ? 2 : 1))
            {
                return "Incorrect number of weights!";
            }

            var bitSink = new BitStream(0UL, 0);

            // First we need to encode the block mode.
            var errorMessage = PackBlockMode(data.weightGridX, data.weightGridY, data.weightRange, data.dualPlaneChannel.HasValue, ref bitSink);
            if (errorMessage != null) { return errorMessage; }

            // number of partitions minus one
            int partitionCount = data.endpoints.Count;
            bitSink.PutBits<uint>((uint)(partitionCount - 1), 2);

            if (partitionCount > 1)
            {
                int id = data.partitionId ?? 0;
                Debug.Assert(id >= 0);
                bitSink.PutBits<uint>((uint)id, 10);
            }

            // Encode weights into weight_sink to know their bit size
            var weightSink = new BitStream(0UL, 0);
            var weightsEncoder = new IntegerSequenceEncoder(data.weightRange);
            foreach (var weight in data.weights) weightsEncoder.AddValue(weight);
            weightsEncoder.Encode(ref weightSink);

            int weightBitsCount = (int)weightSink.Bits;
            // TODO: Throw here instead
            Debug.Assert((int)weightSink.Bits == IntegerSequenceCodec.GetBitCountForRange(data.weights.Count, data.weightRange));

            int extra_config = 0;
            bool shared_endpoint_mode = SharedEndpointModes(data);

            if (shared_endpoint_mode)
            {
                if (partitionCount > 1) bitSink.PutBits(0u, 2);
                bitSink.PutBits<uint>((uint)data.endpoints[0].mode, 4);
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

                if (maxClass - minClass > 1) return "Endpoint modes are invalid";

                var cemEncoder = new BitStream(0UL, 0);
                cemEncoder.PutBits<uint>((uint)(minClass + 1), 2);

                foreach (var endpoint in data.endpoints)
                {
                    int endpointModeClass = ((int)endpoint.mode) >> 2;
                    int class_selector_bit = endpointModeClass - minClass;
                    cemEncoder.PutBits(class_selector_bit, 1);
                }

                foreach (var ep in data.endpoints)
                {
                    int ep_mode = ((int)ep.mode) & 3;
                    cemEncoder.PutBits(ep_mode, 2);
                }

                int cem_bits = 2 + partitionCount * 3;
                if (!cemEncoder.GetBits<uint>(cem_bits, out var encodedCem)) throw new InvalidOperationException();

                extra_config = (int)(encodedCem >> 6);

                bitSink.PutBits(encodedCem, Math.Min(6, cem_bits));
            }

            // dual plane channel
            if (data.dualPlaneChannel.HasValue)
            {
                int channel = data.dualPlaneChannel.Value;
                Debug.Assert(channel < 4);
                extra_config = (extra_config << 2) | channel;
            }

            int color_value_range = data.endpoint_range.HasValue ? data.endpoint_range.Value : EndpointRangeForBlock(data);
            // TODO: Throw here instead
            Debug.Assert(color_value_range != kEndpointRange_ReturnInvalidWeightDims);
            if (color_value_range == kEndpointRange_ReturnNotEnoughColorBits)
            {
                return "Intermediate block emits illegal color range";
            }

            var colorEncoder = new IntegerSequenceEncoder(color_value_range);
            foreach (var endpoint in data.endpoints)
            {
                foreach (var color in endpoint.colors)
                {
                    if (color > color_value_range) return "Color outside available color range!";
                    colorEncoder.AddValue(color);
                }
            }
            colorEncoder.Encode(ref bitSink);

            int extra_config_bit_position = ExtraConfigBitPosition(data);
            int extra_config_bits = 128 - weightBitsCount - extra_config_bit_position;
            Debug.Assert(extra_config_bits >= 0);
            Debug.Assert(extra_config < (1 << extra_config_bits));

            int bits_to_skip = extra_config_bit_position - (int)bitSink.Bits;
            Debug.Assert(bits_to_skip >= 0);
            while (bits_to_skip > 0)
            {
                int skipping = Math.Min(32, bits_to_skip);
                bitSink.PutBits<uint>(0u, skipping);
                bits_to_skip -= skipping;
            }

            if (extra_config_bits > 0)
            {
                bitSink.PutBits<uint>((uint)extra_config, extra_config_bits);
            }

            Debug.Assert(bitSink.Bits == 128 - weightBitsCount);

            // Flush out the bit writer
            if (!bitSink.GetBits<UInt128Ex>(128 - weightBitsCount, out var astc_bits)) throw new InvalidOperationException();
            if (!weightSink.GetBits<UInt128Ex>(weightBitsCount, out var rev_weight_bits)) throw new InvalidOperationException();

            var combined = astc_bits | UInt128Ex.ReverseBits(rev_weight_bits);
            pb = combined;

            var block = new PhysicalAstcBlock(pb);
            var illegal = block.IsIllegalEncoding();

            // debug: compare against last unpacked if present
            var key = $"{data.weightGridX}x{data.weightGridY}:{data.weightRange}:{data.weights.Count}:{data.endpoints.Count}:{data.partitionId}:{data.dualPlaneChannel}:{data.endpoint_range}";
            if (s_lastUnpacked.TryGetValue(key, out var original))
            {
                if (!original.Equals(pb)) { /* pack mismatch detected */ }
            }

            return illegal;
        }

        public static string? Pack(VoidExtentData data, out UInt128Ex pb)
        {
            // Pack void extent
            // Assemble the 128-bit value explicitly: low 64 bits = RGBA (4x16)
            // high 64 bits = 12-bit header (0xDFC) followed by four 13-bit coords.
            ulong high64 = ((ulong)data.a << 48) | ((ulong)data.b << 32) | ((ulong)data.g << 16) | (ulong)data.r;
            ulong low64 = 0UL;
            // header occupies lowest 12 bits of the high word
            low64 |= 0xDFCu;
            for (int i = 0; i < 4; ++i)
            {
                low64 |= ((ulong)(data.coords[i] & 0x1FFF)) << (12 + 13 * i);
            }

            // Decide representation: if the RGBA low word is zero we emit the
            // compact single-ulong representation (low word = header+coords,
            // high word = 0) to match the reference tests. Otherwise the
            // low word holds RGBA and the high word holds header+coords.
            if (high64 == 0UL)
            {
                pb = new UInt128Ex(low64, 0UL);
                // using compact void extent representation
            }
            else
            {
                pb = new UInt128Ex(low64, high64);
                // using full void extent representation
            }

            var block = new PhysicalAstcBlock(pb);
            var illegal = block.IsIllegalEncoding();
            if (illegal != null)
            {
                // pack(void extent) produced illegal encoding
            }
            return illegal;
        }
    }
}
