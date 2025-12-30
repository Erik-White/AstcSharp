// Port of astc-codec/src/decoder/logical_astc_block.{h,cc}
using System;
using System.Collections.Generic;

namespace AstcSharp
{
    internal class LogicalAstcBlock
    {
        // TODO: Consolidate this to RgbaColor class
        private const int ChannelCount = 4; // R, G, B, A

        private List<(RgbaColor first, RgbaColor second)> _endpoints;
        private List<int> _weights;
        private Partition _partition;
        private DualPlaneData? _dualPlane;

        private class DualPlaneData
        {
            public int Channel;
            public List<int> Weights = [];
        }

        public LogicalAstcBlock(Footprint footprint)
        {
            _endpoints = [(new RgbaColor(0,0,0,0), new RgbaColor(0,0,0,0))];
            _weights = [.. new int[footprint.NumPixels()]];
            // TODO: Add pixel count to Partition constructor
            _partition = new Partition(footprint, 1, 0)
            {
                assignment = new List<int>(footprint.NumPixels())
            };
            for (int i = 0; i < footprint.NumPixels(); ++i) _partition.assignment.Add(0);
        }

        public LogicalAstcBlock(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = [.. new int[footprint.NumPixels()]];
            CalculateWeights(footprint, block);
        }

        public LogicalAstcBlock(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = [.. new int[footprint.NumPixels()]];
            CalculateWeights(footprint, block);
        }

        private static List<(RgbaColor, RgbaColor)> DecodeEndpoints(IntermediateAstcBlock.IntermediateBlockData block)
        {
            int endpoint_range = block.endpoint_range.HasValue ? block.endpoint_range.Value : IntermediateAstcBlock.EndpointRangeForBlock(block);
            if (endpoint_range <= 0) throw new InvalidOperationException("Invalid endpoint range");
            var eps = new List<(RgbaColor, RgbaColor)>();
            foreach (var ed in block.endpoints)
            {
                var (d0, d1) = EndpointCodec.DecodeColorsForMode(ed.colors, endpoint_range, ed.mode);
                eps.Add((d0, d1));
            }
            return eps;
        }

        private static List<(RgbaColor, RgbaColor)> DecodeEndpoints(IntermediateAstcBlock.VoidExtentData block)
        {
            var pair = new RgbaColor(block.r * byte.MaxValue / ushort.MaxValue, block.g * byte.MaxValue / ushort.MaxValue, block.b * byte.MaxValue / ushort.MaxValue, block.a * byte.MaxValue / ushort.MaxValue);
            
            return [(pair, pair)];
        }

        private static Partition GenerateSinglePartition(Footprint footprint)
        {
            var p = new Partition(footprint, 1, 0);
            p.assignment = new List<int>(footprint.NumPixels());
            for (int i = 0; i < footprint.NumPixels(); ++i) p.assignment.Add(0);
            return p;
        }

        private static Partition ComputePartition(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
            => block.partitionId.HasValue
                ? Partition.GetASTCPartition(footprint, block.endpoints.Count, block.partitionId.Value)
                : GenerateSinglePartition(footprint);

        private static Partition ComputePartition(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
            => GenerateSinglePartition(footprint);

        private void CalculateWeights(Footprint footprint, IntermediateAstcBlock.IntermediateBlockData block)
        {
            int gridSize = block.weightGridX * block.weightGridY;
            int weightFrequency = block.dualPlaneChannel.HasValue ? 2 : 1;

            var unquantized = new List<int>(gridSize);
            for (int i = 0; i < gridSize; ++i)
            {
                int weight = block.weights[i * weightFrequency];
                unquantized.Add(Quantization.UnquantizeWeightFromRange(weight, block.weightRange));
            }
            _weights = WeightInfill.InfillWeights(unquantized, footprint, block.weightGridX, block.weightGridY);

            if (block.dualPlaneChannel.HasValue)
            {
                SetDualPlaneChannel(block.dualPlaneChannel.Value);
                for (int i = 0; i < gridSize; ++i)
                {
                    int weight = block.weights[i * weightFrequency + 1];
                    unquantized[i] = Quantization.UnquantizeWeightFromRange(weight, block.weightRange);
                }
                if (_dualPlane is not null)
                    _dualPlane.Weights = WeightInfill.InfillWeights(unquantized, footprint, block.weightGridX, block.weightGridY);
            }
        }

        private void CalculateWeights(Footprint footprint, IntermediateAstcBlock.VoidExtentData block)
        {
            _weights = [.. new int[footprint.NumPixels()]];
        }

        public Footprint GetFootprint() => _partition.footprint;

        public void SetWeightAt(int x, int y, int weight)
        {
            if (weight < 0 || weight > 64)
                throw new ArgumentOutOfRangeException(nameof(weight));

            _weights[y * GetFootprint().Width() + x] = weight;
        }

        public int WeightAt(int x, int y) => _weights[y * GetFootprint().Width() + x];

        public void SetDualPlaneWeightAt(int channel, int x, int y, int weight)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(channel);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 64);

            if (!IsDualPlane())
                throw new InvalidOperationException("Not a dual plane block");
            
            if (_dualPlane is not null && _dualPlane.Channel == channel)
                _dualPlane.Weights[y * GetFootprint().Width() + x] = weight;
            else
                SetWeightAt(x, y, weight);
        }

        public int DualPlaneWeightAt(int channel, int x, int y)
        {
            if (!IsDualPlane())
                return WeightAt(x, y);

            return _dualPlane is not null && _dualPlane.Channel == channel
                ? _dualPlane.Weights[y * GetFootprint().Width() + x]
                : WeightAt(x, y);
        }

        public RgbaColor ColorAt(int x, int y)
        {
            var footprint = GetFootprint();

            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, footprint.Width());
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, footprint.Height());

            int index = y * footprint.Width() + x;
            int part = _partition.assignment[index];
            var (firstColor, secondColor) = _endpoints[part];

            var result = new int[RgbaColor.BytesPerPixel];
            for (int channel = 0; channel < ChannelCount; ++channel)
            {
                int weight = (_dualPlane != null && _dualPlane.Channel == channel) ? _dualPlane.Weights[index] : _weights[index];
                int p0 = channel switch { 0 => firstColor.R, 1 => firstColor.G, 2 => firstColor.B, _ => firstColor.A };
                int p1 = channel switch { 0 => secondColor.R, 1 => secondColor.G, 2 => secondColor.B, _ => secondColor.A };

                ArgumentOutOfRangeException.ThrowIfLessThan(p0, byte.MinValue);
                ArgumentOutOfRangeException.ThrowIfLessThan(p1, byte.MinValue);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(p0, byte.MaxValue);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(p1, byte.MaxValue);

                int c0 = (p0 << 8) | p0;
                int c1 = (p1 << 8) | p1;
                int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
                int quantized = ((c * byte.MaxValue) + short.MaxValue) / (ushort.MaxValue + 1);
                quantized = Math.Clamp(quantized, 0, byte.MaxValue);
                switch (channel)
                {
                    case 0: result[0] = quantized; break;
                    case 1: result[1] = quantized; break;
                    case 2: result[2] = quantized; break;
                    case 3: result[3] = quantized; break;
                }
            }
            return new RgbaColor(
                r: result[0],
                g: result[1],
                b: result[2],
                a: result[3]);
        }

        public void SetPartition(Partition p)
        {
            if (!p.footprint.Equals(_partition.footprint))
                throw new InvalidOperationException("New partitions may not be for a different footprint");
            _partition = p;
            while (_endpoints.Count < p.num_parts) _endpoints.Add((new RgbaColor(0,0,0,0), new RgbaColor(0,0,0,0)));
            if (_endpoints.Count > p.num_parts) _endpoints.RemoveRange(p.num_parts, _endpoints.Count - p.num_parts);
        }

        public void SetEndpoints((RgbaColor first, RgbaColor second) eps, int subset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(subset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(subset, _partition.num_parts);
            
            _endpoints[subset] = eps;
        }

        public void SetEndpoints(RgbaColor ep1, RgbaColor ep2, int subset)
            => SetEndpoints((ep1, ep2), subset);

        public void SetDualPlaneChannel(int channel)
        {
            if (channel < 0) { _dualPlane = null; }
            else if (_dualPlane != null) { _dualPlane.Channel = channel; }
            else { _dualPlane = new DualPlaneData { Channel = channel, Weights = [.. _weights] }; }
        }

        public bool IsDualPlane() => _dualPlane is not null;

        public static LogicalAstcBlock? UnpackLogicalBlock(Footprint footprint, PhysicalAstcBlock physicalBlock)
        {
            if (physicalBlock.IsVoidExtent())
            {
                var voidExtantIntermediateBlock = IntermediateAstcBlock.UnpackVoidExtent(physicalBlock);
                
                return voidExtantIntermediateBlock is null
                    ? null
                    : new LogicalAstcBlock(footprint, voidExtantIntermediateBlock.Value);
            }
            else
            {
                var intermediateBlock = IntermediateAstcBlock.UnpackIntermediateBlock(physicalBlock);
                
                return intermediateBlock is null
                    ? null
                    : new LogicalAstcBlock(footprint, intermediateBlock);
            }
        }
    }
}
