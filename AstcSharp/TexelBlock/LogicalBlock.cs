using System.Buffers;
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

        private static readonly ArrayPool<int> _intPool = ArrayPool<int>.Shared;

        private ColorEndpointPair[] _endpoints;
        private int _endpointCount;
        private int[] _weights;
        private bool _weightsPooled;
        private Partition _partition;
        private DualPlaneData? _dualPlane;

        private class DualPlaneData
        {
            public int Channel;
            public int[] Weights = [];
            public bool WeightsPooled;
        }

        public LogicalBlock(Footprint footprint)
        {
            _endpoints = [ColorEndpointPair.Ldr(RgbaColor.Empty, RgbaColor.Empty)];
            _endpointCount = 1;
            _weights = new int[footprint.PixelCount];
            _partition = new Partition(footprint, 1, 0)
            {
                assignment = new int[footprint.PixelCount]
            };
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
        {
            (_endpoints, _endpointCount) = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = _intPool.Rent(footprint.PixelCount);
            _weightsPooled = true;
            CalculateWeights(footprint, block);
        }

        public LogicalBlock(Footprint footprint, IntermediateBlock.VoidExtentData block)
        {
            (_endpoints, _endpointCount) = DecodeEndpoints(block);
            _partition = ComputePartition(footprint, block);
            _weights = _intPool.Rent(footprint.PixelCount);
            _weightsPooled = true;
            Array.Clear(_weights, 0, footprint.PixelCount);
        }

        private static (ColorEndpointPair[] eps, int count) DecodeEndpoints(IntermediateBlock.IntermediateBlockData block)
        {
            int endpointRange = block.endpointRange.HasValue ? block.endpointRange.Value : IntermediateBlock.EndpointRangeForBlock(block);
            if (endpointRange <= 0) throw new InvalidOperationException("Invalid endpoint range");
            var eps = new ColorEndpointPair[block.endpointCount];
            for (int i = 0; i < block.endpointCount; i++)
            {
                ref var ed = ref block.endpoints[i];
                ReadOnlySpan<int> colorSpan = ((ReadOnlySpan<int>)ed.colors)[..ed.colorCount];
                eps[i] = EndpointCodec.DecodeColorsForModePolymorphic(colorSpan, endpointRange, ed.mode);
            }
            return (eps, block.endpointCount);
        }

        private static (ColorEndpointPair[] eps, int count) DecodeEndpoints(IntermediateBlock.VoidExtentData block)
        {
            if (block.isHdr)
            {
                // HDR void extent: ushort values are FP16 bit patterns (not LNS)
                var hdrColor = new RgbaHdrColor(block.r, block.g, block.b, block.a);
                return ([ColorEndpointPair.Hdr(hdrColor, hdrColor, valuesAreLns: false)], 1);
            }
            else
            {
                // LDR void extent: ushort values are UNORM16, convert to byte range
                var ldrColor = new RgbaColor(
                    (byte)(block.r >> 8),
                    (byte)(block.g >> 8),
                    (byte)(block.b >> 8),
                    (byte)(block.a >> 8));
                return ([ColorEndpointPair.Ldr(ldrColor, ldrColor)], 1);
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Footprint, Partition> _singlePartitionCache = new();

        private static Partition GenerateSinglePartition(Footprint footprint)
        {
            return _singlePartitionCache.GetOrAdd(footprint, static f => new Partition(f, 1, 0)
            {
                assignment = new int[f.PixelCount]
            });
        }

        private static Partition ComputePartition(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
            => block.partitionId.HasValue
                ? Partition.GetASTCPartition(footprint, block.endpointCount, block.partitionId.Value)
                : GenerateSinglePartition(footprint);

        private static Partition ComputePartition(Footprint footprint, IntermediateBlock.VoidExtentData block)
            => GenerateSinglePartition(footprint);

        private void CalculateWeights(Footprint footprint, IntermediateBlock.IntermediateBlockData block)
        {
            int gridSize = block.weightGridX * block.weightGridY;
            int weightFrequency = block.dualPlaneChannel.HasValue ? 2 : 1;

            // Get decimation info once for both planes
            var di = DecimationTable.Get(footprint, block.weightGridX, block.weightGridY);

            // stackalloc avoids per-block heap allocation (max 12×12 = 144 ints = 576 bytes)
            Span<int> unquantized = stackalloc int[gridSize];
            for (int i = 0; i < gridSize; ++i)
            {
                unquantized[i] = Quantization.UnquantizeWeightFromRange(
                    block.weights[i * weightFrequency], block.weightRange);
            }
            DecimationTable.InfillWeights(unquantized, di, _weights);

            if (block.dualPlaneChannel.HasValue)
            {
                _dualPlane = new DualPlaneData
                {
                    Channel = block.dualPlaneChannel.Value,
                    Weights = _intPool.Rent(footprint.PixelCount),
                    WeightsPooled = true
                };
                for (int i = 0; i < gridSize; ++i)
                {
                    unquantized[i] = Quantization.UnquantizeWeightFromRange(
                        block.weights[i * weightFrequency + 1], block.weightRange);
                }
                DecimationTable.InfillWeights(unquantized, di, _dualPlane.Weights);
            }
        }

        private void CalculateWeights(Footprint footprint, IntermediateBlock.VoidExtentData block)
        {
            // _weights already allocated and cleared in constructor
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
            ref var ep = ref _endpoints[part];

            int w = _weights[index];
            if (!ep.IsHdr)
            {
                if (_dualPlane != null)
                    return SimdHelpers.InterpolateColorLdrDualPlane(
                        ep.LdrLow, ep.LdrHigh, w, _dualPlane.Channel, _dualPlane.Weights[index]);
                return SimdHelpers.InterpolateColorLdr(ep.LdrLow, ep.LdrHigh, w);
            }
            else
            {
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaColor(
                        r: InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], dpCh == 0 ? dpW : w) >> 8,
                        g: InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], dpCh == 1 ? dpW : w) >> 8,
                        b: InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], dpCh == 2 ? dpW : w) >> 8,
                        a: InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], dpCh == 3 ? dpW : w) >> 8);
                }
                return new RgbaColor(
                    r: InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], w) >> 8,
                    g: InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], w) >> 8,
                    b: InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], w) >> 8,
                    a: InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], w) >> 8);
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
            ref var ep = ref _endpoints[part];

            int w = _weights[index];
            if (ep.IsHdr)
            {
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaHdrColor(
                        InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], dpCh == 0 ? dpW : w),
                        InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], dpCh == 1 ? dpW : w),
                        InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], dpCh == 2 ? dpW : w),
                        InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], dpCh == 3 ? dpW : w));
                }
                return new RgbaHdrColor(
                    InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], w),
                    InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], w),
                    InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], w),
                    InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], w));
            }
            else
            {
                if (_dualPlane != null)
                {
                    int dpCh = _dualPlane.Channel;
                    int dpW = _dualPlane.Weights[index];
                    return new RgbaHdrColor(
                        (ushort)(InterpolateChannel(ep.LdrLow.R, ep.LdrHigh.R, dpCh == 0 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ep.LdrLow.G, ep.LdrHigh.G, dpCh == 1 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ep.LdrLow.B, ep.LdrHigh.B, dpCh == 2 ? dpW : w) * 257),
                        (ushort)(InterpolateChannel(ep.LdrLow.A, ep.LdrHigh.A, dpCh == 3 ? dpW : w) * 257));
                }
                return new RgbaHdrColor(
                    (ushort)(InterpolateChannel(ep.LdrLow.R, ep.LdrHigh.R, w) * 257),
                    (ushort)(InterpolateChannel(ep.LdrLow.G, ep.LdrHigh.G, w) * 257),
                    (ushort)(InterpolateChannel(ep.LdrLow.B, ep.LdrHigh.B, w) * 257),
                    (ushort)(InterpolateChannel(ep.LdrLow.A, ep.LdrHigh.A, w) * 257));
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
            ref var ep = ref _endpoints[part];

            int w = _weights[index];
            int dpCh = _dualPlane?.Channel ?? -1;
            int dpW = _dualPlane?.Weights[index] ?? w;

            if (ep.IsHdr)
            {
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int cw = (channel == dpCh) ? dpW : w;
                    ushort interpolated = InterpolateChannelHdr(ep.HdrLow[channel], ep.HdrHigh[channel], cw);

                    if (channel == 3 && ep.AlphaIsLdr)
                    {
                        // Mode 14: alpha is UNORM16, normalize directly
                        output[channel] = interpolated / 65535.0f;
                    }
                    else if (ep.ValuesAreLns)
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
            else
            {
                for (int channel = 0; channel < ChannelCount; ++channel)
                {
                    int cw = (channel == dpCh) ? dpW : w;
                    int p0 = channel switch { 0 => ep.LdrLow.R, 1 => ep.LdrLow.G, 2 => ep.LdrLow.B, _ => ep.LdrLow.A };
                    int p1 = channel switch { 0 => ep.LdrHigh.R, 1 => ep.LdrHigh.G, 2 => ep.LdrHigh.B, _ => ep.LdrHigh.A };
                    ushort unorm16 = InterpolateLdrAsUnorm16(p0, p1, cw);
                    output[channel] = unorm16 / 65535.0f;
                }
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
            ref var ep0 = ref _endpoints[0];

            if (!ep0.IsHdr && _partition.numParts == 1)
            {
                // Fast path: single-partition LDR block (most common case)
                int lowR = ep0.LdrLow.R, lowG = ep0.LdrLow.G, lowB = ep0.LdrLow.B, lowA = ep0.LdrLow.A;
                int highR = ep0.LdrHigh.R, highG = ep0.LdrHigh.G, highB = ep0.LdrHigh.B, highA = ep0.LdrHigh.A;

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
                ref var ep = ref _endpoints[part];

                int w = _weights[i];
                if (!ep.IsHdr)
                {
                    if (_dualPlane != null)
                    {
                        SimdHelpers.WritePixel1LdrDualPlane(
                            buffer, i * 4,
                            ep.LdrLow.R, ep.LdrLow.G, ep.LdrLow.B, ep.LdrLow.A,
                            ep.LdrHigh.R, ep.LdrHigh.G, ep.LdrHigh.B, ep.LdrHigh.A,
                            w, _dualPlane.Channel, _dualPlane.Weights[i]);
                    }
                    else
                    {
                        SimdHelpers.WritePixel1Ldr(
                            buffer, i * 4,
                            ep.LdrLow.R, ep.LdrLow.G, ep.LdrLow.B, ep.LdrLow.A,
                            ep.LdrHigh.R, ep.LdrHigh.G, ep.LdrHigh.B, ep.LdrHigh.A,
                            w);
                    }
                }
                else
                {
                    int dpCh = _dualPlane?.Channel ?? -1;
                    int dpW = _dualPlane?.Weights[i] ?? w;
                    buffer[i * 4 + 0] = (byte)(InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], dpCh == 0 ? dpW : w) >> 8);
                    buffer[i * 4 + 1] = (byte)(InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], dpCh == 1 ? dpW : w) >> 8);
                    buffer[i * 4 + 2] = (byte)(InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], dpCh == 2 ? dpW : w) >> 8);
                    buffer[i * 4 + 3] = (byte)(InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], dpCh == 3 ? dpW : w) >> 8);
                }
            }
        }

        public void SetPartition(Partition p)
        {
            if (!p.footprint.Equals(_partition.footprint))
                throw new InvalidOperationException("New partitions may not be for a different footprint");
            _partition = p;
            if (_endpointCount < p.numParts)
            {
                var newEndpoints = new ColorEndpointPair[p.numParts];
                Array.Copy(_endpoints, newEndpoints, _endpointCount);
                for (int i = _endpointCount; i < p.numParts; i++)
                    newEndpoints[i] = ColorEndpointPair.Ldr(RgbaColor.Empty, RgbaColor.Empty);
                _endpoints = newEndpoints;
            }
            _endpointCount = p.numParts;
        }

        public void SetEndpoints((RgbaColor first, RgbaColor second) eps, int subset)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(subset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(subset, _partition.numParts);

            _endpoints[subset] = ColorEndpointPair.Ldr(eps.first, eps.second);
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

        /// <summary>
        /// Returns any pooled arrays to the shared ArrayPool.
        /// Must be called after the block is no longer needed.
        /// </summary>
        internal void ReturnPooledArrays()
        {
            if (_weightsPooled)
            {
                _intPool.Return(_weights);
                _weights = [];
                _weightsPooled = false;
            }
            if (_dualPlane is { WeightsPooled: true })
            {
                _intPool.Return(_dualPlane.Weights);
                _dualPlane.Weights = [];
                _dualPlane.WeightsPooled = false;
            }
        }

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
                if (intermediateBlock is null) return null;

                var result = new LogicalBlock(footprint, intermediateBlock);
                intermediateBlock.ReturnPooledArrays();
                return result;
            }
        }
    }
}
