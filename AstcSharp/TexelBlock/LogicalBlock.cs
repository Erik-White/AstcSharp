using System.Runtime.Intrinsics;
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
        private int[] _weights;
        private Partition _partition;
        private DualPlaneData? _dualPlane;

        private class DualPlaneData
        {
            public int Channel;
            public int[] Weights = [];
        }

        public LogicalBlock(Footprint footprint)
        {
            _endpoints = [new LdrEndpointPair(RgbaColor.Empty, RgbaColor.Empty)];
            _weights = new int[footprint.PixelCount];
            _partition = new Partition(footprint, 1, 0)
            {
                assignment = new int[footprint.PixelCount]
            };
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = new int[footprint.PixelCount];
            CalculateWeights(footprint, block);
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.VoidExtentData block)
        {
            _endpoints = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = new int[footprint.PixelCount];
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
            p.assignment = new int[footprint.PixelCount];
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

            var unquantized = new int[gridSize];
            for (int i = 0; i < gridSize; ++i)
            {
                int weight = block.weights[i * weightFrequency];
                unquantized[i] = Quantization.UnquantizeWeightFromRange(weight, block.weightRange);
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
            _weights = new int[footprint.PixelCount];
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
                int w = _weights[index];
                if (_dualPlane != null)
                    return SimdHelpers.InterpolateColorLdrDualPlane(
                        ldrPair.Low, ldrPair.High, w, _dualPlane.Channel, _dualPlane.Weights[index]);
                return SimdHelpers.InterpolateColorLdr(ldrPair.Low, ldrPair.High, w);
            }
            else if (endpointPair is HdrEndpointPair hdrPair)
            {
                // HDR endpoints: downscale to LDR for legacy ColorAt() method
                int w = _weights[index];
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaColor(
                        r: InterpolateChannelHdr(hdrPair.Low[0], hdrPair.High[0], dpCh == 0 ? dpW : w) >> 8,
                        g: InterpolateChannelHdr(hdrPair.Low[1], hdrPair.High[1], dpCh == 1 ? dpW : w) >> 8,
                        b: InterpolateChannelHdr(hdrPair.Low[2], hdrPair.High[2], dpCh == 2 ? dpW : w) >> 8,
                        a: InterpolateChannelHdr(hdrPair.Low[3], hdrPair.High[3], dpCh == 3 ? dpW : w) >> 8);
                }
                return new RgbaColor(
                    r: InterpolateChannelHdr(hdrPair.Low[0], hdrPair.High[0], w) >> 8,
                    g: InterpolateChannelHdr(hdrPair.Low[1], hdrPair.High[1], w) >> 8,
                    b: InterpolateChannelHdr(hdrPair.Low[2], hdrPair.High[2], w) >> 8,
                    a: InterpolateChannelHdr(hdrPair.Low[3], hdrPair.High[3], w) >> 8);
            }
            else
            {
                throw new InvalidOperationException("Unknown endpoint pair type");
            }
        }

        private static int InterpolateChannel(int p0, int p1, int weight)
        {
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
        private static ushort InterpolateLdrAsUnorm16(int p0, int p1, int weight)
        {
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
        private static ushort InterpolateChannelHdr(int p0, int p1, int weight)
        {
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
                int w = _weights[index];
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaHdrColor(
                        InterpolateChannelHdr(hdrPair.Low[0], hdrPair.High[0], dpCh == 0 ? dpW : w),
                        InterpolateChannelHdr(hdrPair.Low[1], hdrPair.High[1], dpCh == 1 ? dpW : w),
                        InterpolateChannelHdr(hdrPair.Low[2], hdrPair.High[2], dpCh == 2 ? dpW : w),
                        InterpolateChannelHdr(hdrPair.Low[3], hdrPair.High[3], dpCh == 3 ? dpW : w));
                }
                return new RgbaHdrColor(
                    InterpolateChannelHdr(hdrPair.Low[0], hdrPair.High[0], w),
                    InterpolateChannelHdr(hdrPair.Low[1], hdrPair.High[1], w),
                    InterpolateChannelHdr(hdrPair.Low[2], hdrPair.High[2], w),
                    InterpolateChannelHdr(hdrPair.Low[3], hdrPair.High[3], w));
            }
            else if (endpointPair is LdrEndpointPair ldrPair)
            {
                int w = _weights[index];
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaHdrColor(
                        (ushort)(InterpolateChannel(ldrPair.Low.R, ldrPair.High.R, dpCh == 0 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ldrPair.Low.G, ldrPair.High.G, dpCh == 1 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ldrPair.Low.B, ldrPair.High.B, dpCh == 2 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ldrPair.Low.A, ldrPair.High.A, dpCh == 3 ? dpW : w) * 257));
                }
                return new RgbaHdrColor(
                    (ushort)(InterpolateChannel(ldrPair.Low.R, ldrPair.High.R, w) * 257),
                    (ushort)(InterpolateChannel(ldrPair.Low.G, ldrPair.High.G, w) * 257),
                    (ushort)(InterpolateChannel(ldrPair.Low.B, ldrPair.High.B, w) * 257),
                    (ushort)(InterpolateChannel(ldrPair.Low.A, ldrPair.High.A, w) * 257));
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
                int w = _weights[index];
                int dpCh = _dualPlane?.Channel ?? -1;
                int dpW = _dualPlane?.Weights[index] ?? w;

                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int cw = (channel == dpCh) ? dpW : w;
                    ushort interpolated = InterpolateChannelHdr(hdrPair.Low[channel], hdrPair.High[channel], cw);

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
                int w = _weights[index];
                int dpCh = _dualPlane?.Channel ?? -1;
                int dpW = _dualPlane?.Weights[index] ?? w;

                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int cw = (channel == dpCh) ? dpW : w;
                    int p0 = channel switch { 0 => ldrPair.Low.R, 1 => ldrPair.Low.G, 2 => ldrPair.Low.B, _ => ldrPair.Low.A };
                    int p1 = channel switch { 0 => ldrPair.High.R, 1 => ldrPair.High.G, 2 => ldrPair.High.B, _ => ldrPair.High.A };
                    ushort unorm16 = InterpolateLdrAsUnorm16(p0, p1, cw);
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

        /// <summary>
        /// Writes all pixels in the block directly to the output buffer in RGBA byte format.
        /// Avoids per-pixel method call overhead, type dispatch, and RgbaColor allocation.
        /// </summary>
        public void WriteAllPixelsLdr(Footprint footprint, Span<byte> buffer)
        {
            var ep0 = _endpoints[0];

            if (ep0 is LdrEndpointPair ldrPair && _partition.numParts == 1)
            {
                // Fast path: single-partition LDR block (most common case)
                int lowR = ldrPair.Low.R, lowG = ldrPair.Low.G, lowB = ldrPair.Low.B, lowA = ldrPair.Low.A;
                int highR = ldrPair.High.R, highG = ldrPair.High.G, highB = ldrPair.High.B, highA = ldrPair.High.A;

                if (_dualPlane == null)
                {
                    WriteLdrSinglePartition(buffer, footprint, lowR, lowG, lowB, lowA, highR, highG, highB, highA);
                }
                else
                {
                    int dpCh = _dualPlane.Channel;
                    var dpWeights = _dualPlane.Weights;
                    int pixelCount = footprint.PixelCount;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        SimdHelpers.WritePixel1LdrDualPlane(
                            buffer, i * 4,
                            lowR, lowG, lowB, lowA, highR, highG, highB, highA,
                            _weights[i], dpCh, dpWeights[i]);
                    }
                }
            }
            else
            {
                // General path: multi-partition or HDR blocks
                WriteAllPixelsGeneral(footprint, buffer);
            }
        }

        private void WriteLdrSinglePartition(
            Span<byte> buffer,
            Footprint footprint,
            int lowR,
            int lowG,
            int lowB,
            int lowA,
            int highR,
            int highG,
            int highB,
            int highA)
        {
            int pixelCount = footprint.PixelCount;
            int i = 0;

            if (Vector128.IsHardwareAccelerated)
            {
                // Process 4 pixels at a time: 4 different weights × same endpoints
                int limit = pixelCount - 3;
                for (; i < limit; i += 4)
                {
                    var weights = Vector128.Create(_weights[i], _weights[i + 1], _weights[i + 2], _weights[i + 3]);
                    SimdHelpers.WritePixels4Ldr(
                        buffer, i * 4,
                        lowR, lowG, lowB, lowA, highR, highG, highB, highA,
                        weights);
                }
            }

            // Scalar remainder
            for (; i < pixelCount; i++)
            {
                SimdHelpers.WritePixel1Ldr(
                    buffer, i * 4,
                    lowR, lowG, lowB, lowA, highR, highG, highB, highA,
                    _weights[i]);
            }
        }

        private void WriteAllPixelsGeneral(Footprint footprint, Span<byte> buffer)
        {
            int pixelCount = footprint.PixelCount;
            for (int i = 0; i < pixelCount; i++)
            {
                int part = _partition.assignment[i];
                var endpointPair = _endpoints[part];

                if (endpointPair is LdrEndpointPair ldrPair)
                {
                    int w = _weights[i];
                    if (_dualPlane != null)
                    {
                        SimdHelpers.WritePixel1LdrDualPlane(
                            buffer, i * 4,
                            ldrPair.Low.R, ldrPair.Low.G, ldrPair.Low.B, ldrPair.Low.A,
                            ldrPair.High.R, ldrPair.High.G, ldrPair.High.B, ldrPair.High.A,
                            w, _dualPlane.Channel, _dualPlane.Weights[i]);
                    }
                    else
                    {
                        SimdHelpers.WritePixel1Ldr(
                            buffer, i * 4,
                            ldrPair.Low.R, ldrPair.Low.G, ldrPair.Low.B, ldrPair.Low.A,
                            ldrPair.High.R, ldrPair.High.G, ldrPair.High.B, ldrPair.High.A,
                            w);
                    }
                }
                else if (endpointPair is HdrEndpointPair hdrPair)
                {
                    int w = _weights[i];
                    int dpCh = _dualPlane?.Channel ?? -1;
                    int dpW = _dualPlane?.Weights[i] ?? w;
                    buffer[i * 4 + 0] = (byte)(InterpolateChannelHdr(hdrPair.Low[0], hdrPair.High[0], dpCh == 0 ? dpW : w) >> 8);
                    buffer[i * 4 + 1] = (byte)(InterpolateChannelHdr(hdrPair.Low[1], hdrPair.High[1], dpCh == 1 ? dpW : w) >> 8);
                    buffer[i * 4 + 2] = (byte)(InterpolateChannelHdr(hdrPair.Low[2], hdrPair.High[2], dpCh == 2 ? dpW : w) >> 8);
                    buffer[i * 4 + 3] = (byte)(InterpolateChannelHdr(hdrPair.Low[3], hdrPair.High[3], dpCh == 3 ? dpW : w) >> 8);
                }
            }
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
            else { _dualPlane = new DualPlaneData { Channel = channel, Weights = (int[])_weights.Clone() }; }
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
