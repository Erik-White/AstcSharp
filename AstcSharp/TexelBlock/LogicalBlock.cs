using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoder;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

internal class LogicalBlock
{
    private ColorEndpointPair[] _endpoints;
    private int _endpointCount;
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
        _endpoints = [ColorEndpointPair.Ldr(RgbaColor.Empty, RgbaColor.Empty)];
        _endpointCount = 1;
        _weights = new int[footprint.PixelCount];
        _partition = new Partition(footprint, 1, 0)
        {
            assignment = new int[footprint.PixelCount]
        };
    }

    public LogicalBlock(Footprint footprint, in IntermediateBlock.IntermediateBlockData block)
    {
        _endpoints = new ColorEndpointPair[block.endpointCount];
        _endpointCount = DecodeEndpoints(in block, _endpoints);
        _partition = ComputePartition(footprint, in block);
        _weights = new int[footprint.PixelCount];
        CalculateWeights(footprint, in block);
    }

    public LogicalBlock(Footprint footprint, IntermediateBlock.VoidExtentData block)
    {
        _endpoints = new ColorEndpointPair[1];
        _endpointCount = DecodeEndpoints(block, _endpoints);
        _partition = ComputePartition(footprint, block);
        _weights = new int[footprint.PixelCount];
    }

    /// <summary>
    /// Direct-decode constructor: decodes directly from raw bits + BlockInfo,
    /// bypassing IntermediateBlock and using batch unquantize operations.
    /// </summary>
    private LogicalBlock(Footprint footprint, UInt128 bits, in BlockInfo info)
    {
        // --- BISE decode + batch unquantize color endpoint values ---
        Span<int> colors = stackalloc int[info.ColorValuesCount];
        FusedBlockDecoder.DecodeBiseValues(bits, info.ColorStartBit, info.ColorBitCount,
            info.ColorValuesRange, info.ColorValuesCount, colors);
        Quantization.UnquantizeCEValuesBatch(colors, info.ColorValuesCount, info.ColorValuesRange);

        // --- Decode endpoints per partition ---
        _endpointCount = info.PartitionCount;
        _endpoints = new ColorEndpointPair[_endpointCount];
        int colorIndex = 0;
        for (int i = 0; i < _endpointCount; i++)
        {
            var mode = info.GetEndpointMode(i);
            int colorCount = mode.GetColorValuesCount();
            ReadOnlySpan<int> slice = colors.Slice(colorIndex, colorCount);
            _endpoints[i] = EndpointCodec.DecodeColorsForModePolymorphicUnquantized(slice, mode);
            colorIndex += colorCount;
        }

        // --- Set up partition ---
        _partition = info.PartitionCount > 1
            ? Partition.GetASTCPartition(footprint, info.PartitionCount,
                (int)BitOperations.GetBits(bits.Low(), 13, 10))
            : GenerateSinglePartition(footprint);

        // --- BISE decode + unquantize + infill weights ---
        int gridSize = info.GridWidth * info.GridHeight;
        bool isDualPlane = info.IsDualPlane;
        int totalWeights = isDualPlane ? gridSize * 2 : gridSize;

        Span<int> rawWeights = stackalloc int[totalWeights];
        FusedBlockDecoder.DecodeBiseWeights(bits, info.WeightBitCount, info.WeightRange,
            totalWeights, rawWeights);

        var di = DecimationTable.Get(footprint, info.GridWidth, info.GridHeight);
        _weights = new int[footprint.PixelCount];

        if (!isDualPlane)
        {
            Quantization.UnquantizeWeightsBatch(rawWeights, gridSize, info.WeightRange);
            DecimationTable.InfillWeights(rawWeights[..gridSize], di, _weights);
        }
        else
        {
            // De-interleave: even indices -> plane0, odd indices -> plane1
            Span<int> plane0 = stackalloc int[gridSize];
            Span<int> plane1 = stackalloc int[gridSize];
            for (int i = 0; i < gridSize; i++)
            {
                plane0[i] = rawWeights[i * 2];
                plane1[i] = rawWeights[i * 2 + 1];
            }

            Quantization.UnquantizeWeightsBatch(plane0, gridSize, info.WeightRange);
            Quantization.UnquantizeWeightsBatch(plane1, gridSize, info.WeightRange);

            DecimationTable.InfillWeights(plane0, di, _weights);

            _dualPlane = new DualPlaneData
            {
                Channel = info.DualPlaneChannel,
                Weights = new int[footprint.PixelCount]
            };
            DecimationTable.InfillWeights(plane1, di, _dualPlane.Weights);
        }
    }

    private static int DecodeEndpoints(in IntermediateBlock.IntermediateBlockData block, ColorEndpointPair[] endpointPair)
    {
        int endpointRange = block.endpointRange ?? IntermediateBlock.EndpointRangeForBlock(block);
        if (endpointRange <= 0) throw new InvalidOperationException("Invalid endpoint range");
        for (int i = 0; i < block.endpointCount; i++)
        {
            var ed = block.endpoints[i];
            ReadOnlySpan<int> colorSpan = ((ReadOnlySpan<int>)ed.colors)[..ed.colorCount];
            endpointPair[i] = EndpointCodec.DecodeColorsForModePolymorphic(colorSpan, endpointRange, ed.mode);
        }
        return block.endpointCount;
    }

    private static int DecodeEndpoints(IntermediateBlock.VoidExtentData block, ColorEndpointPair[] endpointPair)
    {
        if (block.isHdr)
        {
            // HDR void extent: ushort values are FP16 bit patterns (not LNS)
            var hdrColor = new RgbaHdrColor(block.r, block.g, block.b, block.a);
            endpointPair[0] = ColorEndpointPair.Hdr(hdrColor, hdrColor, valuesAreLns: false);
        }
        else
        {
            // LDR void extent: ushort values are UNORM16, convert to byte range
            var ldrColor = new RgbaColor(
                (byte)(block.r >> 8),
                (byte)(block.g >> 8),
                (byte)(block.b >> 8),
                (byte)(block.a >> 8));
            endpointPair[0] = ColorEndpointPair.Ldr(ldrColor, ldrColor);
        }
        return 1;
    }

    private static Partition GenerateSinglePartition(Footprint footprint)
    {
        return new Partition(footprint, 1, 0)
        {
            assignment = new int[footprint.PixelCount]
        };
    }

    private static Partition ComputePartition(Footprint footprint, in IntermediateBlock.IntermediateBlockData block)
        => block.partitionId.HasValue
            ? Partition.GetASTCPartition(footprint, block.endpointCount, block.partitionId.Value)
            : GenerateSinglePartition(footprint);

    private static Partition ComputePartition(Footprint footprint, IntermediateBlock.VoidExtentData block)
        => GenerateSinglePartition(footprint);

    private void CalculateWeights(Footprint footprint, in IntermediateBlock.IntermediateBlockData block)
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
            var dp = new DualPlaneData();
            dp.Channel = block.dualPlaneChannel.Value;
            dp.Weights = new int[footprint.PixelCount];
            _dualPlane = dp;
            for (int i = 0; i < gridSize; ++i)
            {
                unquantized[i] = Quantization.UnquantizeWeightFromRange(
                    block.weights[i * weightFrequency + 1], block.weightRange);
            }
            DecimationTable.InfillWeights(unquantized, di, _dualPlane.Weights);
        }
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
            if (_dualPlane is not null)
                return SimdHelpers.InterpolateColorLdrDualPlane(
                    ep.LdrLow, ep.LdrHigh, w, _dualPlane.Channel, _dualPlane.Weights[index]);
            return SimdHelpers.InterpolateColorLdr(ep.LdrLow, ep.LdrHigh, w);
        }
        else
        {
            if (_dualPlane is not null)
            {
                int dualPlaneChannel = _dualPlane.Channel;
                int dualPlaneWeight = _dualPlane.Weights[index];
                return new RgbaColor(
                    r: InterpolateChannelHdr(ep.HdrLow[0], ep.HdrHigh[0], dualPlaneChannel == 0 ? dualPlaneWeight : w) >> 8,
                    g: InterpolateChannelHdr(ep.HdrLow[1], ep.HdrHigh[1], dualPlaneChannel == 1 ? dualPlaneWeight : w) >> 8,
                    b: InterpolateChannelHdr(ep.HdrLow[2], ep.HdrHigh[2], dualPlaneChannel == 2 ? dualPlaneWeight : w) >> 8,
                    a: InterpolateChannelHdr(ep.HdrLow[3], ep.HdrHigh[3], dualPlaneChannel == 3 ? dualPlaneWeight : w) >> 8);
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
            for (int channel = 0; channel < RgbaColor.BytesPerPixel; ++channel)
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
            for (int channel = 0; channel < RgbaColor.BytesPerPixel; ++channel)
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
    internal static ushort LnsToSf16(int lns)
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
                    SimdHelpers.WriteSinglePixelLdrDualPlane(
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
        for (int i = 0; i < pixelCount; i++)
        {
            SimdHelpers.WriteSinglePixelLdr(
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
                if (_dualPlane is not null)
                {
                    SimdHelpers.WriteSinglePixelLdrDualPlane(
                        buffer, i * 4,
                        ep.LdrLow.R, ep.LdrLow.G, ep.LdrLow.B, ep.LdrLow.A,
                        ep.LdrHigh.R, ep.LdrHigh.G, ep.LdrHigh.B, ep.LdrHigh.A,
                        w, _dualPlane.Channel, _dualPlane.Weights[i]);
                }
                else
                {
                    SimdHelpers.WriteSinglePixelLdr(
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

    public void SetEndpoints(RgbaColor firstEndpoint, RgbaColor secondEndpoint, int subset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(subset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(subset, _partition.numParts);

        _endpoints[subset] = ColorEndpointPair.Ldr(firstEndpoint, secondEndpoint);
    }

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

            return voidExtantIntermediateBlock is not null
                ? new LogicalBlock(footprint, voidExtantIntermediateBlock.Value)
                : null;
        }
        else
        {
            var info = BlockInfo.Decode(physicalBlock.BlockBits);
            if (!info.IsValid) return null;

            return new LogicalBlock(footprint, physicalBlock.BlockBits, in info);
        }
    }

    /// <summary>
    /// Fast path with pre-computed BlockInfo (avoids re-decoding when caller already has it).
    /// </summary>
    public static LogicalBlock? UnpackLogicalBlock(Footprint footprint, UInt128 bits, in BlockInfo info)
    {
        if (!info.IsValid) return null;

        if (info.IsVoidExtent)
        {
            // Void extent blocks are rare; fall back to existing PhysicalBlock path
            var pb = PhysicalBlock.Create(bits);
            var voidExtentData = IntermediateBlock.UnpackVoidExtent(pb);
            if (voidExtentData is null) return null;

            return new LogicalBlock(footprint, voidExtentData.Value);
        }
        else
        {
            return new LogicalBlock(footprint, bits, in info);
        }
    }
}
