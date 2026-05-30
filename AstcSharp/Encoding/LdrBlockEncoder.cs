using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes a single-partition LDR block using the RGBA-direct colour endpoint mode (CEM 12, spec
/// §C.2.14). Endpoints are fitted to the principal axis of the block's texels; per-texel weights
/// are each texel's projection onto the endpoint line. A weight grid (possibly decimated below the
/// footprint size, spec §C.2.18) is fitted to those weights. The grid size, weight range, and
/// colour range are chosen by searching the configurations that fit the 128-bit budget and keeping
/// the one with the lowest reconstruction error.
/// </summary>
internal static class LdrBlockEncoder
{
    // Total bits in an ASTC block (spec §C.2.7).
    private const int BlockBits = 128;

    // RGBA channels per texel.
    private const int ChannelCount = 4;

    private const ColorEndpointMode Mode = ColorEndpointMode.LdrRgbaDirect;
    private const int ColorValueCount = 8; // r0,r1,g0,g1,b0,b1,a0,a1

    // Block-mode layout for a single-partition block (spec §C.2.10): the 11-bit block mode, then
    // the 2-bit partition-count field, the colour endpoint mode, and the colour data.
    private const int BlockModeStartBit = 0;
    private const int BlockModeBits = 11;
    private const int PartitionCountStartBit = 11;
    private const int PartitionCountBits = 2;
    private const int PartitionCountField = 0; // single partition: (partitionCount - 1)
    private const int CemStartBit = 13;
    private const int CemBits = 4;
    private const int ColorStartBit = 17;

    // Candidate weight ranges to try, richest first (spec §C.2.7 Table 23 weight ranges).
    private static ReadOnlySpan<int> WeightRangeCandidates => [31, 23, 19, 15, 11, 9, 7, 5, 4, 3, 2, 1];

    // The maximum weight value the decoder interpolates with (spec §C.2.19): weights span [0, 64].
    private const int MaxWeight = 64;

    // Maximum value of an 8-bit colour channel.
    private const int MaxChannel = 255;

    // Power-iteration passes used to estimate the endpoint principal axis. The covariance is 4x4,
    // so a handful of iterations converges to the dominant eigenvector.
    private const int PrincipalAxisIterations = 8;

    // Below this vector norm the covariance has no dominant direction (a constant or near-constant
    // block). The principal-axis search then falls back to a fixed diagonal.
    private const double DegenerateAxisNorm = 1e-9;

    // Grid dimensions range from 2 to 12 (spec §C.2.8); a single weight plane holds at most 64
    // weights (spec §C.2.11), and the weight bit total must fall in [24, 96].
    private const int MinGridDim = 2;
    private const int MaxGridDim = 12;
    private const int MaxGridWeights = 64;
    private const int MinWeightBits = 24;
    private const int MaxWeightBits = 96;

    /// <summary>
    /// Encodes <paramref name="texels"/> (one <see cref="RgbaColor"/> per footprint texel, raster
    /// order) into a 128-bit block. Searches weight-grid sizes from the footprint down to 2x2 and,
    /// per grid, the weight ranges that fit the bit budget, keeping the configuration with the
    /// lowest reconstruction error. A grid smaller than the footprint (decimation, spec §C.2.18)
    /// is what makes footprints larger than 64 texels encodable and lets large blocks spend more
    /// bits per weight.
    /// </summary>
    public static UInt128 Encode(ReadOnlySpan<RgbaColor> texels, Footprint footprint)
    {
        (RgbaColor low, RgbaColor high) = FitEndpoints(texels);

        int texelCount = footprint.PixelCount;
        Span<int> bestColorValues = stackalloc int[ColorValueCount];
        Span<int> candidateColorValues = stackalloc int[ColorValueCount];
        Span<int> bestGridWeights = stackalloc int[MaxGridWeights];
        Span<int> candidateGridWeights = stackalloc int[MaxGridWeights];

        // Per-config scratch, allocated once and reused across every configuration tried below so
        // the search loop does not grow the stack per iteration. Each buffer is fully written
        // before use in EvaluateConfig.
        var scratch = new ConfigScratch(
            effectiveLow: stackalloc int[ChannelCount],
            effectiveHigh: stackalloc int[ChannelCount],
            idealWeights: stackalloc int[texelCount],
            fittedGrid: stackalloc double[MaxGridWeights],
            effectiveGrid: stackalloc int[MaxGridWeights],
            perTexelWeights: stackalloc int[texelCount]);

        long bestError = long.MaxValue;
        int bestGridWidth = 0, bestGridHeight = 0, bestWeightRange = 0, bestColorRange = 0;

        int maxGridWidth = Math.Min(footprint.Width, MaxGridDim);
        int maxGridHeight = Math.Min(footprint.Height, MaxGridDim);

        for (int gridHeight = MinGridDim; gridHeight <= maxGridHeight; gridHeight++)
        {
            for (int gridWidth = MinGridDim; gridWidth <= maxGridWidth; gridWidth++)
            {
                int gridWeightCount = gridWidth * gridHeight;
                if (gridWeightCount > MaxGridWeights)
                {
                    continue;
                }

                foreach (int weightRange in WeightRangeCandidates)
                {
                    if (!BlockModeEncoder.TryEncode(gridWidth, gridHeight, weightRange, isDualPlane: false, out _))
                    {
                        continue;
                    }

                    int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(gridWeightCount, weightRange);
                    if (weightBitCount is < MinWeightBits or > MaxWeightBits)
                    {
                        continue;
                    }

                    int maxColorBits = BlockBits - weightBitCount - ColorStartBit;
                    if (!BlockModeDecoder.TryResolveColorEncoding(ColorValueCount, maxColorBits, out int colorRange, out _))
                    {
                        continue;
                    }

                    long error = EvaluateConfig(
                        texels, footprint, low, high, gridWidth, gridHeight, gridWeightCount, weightRange, colorRange,
                        candidateColorValues, candidateGridWeights, in scratch);

                    if (error < bestError)
                    {
                        bestError = error;
                        bestGridWidth = gridWidth;
                        bestGridHeight = gridHeight;
                        bestWeightRange = weightRange;
                        bestColorRange = colorRange;
                        candidateColorValues.CopyTo(bestColorValues);
                        candidateGridWeights.CopyTo(bestGridWeights);
                    }
                }
            }
        }

        if (bestWeightRange == 0)
        {
            throw new InvalidOperationException(
                $"No legal single-partition encoding fits footprint {footprint.Width}x{footprint.Height}.");
        }

        int bestGridCount = bestGridWidth * bestGridHeight;
        return Assemble(bestGridWidth, bestGridHeight, bestWeightRange, bestColorRange, bestColorValues, bestGridWeights[..bestGridCount]);
    }

    /// <summary>
    /// Reusable per-configuration scratch buffers for <see cref="EvaluateConfig"/>
    /// </summary>
    private readonly ref struct ConfigScratch(
        Span<int> effectiveLow,
        Span<int> effectiveHigh,
        Span<int> idealWeights,
        Span<double> fittedGrid,
        Span<int> effectiveGrid,
        Span<int> perTexelWeights)
    {
        public Span<int> EffectiveLow { get; } = effectiveLow;
        public Span<int> EffectiveHigh { get; } = effectiveHigh;
        public Span<int> IdealWeights { get; } = idealWeights;
        public Span<double> FittedGrid { get; } = fittedGrid;
        public Span<int> EffectiveGrid { get; } = effectiveGrid;
        public Span<int> PerTexelWeights { get; } = perTexelWeights;
    }

    /// <summary>
    /// Evaluates one (grid, weight-range, colour-range) configuration: quantises endpoints,
    /// projects the ideal per-texel weights, fits grid weights to them (the decimation inverse,
    /// spec §C.2.18), quantises those, then reconstructs through the decoder's actual infill and
    /// interpolation to measure the true reconstruction error. Fills <paramref name="colorValues"/>
    /// and <paramref name="quantGridWeights"/> (first <paramref name="gridWeightCount"/> entries).
    /// </summary>
    private static long EvaluateConfig(
        ReadOnlySpan<RgbaColor> texels,
        Footprint footprint,
        RgbaColor low,
        RgbaColor high,
        int gridWidth,
        int gridHeight,
        int gridWeightCount,
        int weightRange,
        int colorRange,
        Span<int> colorValues,
        Span<int> quantGridWeights,
        in ConfigScratch scratch)
    {
        Span<int> effectiveLow = scratch.EffectiveLow;
        Span<int> effectiveHigh = scratch.EffectiveHigh;
        QuantizeEndpoints(low, high, colorRange, colorValues, effectiveLow, effectiveHigh);

        int texelCount = footprint.PixelCount;
        Span<int> idealWeights = scratch.IdealWeights;
        for (int t = 0; t < texelCount; t++)
        {
            idealWeights[t] = ProjectWeight(texels[t], effectiveLow, effectiveHigh);
        }

        DecimationInfo decimation = DecimationTable.Get(footprint, gridWidth, gridHeight);

        // Fit continuous grid weights to the ideal texel weights, then quantise and round-trip each
        // through the weight range to get the values the decoder will actually interpolate with.
        Span<double> fittedGrid = scratch.FittedGrid[..gridWeightCount];
        DecimationFit.Fit(idealWeights, decimation, gridWeightCount, fittedGrid);

        Span<int> effectiveGrid = scratch.EffectiveGrid[..gridWeightCount];
        for (int p = 0; p < gridWeightCount; p++)
        {
            int quant = Quantization.QuantizeWeightToRange((int)Math.Round(fittedGrid[p]), weightRange);
            quantGridWeights[p] = quant;
            effectiveGrid[p] = Quantization.UnquantizeWeightFromRange(quant, weightRange);
        }

        // Infill the effective grid weights back to per-texel weights exactly as the decoder does.
        Span<int> perTexelWeights = scratch.PerTexelWeights[..texelCount];
        DecimationTable.InfillWeights(effectiveGrid, decimation, perTexelWeights);

        long error = 0;
        for (int t = 0; t < texelCount; t++)
        {
            error += ReconstructionError(texels[t], effectiveLow, effectiveHigh, perTexelWeights[t]);
        }

        return error;
    }

    /// <summary>
    /// Stores RGBA-direct colour values for the bounding box: interleaved (low, high) per channel
    /// (spec §C.2.14 mode 12). Because each low channel is &lt;= its high channel and quantisation
    /// is monotonic, the decoder's "blue contract" swap (triggered when the high triple is dimmer)
    /// never fires, so this is the exact inverse of the decode path.
    /// </summary>
    private static void QuantizeEndpoints(
        RgbaColor boxLow,
        RgbaColor boxHigh,
        int colorRange,
        Span<int> colorValues,
        Span<int> effectiveLow,
        Span<int> effectiveHigh)
    {
        ReadOnlySpan<byte> low = [boxLow.R, boxLow.G, boxLow.B, boxLow.A];
        ReadOnlySpan<byte> high = [boxHigh.R, boxHigh.G, boxHigh.B, boxHigh.A];

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int quantLow = Quantization.QuantizeCEValueToRange(low[channel], colorRange);
            int quantHigh = Quantization.QuantizeCEValueToRange(high[channel], colorRange);
            colorValues[channel * 2] = quantLow;
            colorValues[(channel * 2) + 1] = quantHigh;
            effectiveLow[channel] = Quantization.UnquantizeCEValueFromRange(quantLow, colorRange);
            effectiveHigh[channel] = Quantization.UnquantizeCEValueFromRange(quantHigh, colorRange);
        }
    }

    /// <summary>
    /// Projects a texel onto the endpoint line and returns the nearest weight in [0, 64]
    /// (spec §C.2.19). Degenerate (low == high) endpoints map to weight 0.
    /// </summary>
    private static int ProjectWeight(RgbaColor texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high)
    {
        ReadOnlySpan<int> pixel = [texel.R, texel.G, texel.B, texel.A];

        long dirDotDir = 0;
        long pixelDotDir = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int direction = high[channel] - low[channel];
            dirDotDir += (long)direction * direction;
            pixelDotDir += (long)(pixel[channel] - low[channel]) * direction;
        }

        if (dirDotDir == 0)
        {
            return 0;
        }

        long weight = ((pixelDotDir * MaxWeight) + (dirDotDir / 2)) / dirDotDir;
        return (int)Math.Clamp(weight, 0, MaxWeight);
    }

    /// <summary>
    /// Sum-of-squared error between a texel and its reconstruction using the decoder's LDR
    /// interpolation (spec §C.2.19) at the given weight.
    /// </summary>
    private static long ReconstructionError(RgbaColor texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int weight)
    {
        ReadOnlySpan<int> pixel = [texel.R, texel.G, texel.B, texel.A];
        long error = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int reconstructed = (Interpolation.BlendLdrReplicated(low[channel], high[channel], weight) >> 8) & 0xFF;
            int diff = reconstructed - pixel[channel];
            error += (long)diff * diff;
        }

        return error;
    }

    /// <summary>
    /// Fits the endpoint pair to the texel cloud by least-squares: the endpoints lie on the
    /// principal axis (the direction of greatest variance) through the mean, extended to span the
    /// texels' projections. This tracks correlated and anti-correlated channel variation that a
    /// per-channel bounding box would mis-orient. Endpoints are clamped to the [0, 255] byte range.
    /// </summary>
    private static (RgbaColor Low, RgbaColor High) FitEndpoints(ReadOnlySpan<RgbaColor> texels)
    {
        Span<double> mean = stackalloc double[ChannelCount];
        foreach (RgbaColor texel in texels)
        {
            mean[0] += texel.R; mean[1] += texel.G; mean[2] += texel.B; mean[3] += texel.A;
        }

        for (int c = 0; c < ChannelCount; c++)
        {
            mean[c] /= texels.Length;
        }

        Span<double> axis = stackalloc double[ChannelCount];
        PrincipalAxis(texels, mean, axis);

        // Project each texel onto the axis; the min/max projections bound the endpoints.
        double minProj = double.MaxValue;
        double maxProj = double.MinValue;
        foreach (RgbaColor texel in texels)
        {
            double projection = ((texel.R - mean[0]) * axis[0])
                + ((texel.G - mean[1]) * axis[1])
                + ((texel.B - mean[2]) * axis[2])
                + ((texel.A - mean[3]) * axis[3]);
            minProj = Math.Min(minProj, projection);
            maxProj = Math.Max(maxProj, projection);
        }

        RgbaColor a = EndpointAt(mean, axis, minProj);
        RgbaColor b = EndpointAt(mean, axis, maxProj);

        // The decoder's RGBA-direct mode applies a "blue contract" swap when the second endpoint's
        // channel sum is the smaller one (spec §C.2.14 mode 12). The principal axis has an arbitrary
        // sign, so order the endpoints by channel sum to keep the high endpoint's sum >= the low's
        // and avoid triggering that branch — making this the exact inverse of the decode path.
        return ChannelSum(a) <= ChannelSum(b) ? (a, b) : (b, a);
    }

    private static int ChannelSum(RgbaColor c) => c.R + c.G + c.B;

    /// <summary>
    /// Estimates the principal axis (unit vector) of the texel cloud's covariance via power
    /// iteration. Falls back to a fixed axis when the cloud has no variance.
    /// </summary>
    private static void PrincipalAxis(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<double> mean, Span<double> axis)
    {
        // Covariance matrix (ChannelCount x ChannelCount, symmetric) of the centred texels. The
        // centred-texel vector and the power-iteration scratch are allocated once and reused; both
        // are fully overwritten before use each iteration, so hoisting them out of the loops avoids
        // per-iteration stack growth.
        Span<double> cov = stackalloc double[ChannelCount * ChannelCount];
        Span<double> d = stackalloc double[ChannelCount];
        foreach (RgbaColor texel in texels)
        {
            d[0] = texel.R - mean[0]; d[1] = texel.G - mean[1]; d[2] = texel.B - mean[2]; d[3] = texel.A - mean[3];
            for (int i = 0; i < ChannelCount; i++)
            {
                for (int j = 0; j < ChannelCount; j++)
                {
                    cov[(i * ChannelCount) + j] += d[i] * d[j];
                }
            }
        }

        // Power iteration from a non-degenerate start vector.
        Span<double> next = stackalloc double[ChannelCount];
        axis[0] = 1; axis[1] = 1; axis[2] = 1; axis[3] = 1;
        for (int iteration = 0; iteration < PrincipalAxisIterations; iteration++)
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                double sum = 0;
                for (int j = 0; j < ChannelCount; j++)
                {
                    sum += cov[(i * ChannelCount) + j] * axis[j];
                }

                next[i] = sum;
            }

            double norm = Math.Sqrt((next[0] * next[0]) + (next[1] * next[1]) + (next[2] * next[2]) + (next[3] * next[3]));
            if (norm < DegenerateAxisNorm)
            {
                // No variance along the current estimate; keep a fixed diagonal axis.
                axis[0] = axis[1] = axis[2] = axis[3] = 0.5;
                return;
            }

            for (int i = 0; i < ChannelCount; i++)
            {
                axis[i] = next[i] / norm;
            }
        }
    }

    private static RgbaColor EndpointAt(ReadOnlySpan<double> mean, ReadOnlySpan<double> axis, double projection)
        => new(
            ClampByte(mean[0] + (axis[0] * projection)),
            ClampByte(mean[1] + (axis[1] * projection)),
            ClampByte(mean[2] + (axis[2] * projection)),
            ClampByte(mean[3] + (axis[3] * projection)));

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, MaxChannel);

    private static UInt128 Assemble(
        int gridWidth,
        int gridHeight,
        int weightRange,
        int colorRange,
        ReadOnlySpan<int> colorValues,
        ReadOnlySpan<int> quantWeights)
    {
        ushort blockMode = BlockModeEncoder.Encode(gridWidth, gridHeight, weightRange, isDualPlane: false);

        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, BlockModeStartBit, BlockModeBits);
        builder.PlaceLowField(PartitionCountField, PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)Mode, CemStartBit, CemBits);

        var colorStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(colorRange, colorValues, ref colorStream);
        builder.PlaceColorData(colorStream, ColorStartBit);

        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(weightRange, quantWeights, ref weightStream);
        builder.PlaceWeightData(weightStream);

        return builder.Build();
    }
}
