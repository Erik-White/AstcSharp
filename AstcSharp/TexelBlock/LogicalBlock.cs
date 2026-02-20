using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock
{
    internal class LogicalBlock
    {
        // TODO: Consolidate this to RgbaColor class
        private const int ChannelCount = 4; // R, G, B, A

        private List<IColorEndpointPair> _endpoints;
        private List<int> _weights;
        private Partition _partition;
        private DualPlaneData? _dualPlane;

        private class DualPlaneData
        {
            public int Channel;
            public List<int> Weights = [];
        }

        public LogicalBlock(Footprint footprint)
        {
            _endpoints = [new LdrEndpointPair(RgbaColor.Empty, RgbaColor.Empty)];
            _weights = [.. new int[footprint.PixelCount]];
            // TODO: Add pixel count to Partition constructor
            _partition = new Partition(footprint, 1, 0)
            {
                assignment = new List<int>(footprint.PixelCount)
            };
            for (int i = 0; i < footprint.PixelCount; ++i) _partition.assignment.Add(0);
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = [.. new int[footprint.PixelCount]];
            CalculateWeights(footprint, block);
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.VoidExtentData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = [.. new int[footprint.PixelCount]];
            CalculateWeights(footprint, block);
        }

        private static List<IColorEndpointPair> DecodeEndpoints(IntermediateBlock.IntermediateBlockData block)
        {
            int endpointRange = block.endpointRange.HasValue ? block.endpointRange.Value : IntermediateBlock.EndpointRangeForBlock(block);
            if (endpointRange <= 0) throw new InvalidOperationException("Invalid endpoint range");
            var eps = new List<IColorEndpointPair>();
            foreach (var ed in block.endpoints)
            {
                eps.Add(EndpointCodec.DecodeColorsForModePolymorphic(ed.colors, endpointRange, ed.mode));
            }
            return eps;
        }

        private static List<IColorEndpointPair> DecodeEndpoints(IntermediateBlock.VoidExtentData block)
        {
            // VoidExtent blocks store HDR values (ushort) - preserve precision for HDR output
            var hdrColor = new RgbaHdrColor(block.r, block.g, block.b, block.a);

            return [new HdrEndpointPair(hdrColor, hdrColor)];
        }

        private static Partition GenerateSinglePartition(Footprint footprint)
        {
            var p = new Partition(footprint, 1, 0);
            p.assignment = new List<int>(footprint.PixelCount);
            for (int i = 0; i < footprint.PixelCount; ++i) p.assignment.Add(0);
            return p;
        }

        private static Partition ComputePartition(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
            => block.partitionId.HasValue
                ? Partition.GetASTCPartition(footprint, block.endpoints.Count, block.partitionId.Value)
                : GenerateSinglePartition(footprint);

        private static Partition ComputePartition(Footprint footprint, IntermediateBlock.VoidExtentData block)
            => GenerateSinglePartition(footprint);

        private void CalculateWeights(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
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

        private void CalculateWeights(Footprint footprint, IntermediateBlock.VoidExtentData block)
        {
            _weights = [.. new int[footprint.PixelCount]];
        }

        public Footprint GetFootprint() => _partition.footprint;

        public void SetWeightAt(int x, int y, int weight)
        {
            if (weight < 0 || weight > 64)
                throw new ArgumentOutOfRangeException(nameof(weight));

            _weights[y * GetFootprint().Width + x] = weight;
        }

        public int WeightAt(int x, int y) => _weights[y * GetFootprint().Width + x];

        public void SetDualPlaneWeightAt(int channel, int x, int y, int weight)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(channel);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 64);

            if (!IsDualPlane())
                throw new InvalidOperationException("Not a dual plane block");
            
            if (_dualPlane is not null && _dualPlane.Channel == channel)
                _dualPlane.Weights[y * GetFootprint().Width + x] = weight;
            else
                SetWeightAt(x, y, weight);
        }

        public int DualPlaneWeightAt(int channel, int x, int y)
        {
            if (!IsDualPlane())
                return WeightAt(x, y);

            return _dualPlane is not null && _dualPlane.Channel == channel
                ? _dualPlane.Weights[y * GetFootprint().Width + x]
                : WeightAt(x, y);
        }

        public RgbaColor ColorAt(int x, int y)
        {
            var footprint = GetFootprint();

            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, footprint.Width);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, footprint.Height);

            int index = y * footprint.Width + x;
            int part = _partition.assignment[index];
            var endpointPair = _endpoints[part];

            // For LDR output, handle both LDR and HDR endpoints
            if (endpointPair is LdrEndpointPair ldrPair)
            {
                var result = new int[ChannelCount];
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = (_dualPlane != null && _dualPlane.Channel == channel) ? _dualPlane.Weights[index] : _weights[index];
                    result[channel] = InterpolateChannel(ldrPair.Low, ldrPair.High, channel, weight);
                }

                return new RgbaColor(
                    r: result[0],
                    g: result[1],
                    b: result[2],
                    a: result[3]);
            }
            else if (endpointPair is HdrEndpointPair hdrPair)
            {
                // HDR endpoints: downscale to LDR for legacy ColorAt() method
                var result = new int[ChannelCount];
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = (_dualPlane != null && _dualPlane.Channel == channel) ? _dualPlane.Weights[index] : _weights[index];
                    ushort hdrValue = InterpolateChannelHdr(hdrPair.Low, hdrPair.High, channel, weight);
                    result[channel] = hdrValue >> 8; // Convert 0-65535 to 0-255
                }

                return new RgbaColor(
                    r: result[0],
                    g: result[1],
                    b: result[2],
                    a: result[3]);
            }
            else
            {
                throw new InvalidOperationException("Unknown endpoint pair type");
            }
        }

        private static int InterpolateChannel(RgbaColor first, RgbaColor second, int channel, int weight)
        {
            int p0 = channel switch { 0 => first.R, 1 => first.G, 2 => first.B, _ => first.A };
            int p1 = channel switch { 0 => second.R, 1 => second.G, 2 => second.B, _ => second.A };

            ArgumentOutOfRangeException.ThrowIfLessThan(p0, byte.MinValue);
            ArgumentOutOfRangeException.ThrowIfLessThan(p1, byte.MinValue);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(p0, byte.MaxValue);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(p1, byte.MaxValue);

            int c0 = (p0 << 8) | p0;
            int c1 = (p1 << 8) | p1;
            int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
            int quantized = ((c * byte.MaxValue) + short.MaxValue) / (ushort.MaxValue + 1);
            return Math.Clamp(quantized, 0, byte.MaxValue);
        }

        /// <summary>
        /// Interpolates an HDR channel value between two endpoints using the specified weight.
        /// </summary>
        /// <remarks>
        /// Uses the same interpolation algorithm as LDR but operates on ushort values (0-65535).
        /// </remarks>
        private static ushort InterpolateChannelHdr(RgbaHdrColor first, RgbaHdrColor second, int channel, int weight)
        {
            ushort p0 = first[channel];
            ushort p1 = second[channel];

            // Same algorithm as LDR but with ushort (0-65535) values
            int c0 = (p0 << 8) | (p0 >> 8);
            int c1 = (p1 << 8) | (p1 >> 8);
            int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
            return (ushort)Math.Clamp(c >> 8, 0, 0xFFFF);
        }

        /// <summary>
        /// Returns the HDR color at the specified pixel position.
        /// </summary>
        /// <remarks>
        /// For HDR endpoints, returns full 16-bit precision (0-65535) per channel.
        /// For LDR endpoints, upscales to HDR range (multiplies by 257).
        /// </remarks>
        public RgbaHdrColor ColorAtHdr(int x, int y)
        {
            var footprint = GetFootprint();

            ArgumentOutOfRangeException.ThrowIfNegative(x);
            ArgumentOutOfRangeException.ThrowIfNegative(y);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, footprint.Width);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, footprint.Height);

            int index = y * footprint.Width + x;
            int part = _partition.assignment[index];
            var endpointPair = _endpoints[part];

            if (endpointPair is HdrEndpointPair hdrPair)
            {
                // Interpolate HDR endpoints
                var result = new ushort[ChannelCount];
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = GetWeightForPixel(index, channel);
                    result[channel] = InterpolateChannelHdr(hdrPair.Low, hdrPair.High, channel, weight);
                }
                return new RgbaHdrColor(result[0], result[1], result[2], result[3]);
            }
            else if (endpointPair is LdrEndpointPair ldrPair)
            {
                // LDR block: interpolate at LDR precision then upscale to HDR range
                var result = new int[ChannelCount];
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = GetWeightForPixel(index, channel);
                    result[channel] = InterpolateChannel(ldrPair.Low, ldrPair.High, channel, weight);
                }

                // Convert LDR (0-255) to HDR (0-65535) using multiply by 257
                return new RgbaHdrColor(
                    (ushort)(result[0] * 257),
                    (ushort)(result[1] * 257),
                    (ushort)(result[2] * 257),
                    (ushort)(result[3] * 257));
            }
            else
            {
                throw new InvalidOperationException("Unknown endpoint pair type");
            }
        }

        /// <summary>
        /// Helper method to get the weight for a specific pixel and channel.
        /// </summary>
        private int GetWeightForPixel(int index, int channel)
        {
            return (_dualPlane != null && _dualPlane.Channel == channel)
                ? _dualPlane.Weights[index]
                : _weights[index];
        }

        public void SetPartition(Partition p)
        {
            if (!p.footprint.Equals(_partition.footprint))
                throw new InvalidOperationException("New partitions may not be for a different footprint");
            _partition = p;
            while (_endpoints.Count < p.numParts) _endpoints.Add(new LdrEndpointPair(RgbaColor.Empty, RgbaColor.Empty));
            if (_endpoints.Count > p.numParts) _endpoints.RemoveRange(p.numParts, _endpoints.Count - p.numParts);
        }

        public void SetEndpoints((RgbaColor first, RgbaColor second) eps, int subset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(subset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(subset, _partition.numParts);

            _endpoints[subset] = new LdrEndpointPair(eps.first, eps.second);
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

        public static LogicalBlock? UnpackLogicalBlock(Footprint footprint, PhysicalBlock physicalBlock)
        {
            if (physicalBlock.IsVoidExtent)
            {
                var voidExtantIntermediateBlock = IntermediateBlock.UnpackVoidExtent(physicalBlock);
                
                return voidExtantIntermediateBlock is null
                    ? null
                    : new LogicalBlock(footprint, voidExtantIntermediateBlock.Value);
            }
            else
            {
                var intermediateBlock = IntermediateBlock.UnpackIntermediateBlock(physicalBlock);
                
                return intermediateBlock is null
                    ? null
                    : new LogicalBlock(footprint, intermediateBlock);
            }
        }
    }
}
