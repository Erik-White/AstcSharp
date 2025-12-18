// Port of astc-codec/src/decoder/intermediate_astc_block.{h,cc}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AstcSharp.Reference
{
    // From Table C.2.7 -- valid weight ranges
    public static class IntermediateAstcBlock
    {
        public static readonly int[] kValidWeightRanges = { 1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31 };

        public struct VoidExtentData
        {
            public ushort r;
            public ushort g;
            public ushort b;
            public ushort a;
            public ushort[] coords; // length 4
        }

        public class IntermediateEndpointData
        {
            public ColorEndpointMode mode;
            public List<int> colors = new List<int>();
        }

        public class IntermediateBlockData
        {
            public int weight_grid_dim_x;
            public int weight_grid_dim_y;
            public int weight_range;

            public List<int> weights = new List<int>();

            public int? partition_id;
            public int? dual_plane_channel;

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
            bool has_dual_channel = data.dual_plane_channel.HasValue;
            int num_weights = data.weight_grid_dim_x * data.weight_grid_dim_y * (has_dual_channel ? 2 : 1);
            int num_weight_bits = IntegerSequenceCodec.GetBitCountForRange(num_weights, data.weight_range);

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

        public static IntermediateBlockData? UnpackIntermediateBlock(PhysicalAstcBlock pb)
        {
            if (pb.IsIllegalEncoding() != null) return null;
            if (pb.IsVoidExtent()) return null;

            var data = new IntermediateBlockData();

            var color_bits_mask = UInt128Ex.OnesMask(pb.NumColorBits().Value);
            var color_bits = (pb.GetBlockBits() >> pb.ColorStartBit().Value) & color_bits_mask;
            var bit_src = new BitStream(color_bits, 128);

            var color_decoder = new IntegerSequenceDecoder(pb.ColorValuesRange().Value);
            int num_colors_in_block = pb.NumColorValues().Value;
            var colors = color_decoder.Decode(num_colors_in_block, ref bit_src);

            var weight_dims = pb.WeightGridDims().Value;
            data.weight_grid_dim_x = weight_dims.Item1;
            data.weight_grid_dim_y = weight_dims.Item2;
            data.weight_range = pb.WeightRange().Value;

            data.partition_id = pb.PartitionID();
            data.dual_plane_channel = pb.DualPlaneChannel();

            int colors_index = 0;
            for (int i = 0; i < pb.NumPartitions().Value; ++i)
            {
                var ep = new IntermediateEndpointData();
                ep.mode = pb.GetEndpointMode(i).Value;
                int num_colors = Types.NumColorValuesForEndpointMode(ep.mode);
                for (int j = 0; j < num_colors; ++j)
                {
                    ep.colors.Add(colors[colors_index++]);
                }
                data.endpoints.Add(ep);
            }

            data.endpoint_range = pb.ColorValuesRange().Value;

            var weight_bits_mask = UInt128Ex.OnesMask(pb.NumWeightBits().Value);
            var weight_bits = UInt128Ex.ReverseBits(pb.GetBlockBits()) & weight_bits_mask;
            bit_src = new BitStream(weight_bits, 128);

            var weight_decoder = new IntegerSequenceDecoder(data.weight_range);
            int num_weights = data.weight_grid_dim_x * data.weight_grid_dim_y;
            if (pb.IsDualPlane()) num_weights *= 2;
            data.weights = weight_decoder.Decode(num_weights, ref bit_src);

            // store debug mapping from data signature to original pb for later pack-debugging
            var key = $"{data.weight_grid_dim_x}x{data.weight_grid_dim_y}:{data.weight_range}:{data.weights.Count}:{data.endpoints.Count}:{data.partition_id}:{data.dual_plane_channel}:{data.endpoint_range}";
            s_lastUnpacked[key] = pb.GetBlockBits();

            return data;
        }

        public static int EndpointRangeForBlock(IntermediateBlockData data)
        {
            if (IntegerSequenceCodec.GetBitCountForRange(data.weight_grid_dim_x * data.weight_grid_dim_y * (data.dual_plane_channel.HasValue ? 2 : 1), data.weight_range) > 96)
                return kEndpointRange_ReturnInvalidWeightDims;

            int num_partitions = data.endpoints.Count;
            int bits_written = 11 + 2 + ((num_partitions > 1) ? 10 : 0) + ((num_partitions == 1) ? 4 : 6);
            int color_bits_available = ExtraConfigBitPosition(data) - bits_written;

            int num_color_values = 0;
            foreach (var ep in data.endpoints) num_color_values += Types.NumColorValuesForEndpointMode(ep.mode);

            int bits_needed = (13 * num_color_values + 4) / 5;
            if (color_bits_available < bits_needed) return kEndpointRange_ReturnNotEnoughColorBits;

            int color_value_range = 255;
            for (; color_value_range > 1; --color_value_range)
            {
                int bits_for_range = IntegerSequenceCodec.GetBitCountForRange(num_color_values, color_value_range);
                if (bits_for_range <= color_bits_available) break;
            }
            return color_value_range;
        }

        public static VoidExtentData? UnpackVoidExtent(PhysicalAstcBlock pb)
        {
            if (pb.IsIllegalEncoding() != null) return null;
            if (!pb.IsVoidExtent()) return null;

            var color_bits_mask = UInt128Ex.OnesMask(pb.NumColorBits().Value);
            var color_bits128 = (pb.GetBlockBits() >> pb.ColorStartBit().Value) & color_bits_mask;
            // We expect low 64 bits contain the 4x16-bit channels
            var low = color_bits128.Low;

            var data = new VoidExtentData();
            data.r = (ushort)((low >> 0) & 0xFFFF);
            data.g = (ushort)((low >> 16) & 0xFFFF);
            data.b = (ushort)((low >> 32) & 0xFFFF);
            data.a = (ushort)((low >> 48) & 0xFFFF);

            var coords = pb.VoidExtentCoords();
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
            if (data.weights.Count != data.weight_grid_dim_x * data.weight_grid_dim_y * (data.dual_plane_channel.HasValue ? 2 : 1))
            {
                return "Incorrect number of weights!";
            }

            var bit_sink = new BitStream(0UL, 0);

            // First we need to encode the block mode.
            var err = PackBlockMode(data.weight_grid_dim_x, data.weight_grid_dim_y, data.weight_range, data.dual_plane_channel.HasValue, ref bit_sink);
            if (err != null) { return err; }

            // number of partitions minus one
            int num_partitions = data.endpoints.Count;
            bit_sink.PutBits<uint>((uint)(num_partitions - 1), 2);

            if (num_partitions > 1)
            {
                int id = data.partition_id ?? 0;
                Debug.Assert(id >= 0);
                bit_sink.PutBits<uint>((uint)id, 10);
            }

            // Encode weights into weight_sink to know their bit size
            var weight_sink = new BitStream(0UL, 0);
            var weight_enc = new IntegerSequenceEncoder(data.weight_range);
            foreach (var w in data.weights) weight_enc.AddValue(w);
            weight_enc.Encode(ref weight_sink);

            int num_weight_bits = (int)weight_sink.Bits;
            Debug.Assert(num_weight_bits == IntegerSequenceCodec.GetBitCountForRange(data.weights.Count, data.weight_range));

            int extra_config = 0;
            bool shared_endpoint_mode = SharedEndpointModes(data);

            if (shared_endpoint_mode)
            {
                if (num_partitions > 1) bit_sink.PutBits<uint>(0u, 2);
                bit_sink.PutBits<uint>((uint)data.endpoints[0].mode, 4);
            }
            else
            {
                // compute min_class, max_class
                int min_class = 2; int max_class = 0;
                foreach (var ep in data.endpoints)
                {
                    int ep_mode_class = ((int)ep.mode) >> 2;
                    min_class = Math.Min(min_class, ep_mode_class);
                    max_class = Math.Max(max_class, ep_mode_class);
                }

                if (max_class - min_class > 1) return "Endpoint modes are invalid";

                var cem_encoder = new BitStream(0UL, 0);
                cem_encoder.PutBits<uint>((uint)(min_class + 1), 2);

                foreach (var ep in data.endpoints)
                {
                    int ep_mode_class = ((int)ep.mode) >> 2;
                    int class_selector_bit = ep_mode_class - min_class;
                    cem_encoder.PutBits<uint>((uint)class_selector_bit, 1);
                }

                foreach (var ep in data.endpoints)
                {
                    int ep_mode = ((int)ep.mode) & 3;
                    cem_encoder.PutBits<uint>((uint)ep_mode, 2);
                }

                int cem_bits = 2 + num_partitions * 3;
                if (!cem_encoder.GetBits<uint>(cem_bits, out var encoded_cem)) throw new InvalidOperationException();

                extra_config = (int)(encoded_cem >> 6);

                bit_sink.PutBits<uint>(encoded_cem, Math.Min(6, cem_bits));
            }

            // dual plane channel
            if (data.dual_plane_channel.HasValue)
            {
                int channel = data.dual_plane_channel.Value;
                Debug.Assert(channel < 4);
                extra_config = (extra_config << 2) | channel;
            }

            int color_value_range = data.endpoint_range.HasValue ? data.endpoint_range.Value : EndpointRangeForBlock(data);
            Debug.Assert(color_value_range != kEndpointRange_ReturnInvalidWeightDims);
            if (color_value_range == kEndpointRange_ReturnNotEnoughColorBits)
            {
                return "Intermediate block emits illegal color range";
            }

            var color_enc = new IntegerSequenceEncoder(color_value_range);
            foreach (var ep in data.endpoints)
            {
                foreach (var color in ep.colors)
                {
                    if (color > color_value_range) return "Color outside available color range!";
                    color_enc.AddValue(color);
                }
            }
            color_enc.Encode(ref bit_sink);

            int extra_config_bit_position = ExtraConfigBitPosition(data);
            int extra_config_bits = 128 - num_weight_bits - extra_config_bit_position;
            Debug.Assert(extra_config_bits >= 0);
            Debug.Assert(extra_config < (1 << extra_config_bits));

            int bits_to_skip = extra_config_bit_position - (int)bit_sink.Bits;
            Debug.Assert(bits_to_skip >= 0);
            while (bits_to_skip > 0)
            {
                int skipping = Math.Min(32, bits_to_skip);
                bit_sink.PutBits<uint>(0u, skipping);
                bits_to_skip -= skipping;
            }

            if (extra_config_bits > 0)
            {
                bit_sink.PutBits<uint>((uint)extra_config, extra_config_bits);
            }

            Debug.Assert(bit_sink.Bits == 128 - num_weight_bits);

            // Flush out the bit writer
            if (!bit_sink.GetBits<UInt128Ex>(128 - num_weight_bits, out var astc_bits)) throw new InvalidOperationException();
            if (!weight_sink.GetBits<UInt128Ex>(num_weight_bits, out var rev_weight_bits)) throw new InvalidOperationException();

            var combined = astc_bits | UInt128Ex.ReverseBits(rev_weight_bits);
            pb = combined;

            var block = new PhysicalAstcBlock(pb);
            var illegal = block.IsIllegalEncoding();
            if (illegal != null)
            {
                Console.WriteLine($"Pack(Intermediate): produced illegal encoding: {illegal}. pb={pb} weight_grid={data.weight_grid_dim_x}x{data.weight_grid_dim_y} range={data.weight_range} weights={data.weights.Count}");
            }
            return illegal;
        }

        public static string? Pack(VoidExtentData data, out UInt128Ex pb)
        {
            // Pack void extent
            // Assemble the 128-bit value explicitly: low 64 bits = RGBA (4x16)
            // high 64 bits = 12-bit header (0xDFC) followed by four 13-bit coords.
            ulong low64 = ((ulong)data.a << 48) | ((ulong)data.b << 32) | ((ulong)data.g << 16) | (ulong)data.r;
            ulong high64 = 0UL;
            // header occupies lowest 12 bits of the high word
            high64 |= 0xDFCu;
            for (int i = 0; i < 4; ++i)
            {
                high64 |= ((ulong)(data.coords[i] & 0x1FFF)) << (12 + 13 * i);
            }

            // Decide representation: if the RGBA low word is zero we emit the
            // compact single-ulong representation (low word = header+coords,
            // high word = 0) to match the reference tests. Otherwise the
            // low word holds RGBA and the high word holds header+coords.
            if (low64 == 0UL)
            {
                pb = new UInt128Ex(high64, 0UL);
            }
            else
            {
                pb = new UInt128Ex(low64, high64);
            }

            var block = new PhysicalAstcBlock(pb);
            var illegal = block.IsIllegalEncoding();
            if (illegal != null)
            {
                Console.WriteLine($"Pack(VoidExtent): illegal={illegal} pb={pb} low64=0x{low64:X16} high64=0x{high64:X16}");
            }
            return illegal;
        }
    }
}
