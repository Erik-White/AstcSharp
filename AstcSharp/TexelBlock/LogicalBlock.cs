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
            if (block.isHdr)
            {
                // HDR void extent: ushort values are FP16 bit patterns (not LNS)
                var hdrColor = new RgbaHdrColor(block.r, block.g, block.b, block.a);
                return [new HdrEndpointPair(hdrColor, hdrColor, ValuesAreLns: false)];
            }
            else
            {
                // LDR void extent: ushort values are UNORM16, convert to byte range
                var ldrColor = new RgbaColor(
                    (byte)(block.r >> 8),
                    (byte)(block.g >> 8),
                    (byte)(block.b >> 8),
                    (byte)(block.a >> 8));
                return [new LdrEndpointPair(ldrColor, ldrColor)];
            }
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
        /// Interpolates an LDR channel value and returns the full 16-bit UNORM result
        /// (before reduction to byte). Used by the HDR output path for LDR endpoints.
        /// </summary>
        private static ushort InterpolateLdrAsUnorm16(RgbaColor first, RgbaColor second, int channel, int weight)
        {
            int p0 = channel switch { 0 => first.R, 1 => first.G, 2 => first.B, _ => first.A };
            int p1 = channel switch { 0 => second.R, 1 => second.G, 2 => second.B, _ => second.A };

            int c0 = (p0 << 8) | p0;
            int c1 = (p1 << 8) | p1;
            int c = (c0 * (64 - weight) + c1 * weight + 32) / 64;
            return (ushort)Math.Clamp(c, 0, 0xFFFF);
        }

        /// <summary>
        /// Interpolates an HDR channel value between two endpoints using the specified weight.
        /// </summary>
        /// <remarks>
        /// HDR endpoints are already 16-bit values (FP16 bit patterns). Unlike LDR interpolation
        /// which expands 8-bit to 16-bit before interpolating, HDR interpolation operates directly
        /// on the 16-bit values
        /// </remarks>
        private static ushort InterpolateChannelHdr(RgbaHdrColor first, RgbaHdrColor second, int channel, int weight)
        {
            int p0 = first[channel];
            int p1 = second[channel];

            int c = (p0 * (64 - weight) + p1 * weight + 32) / 64;
            return (ushort)Math.Clamp(c, 0, 0xFFFF);
        }

        /// <summary>
        /// Returns the HDR color at the specified pixel position.
        /// </summary>
        /// <remarks>
        /// For HDR endpoints, returns full 16-bit precision (0-65535) per channel.
        /// For LDR endpoints, upscales to HDR range.
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

                // Convert LDR (0-255) to HDR (0-65535)
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
        /// Writes the HDR float values for the pixel at (x, y) into the output span.
        /// </summary>
        /// <remarks>
        /// For HDR endpoints, values are in LNS (Log-Normalized Space). After interpolation
        /// in LNS, the result is converted to FP16 via <see cref="LnsToSf16"/> then widened to float.
        /// For Mode 14 (HDR RGB + LDR Alpha), the alpha channel is UNORM16 instead of LNS.
        /// For LDR endpoints, the interpolated UNORM16 value is normalized to 0.0-1.0.
        /// </remarks>
        public void WriteHdrPixel(int x, int y, Span<float> output)
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
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = GetWeightForPixel(index, channel);
                    ushort interpolated = InterpolateChannelHdr(hdrPair.Low, hdrPair.High, channel, weight);

                    if (channel == 3 && hdrPair.AlphaIsLdr)
                    {
                        // Mode 14: alpha is UNORM16, normalize directly
                        output[channel] = interpolated / 65535.0f;
                    }
                    else if (hdrPair.ValuesAreLns)
                    {
                        // Normal HDR block: convert from LNS to FP16, then to float
                        ushort sf16 = LnsToSf16(interpolated);
                        output[channel] = (float)BitConverter.UInt16BitsToHalf(sf16);
                    }
                    else
                    {
                        // Void extent HDR: values are already FP16 bit patterns
                        output[channel] = (float)BitConverter.UInt16BitsToHalf(interpolated);
                    }
                }
            }
            else if (endpointPair is LdrEndpointPair ldrPair)
            {
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int weight = GetWeightForPixel(index, channel);
                    ushort unorm16 = InterpolateLdrAsUnorm16(ldrPair.Low, ldrPair.High, channel, weight);
                    output[channel] = unorm16 / 65535.0f;
                }
            }
            else
            {
                throw new InvalidOperationException("Unknown endpoint pair type");
            }
        }

        /// <summary>
        /// Converts a 16-bit LNS (Log-Normalized Space) value to a 16-bit SF16 (FP16) bit pattern.
        /// </summary>
        /// <remarks>
        /// The LNS value encodes a 5-bit exponent in the upper bits and an 11-bit mantissa
        /// in the lower bits. The mantissa is transformed using a piecewise linear function
        /// before being combined with the exponent to form the FP16 result.
        /// </remarks>
        private static ushort LnsToSf16(int lns)
        {
            int mc = lns & 0x7FF;       // Lower 11 bits: mantissa component
            int ec = (lns >> 11) & 0x1F; // Upper 5 bits: exponent component

            int mt;
            if (mc < 512)
                mt = mc * 3;
            else if (mc < 1536)
                mt = mc * 4 - 512;
            else
                mt = mc * 5 - 2048;

            int result = (ec << 10) | (mt >> 3);
            return (ushort)Math.Min(result, 0x7BFF); // Clamp to max finite FP16
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
