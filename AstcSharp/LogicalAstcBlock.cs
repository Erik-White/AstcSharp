// Port of astc-codec/src/decoder/logical_astc_block.{h,cc}
using System;
using System.Collections.Generic;

namespace AstcSharp
{
    internal class LogicalAstcBlock
    {
        private List<(RgbaColor first, RgbaColor second)> endpoints_;
        private List<int> weights_;
        private Partition partition_;

        private class DualPlaneData
        {
            public int channel;
            public List<int> weights = new List<int>();
        }

        private DualPlaneData? dual_plane_;

        public LogicalAstcBlock(Footprint footprint)
        {
            endpoints_ = new List<(RgbaColor, RgbaColor)>() { (new RgbaColor(0,0,0,0), new RgbaColor(0,0,0,0)) };
            weights_ = new List<int>(new int[footprint.NumPixels()]);
            partition_ = new Partition(footprint, 1, 0);
            partition_.assignment = new List<int>(footprint.NumPixels());
            for (int i = 0; i < footprint.NumPixels(); ++i) partition_.assignment.Add(0);
        }

        public LogicalAstcBlock(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
        {
            endpoints_ = DecodeEndpoints(block);
            partition_ = ComputePartition(footprint, block);
            CalculateWeights(footprint, block);
        }

        public LogicalAstcBlock(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
        {
            endpoints_ = DecodeEndpoints(block);
            partition_ = ComputePartition(footprint, block);
            CalculateWeights(footprint, block);
        }

        private static List<(RgbaColor, RgbaColor)> DecodeEndpoints(IntermediateAstcBlock.IntermediateBlockData block)
        {
            int endpoint_range = block.endpoint_range.HasValue ? block.endpoint_range.Value : IntermediateAstcBlock.EndpointRangeForBlock(block);
            if (endpoint_range <= 0) throw new InvalidOperationException("Invalid endpoint range");
            var eps = new List<(RgbaColor, RgbaColor)>();
            foreach (var ed in block.endpoints)
            {
                EndpointCodec.DecodeColorsForMode(ed.colors, endpoint_range, ed.mode, out var d0, out var d1);
                eps.Add((d0, d1));
            }
            return eps;
        }

        private static List<(RgbaColor, RgbaColor)> DecodeEndpoints(IntermediateAstcBlock.VoidExtentData block)
        {
            var pair = new RgbaColor((block.r * 255) / 65535, (block.g * 255) / 65535, (block.b * 255) / 65535, (block.a * 255) / 65535);
            return new List<(RgbaColor, RgbaColor)>() { (pair, pair) };
        }

        private static Partition GenerateSinglePartition(Footprint footprint)
        {
            var p = new Partition(footprint, 1, 0);
            p.assignment = new List<int>(footprint.NumPixels());
            for (int i = 0; i < footprint.NumPixels(); ++i) p.assignment.Add(0);
            return p;
        }

        private static Partition ComputePartition(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
        {
            if (block.partition_id.HasValue)
            {
                int part_id = block.partition_id.Value;
                int num_parts = block.endpoints.Count;
                return Partition.GetASTCPartition(footprint, num_parts, part_id);
            }
            return GenerateSinglePartition(footprint);
        }

        private static Partition ComputePartition(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
            => GenerateSinglePartition(footprint);

        private void CalculateWeights(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
        {
            int grid_x = block.weight_grid_dim_x;
            int grid_y = block.weight_grid_dim_y;
            int grid_size = grid_x * grid_y;
            int weight_frequency = block.dual_plane_channel.HasValue ? 2 : 1;
            int weight_range = block.weight_range;

            var unquantized = new List<int>(grid_size);
            for (int i = 0; i < grid_size; ++i)
            {
                int w = block.weights[i * weight_frequency];
                unquantized.Add(Quantization.UnquantizeWeightFromRange(w, weight_range));
            }
            weights_ = WeightInfill.InfillWeights(unquantized, footprint, grid_x, grid_y);

            if (block.dual_plane_channel.HasValue)
            {
                SetDualPlaneChannel(block.dual_plane_channel.Value);
                for (int i = 0; i < grid_size; ++i)
                {
                    int w = block.weights[i * weight_frequency + 1];
                    unquantized[i] = Quantization.UnquantizeWeightFromRange(w, weight_range);
                }
                dual_plane_.weights = WeightInfill.InfillWeights(unquantized, footprint, grid_x, grid_y);
            }
        }

        private void CalculateWeights(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
        {
            weights_ = new List<int>(new int[footprint.NumPixels()]);
        }

        public Footprint GetFootprint() => partition_.footprint;

        public void SetWeightAt(int x, int y, int weight)
        {
            if (weight < 0 || weight > 64) throw new ArgumentOutOfRangeException(nameof(weight));
            weights_[y * GetFootprint().Width() + x] = weight;
        }

        public int WeightAt(int x, int y) => weights_[y * GetFootprint().Width() + x];

        public void SetDualPlaneWeightAt(int channel, int x, int y, int weight)
        {
            if (weight < 0 || weight > 64) throw new ArgumentOutOfRangeException(nameof(weight));
            if (!IsDualPlane()) throw new InvalidOperationException("Not a dual plane block");
            if (dual_plane_.channel == channel)
                dual_plane_.weights[y * GetFootprint().Width() + x] = weight;
            else
                SetWeightAt(x, y, weight);
        }

        public int DualPlaneWeightAt(int channel, int x, int y)
        {
            if (!IsDualPlane()) return WeightAt(x, y);
            if (dual_plane_.channel == channel) return dual_plane_.weights[y * GetFootprint().Width() + x];
            return WeightAt(x, y);
        }

        public RgbaColor ColorAt(int x, int y)
        {
            var fp = GetFootprint();
            if (x < 0 || x >= fp.Width() || y < 0 || y >= fp.Height()) throw new ArgumentOutOfRangeException();
            int idx = y * fp.Width() + x;
            int part = partition_.assignment[idx];
            var endpoints = endpoints_[part];

            var result = new RgbaColor(0,0,0,0);
            for (int channel = 0; channel < 4; ++channel)
            {
                int weight = (dual_plane_ != null && dual_plane_.channel == channel) ? dual_plane_.weights[idx] : weights_[idx];
                int p0 = channel switch { 0 => endpoints.first.R, 1 => endpoints.first.G, 2 => endpoints.first.B, _ => endpoints.first.A };
                int p1 = channel switch { 0 => endpoints.second.R, 1 => endpoints.second.G, 2 => endpoints.second.B, _ => endpoints.second.A };
                if (p0 < 0 || p0 >= 256 || p1 < 0 || p1 >= 256) throw new InvalidOperationException();
                int c0 = (p0 << 8) | p0;
                int c1 = (p1 << 8) | p1;
                int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
                int quantized = ((c * 255) + 32767) / 65536;
                switch (channel)
                {
                    case 0: result.R = quantized; break;
                    case 1: result.G = quantized; break;
                    case 2: result.B = quantized; break;
                    case 3: result.A = quantized; break;
                }
            }
            return result;
        }

        public void SetPartition(Partition p)
        {
            if (!p.footprint.Equals(partition_.footprint)) throw new InvalidOperationException("New partitions may not be for a different footprint");
            partition_ = p;
            while (endpoints_.Count < p.num_parts) endpoints_.Add((new RgbaColor(0,0,0,0), new RgbaColor(0,0,0,0)));
            if (endpoints_.Count > p.num_parts) endpoints_.RemoveRange(p.num_parts, endpoints_.Count - p.num_parts);
        }

        public void SetEndpoints((RgbaColor first, RgbaColor second) eps, int subset)
        {
            if (subset < 0 || subset >= partition_.num_parts) throw new ArgumentOutOfRangeException(nameof(subset));
            endpoints_[subset] = eps;
        }

        public void SetEndpoints(RgbaColor ep1, RgbaColor ep2, int subset) => SetEndpoints((ep1, ep2), subset);

        public void SetDualPlaneChannel(int channel)
        {
            if (channel < 0) { dual_plane_ = null; }
            else if (dual_plane_ != null) { dual_plane_.channel = channel; }
            else { dual_plane_ = new DualPlaneData { channel = channel, weights = new List<int>(weights_) }; }
        }

        public bool IsDualPlane() => dual_plane_ != null;

        public static LogicalAstcBlock? UnpackLogicalBlock(Footprint footprint, PhysicalAstcBlock pb)
        {
            if (pb.IsVoidExtent())
            {
                var ve = IntermediateAstcBlock.UnpackVoidExtent(pb);
                if (ve == null) return null;
                return new LogicalAstcBlock(footprint, ve.Value);
            }
            else
            {
                var ib = IntermediateAstcBlock.UnpackIntermediateBlock(pb);
                if (ib == null) return null;
                return new LogicalAstcBlock(footprint, ib);
            }
        }
    }
}
