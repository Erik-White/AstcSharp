using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// The shared low-level operations <see cref="BlockEncoderCore"/>'s searches build on: colour-range
/// budget resolution, endpoint encode/decode, texel-onto-line projection, reconstruction error, and
/// weight-grid quantisation. Each is used by both the single-plane and dual-plane paths.
/// </summary>
internal static partial class BlockEncoderCore
{

    // Endpoint coordinate-descent radius and sweep cap. Small: it refines the already-fitted endpoint
    // values, and per-value trials each re-fit the whole grid.
    private const int EndpointRefineRadius = 1;
    private const int MaxEndpointRefineSweeps = 2;

    /// <summary>
    /// Returns the smallest per-partition colour-value count among <paramref name="modes"/> — the
    /// cheapest shared mode the multi-partition search could pick, used to decide whether a partition
    /// count can fit the colour-value budget at all.
    /// </summary>
    private static int MinColorValuesPerPartition(ReadOnlySpan<ColorEndpointMode> modes)
    {
        int min = int.MaxValue;
        foreach (ColorEndpointMode mode in modes)
        {
            min = Math.Min(min, mode.GetColorValuesCount());
        }

        return min;
    }

    /// <summary>
    /// Computes the parts of a configuration that depend only on the colour range (not the weight
    /// range): encodes each partition's endpoints (decoding them back through the real codec into
    /// <see cref="ConfigScratch.EffectiveLow"/>/<see cref="ConfigScratch.EffectiveHigh"/>), projects
    /// each texel's ideal weight onto its partition's endpoint line, and fits the continuous grid
    /// weights (the decimation inverse, spec §C.2.18) into <see cref="ConfigScratch.FittedGrid"/>.
    /// The weight-range loop reuses this across every range that resolves to the same colour range.
    /// </summary>
    private static void PrepareConfig<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int gridWeightCount,
        DecimationInfo decimation,
        int colorRange,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        int valuesPerPartition = mode.GetColorValuesCount();
        Span<int> effectiveLow = scratch.EffectiveLow;
        Span<int> effectiveHigh = scratch.EffectiveHigh;
        for (int p = 0; p < block.PartitionCount; p++)
        {
            EncodeAndDecodeEndpoints<TTexel, TStrategy>(
                mode, block.SubsetLow[p], block.SubsetHigh[p], colorRange,
                scratch.CandidateColorValues.Slice(p * valuesPerPartition, valuesPerPartition),
                scratch.UnquantizedColors,
                effectiveLow.Slice(p * ChannelCount, ChannelCount),
                effectiveHigh.Slice(p * ChannelCount, ChannelCount));
        }

        ReadOnlySpan<TTexel> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        Span<int> idealWeights = scratch.IdealWeights;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            idealWeights[t] = ProjectWeight<TTexel, TStrategy>(
                texels[t], effectiveLow.Slice(p * ChannelCount, ChannelCount), effectiveHigh.Slice(p * ChannelCount, ChannelCount));
        }

        DecimationFit.Fit(idealWeights[..texels.Length], decimation, gridWeightCount, scratch.FittedGrid[..gridWeightCount]);
    }

    /// <summary>
    /// Completes a configuration for one weight range using the prepared endpoints and fitted grid
    /// (from <see cref="PrepareConfig"/>): quantises the grid weights to the range (into
    /// <see cref="ConfigScratch.CandidateGridWeights"/>), reconstructs through the decoder's actual
    /// infill and interpolation, and returns the sum-of-squared error.
    /// </summary>
    private static long MeasureConfig<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int gridWeightCount,
        int weightRange,
        int colorRange,
        DecimationInfo decimation,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        Span<int> effectiveGrid = scratch.EffectiveGrid[..gridWeightCount];
        QuantizeGridToEffective(
            scratch.FittedGrid[..gridWeightCount], weightRange, scratch.CandidateGridWeights[..gridWeightCount], effectiveGrid);

        // Infill the effective grid weights back to per-texel weights exactly as the decoder does.
        ReadOnlySpan<TTexel> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        Span<int> perTexelWeights = scratch.PerTexelWeights[..texels.Length];
        DecimationTable.InfillWeights(effectiveGrid, decimation, perTexelWeights);

        Span<int> effectiveLow = scratch.EffectiveLow;
        Span<int> effectiveHigh = scratch.EffectiveHigh;
        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            error += ReconstructionError<TTexel, TStrategy>(
                texels[t], effectiveLow.Slice(p * ChannelCount, ChannelCount), effectiveHigh.Slice(p * ChannelCount, ChannelCount), perTexelWeights[t]);
        }

        TStrategy strategy = default;
        if (strategy.TryEndpointRefinement)
        {
            // Additive endpoint coordinate-descent (any partition count): perturb the winning quantised
            // colour values by +/- 1, re-decode endpoints, re-fit/quantise weights, keep strict improvements.
            long refinedValues = RefineEndpointValues<TTexel, TStrategy>(
                in block, mode, gridWeightCount, weightRange, colorRange, decimation, currentError: error, in scratch);
            if (refinedValues < error)
            {
                error = refinedValues;
            }
        }

        return error;
    }

    /// <summary>
    /// Endpoint coordinate-descent: sweeps each quantised endpoint colour value in
    /// <see cref="ConfigScratch.CandidateColorValues"/> by ±<see cref="EndpointRefineRadius"/>, keeping
    /// moves that lower the block error. Handles any partition count, the colour values are the
    /// per-partition endpoints concatenated (all partitions share <paramref name="mode"/>).
    /// </summary>
    /// <remarks>
    /// To bound cost, the descent scores each trial against the <em>fixed</em> per-texel weights of the
    /// incoming config. This is a proxy: the best endpoints at fixed weights may not be best after the weights re-adapt.
    /// After the descent settles a single real re-fit (project -> fit -> quantise -> measure) decides the kept result,
    /// and it is applied only on a strict improvement over <paramref name="currentError"/>. Purely additive, it can only
    /// lower error, and the emitted grid always matches the emitted endpoints.
    /// </remarks>
    private static long RefineEndpointValues<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int gridWeightCount,
        int weightRange,
        int colorRange,
        DecimationInfo decimation,
        long currentError,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        int valueCount = mode.GetColorValuesCount() * block.PartitionCount;
        Span<int> values = scratch.CandidateColorValues[..valueCount];
        ReadOnlySpan<TTexel> texels = block.Texels;

        // Snapshot the incoming config so a proxy-improving-but-real-worsening descent can be rolled
        // back — the caller copies Candidate* to Best* by error, so these must stay consistent.
        Span<int> savedValues = scratch.AltColorValues[..valueCount];
        Span<int> savedGrid = scratch.AltGridWeights[..gridWeightCount];
        values.CopyTo(savedValues);
        scratch.CandidateGridWeights[..gridWeightCount].CopyTo(savedGrid);

        // Fixed per-texel weights of the incoming winning config — the descent scores trials against
        // these without re-fitting the grid.
        Span<int> fixedWeights = scratch.AltPerTexelWeights[..texels.Length];
        DecimationTable.InfillWeights(scratch.EffectiveGrid[..gridWeightCount], decimation, fixedWeights);

        // Descend on the fixed-weight proxy error (seeded from the same proxy at the current values, so
        // "improvement" is measured consistently in the proxy metric).
        long proxyBest = ProxyErrorAtValues<TTexel, TStrategy>(in block, mode, colorRange, values, fixedWeights, in scratch);
        bool anyMove = false;
        for (int sweep = 0; sweep < MaxEndpointRefineSweeps; sweep++)
        {
            bool improved = false;
            for (int v = 0; v < valueCount; v++)
            {
                int original = values[v];
                for (int delta = -EndpointRefineRadius; delta <= EndpointRefineRadius; delta++)
                {
                    if (delta == 0)
                    {
                        continue;
                    }

                    int trial = original + delta;
                    if (trial < 0 || trial > colorRange)
                    {
                        continue;
                    }

                    values[v] = trial;
                    long proxy = ProxyErrorAtValues<TTexel, TStrategy>(in block, mode, colorRange, values, fixedWeights, in scratch);
                    if (proxy < proxyBest)
                    {
                        proxyBest = proxy;
                        original = trial;
                        improved = true;
                        anyMove = true;
                    }
                    else
                    {
                        values[v] = original;
                    }
                }
            }

            if (!improved)
            {
                break;
            }
        }

        if (!anyMove)
        {
            return currentError;
        }

        // The descent moved the endpoints (on the fixed-weight proxy); re-fit the grid once for the new
        // endpoints and take the real error. Keep the moved config only on a strict improvement,
        // otherwise restore the snapshot so Candidate* stays the incoming winner (the proxy gain did not
        // survive the weight re-fit).
        long refitted = MeasureColorValuesRefit<TTexel, TStrategy>(
            in block,
            mode,
            gridWeightCount,
            weightRange,
            colorRange,
            decimation,
            values,
            scratch.CandidateGridWeights[..gridWeightCount],
            in scratch);
        if (refitted < currentError)
        {
            return refitted;
        }

        savedValues.CopyTo(values);
        savedGrid.CopyTo(scratch.CandidateGridWeights[..gridWeightCount]);
        return currentError;
    }

    /// <summary>
    /// Fixed weight proxy error for a set of quantised colour <paramref name="values"/>: decodes each
    /// partition's endpoints and sums the reconstruction error against the caller-supplied
    /// <paramref name="fixedWeights"/> (no grid re-fit). O(texels) per call — the cheap inner step of
    /// the endpoint coordinate-descent.
    /// </summary>
    private static long ProxyErrorAtValues<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int colorRange,
        ReadOnlySpan<int> values,
        ReadOnlySpan<int> fixedWeights,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        DecodeEndpointsPerPartition<TTexel, TStrategy>(in block, mode, colorRange, values, scratch);

        Span<int> effLow = scratch.AltEffectiveLow;
        Span<int> effHigh = scratch.AltEffectiveHigh;
        ReadOnlySpan<TTexel> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        long error = 0;

        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            error += ReconstructionError<TTexel, TStrategy>(
                texels[t], effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount), fixedWeights[t]);
        }

        return error;
    }

    /// <summary>
    /// Real reconstruction error for a set of quantised colour <paramref name="values"/>: decodes the
    /// per-partition endpoints, projects each texel onto its partition's line, fits/quantises the shared
    /// weight grid (into <paramref name="quantGrid"/>), and measures through the real infill and
    /// interpolation. The full (grid-refitting) measure the endpoint descent applies once at the end.
    /// </summary>
    private static long MeasureColorValuesRefit<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int gridWeightCount,
        int weightRange,
        int colorRange,
        DecimationInfo decimation,
        ReadOnlySpan<int> values,
        Span<int> quantGrid,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        DecodeEndpointsPerPartition<TTexel, TStrategy>(in block, mode, colorRange, values, scratch);

        Span<int> effLow = scratch.AltEffectiveLow;
        Span<int> effHigh = scratch.AltEffectiveHigh;
        ReadOnlySpan<TTexel> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        Span<int> idealWeights = scratch.AltIdealWeights[..texels.Length];
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            idealWeights[t] = ProjectWeight<TTexel, TStrategy>(
                texels[t], effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount));
        }

        Span<double> fittedGrid = scratch.AltFittedGrid[..gridWeightCount];
        DecimationFit.Fit(idealWeights, decimation, gridWeightCount, fittedGrid);
        Span<int> effectiveGrid = scratch.AltEffectiveGrid[..gridWeightCount];
        QuantizeGridToEffective(fittedGrid, weightRange, quantGrid, effectiveGrid);

        Span<int> perTexel = scratch.AltPerTexelWeights[..texels.Length];
        DecimationTable.InfillWeights(effectiveGrid, decimation, perTexel);

        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            error += ReconstructionError<TTexel, TStrategy>(
                texels[t], effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount), perTexel[t]);
        }

        return error;
    }

    /// <summary>
    /// Decodes each partition's endpoints from its slice of the concatenated colour
    /// <paramref name="values"/> into <see cref="ConfigScratch.AltEffectiveLow"/>/<c>AltEffectiveHigh</c>.
    /// </summary>
    private static void DecodeEndpointsPerPartition<TTexel, TStrategy>(
        in BlockInput<TTexel> block, ColorEndpointMode mode, int colorRange, ReadOnlySpan<int> values, in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        int valuesPerPartition = mode.GetColorValuesCount();
        Span<int> effLow = scratch.AltEffectiveLow;
        Span<int> effHigh = scratch.AltEffectiveHigh;
        for (int p = 0; p < block.PartitionCount; p++)
        {
            Span<int> unquantized = scratch.UnquantizedColors[..valuesPerPartition];
            values.Slice(p * valuesPerPartition, valuesPerPartition).CopyTo(unquantized);
            Quantization.UnquantizeCEValuesBatch(unquantized, colorRange);
            ColorEndpointPair pair = EndpointCodec.Decode(unquantized, mode);
            strategy.StoreEffectiveChannels(in pair, effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount));
        }
    }

    /// <summary>
    /// Validates a (grid, weight-range) candidate and resolves the colour range it leaves room for.
    /// Returns false — skip this candidate — if the block mode is illegal, the weight bit count is
    /// out of the [24, 96] window (spec §C.2.11), or the colour values do not fit the remaining bit
    /// budget (spec §C.2.22).
    /// </summary>
    private static bool TryResolveConfig(
        int gridWidth, int gridHeight, int gridWeightCount, int weightRange, int colorStartBit, int colorValueCount, out int colorRange)
    {
        colorRange = 0;
        if (!BlockModeEncoder.TryEncode(gridWidth, gridHeight, weightRange, isDualPlane: false, out _))
        {
            return false;
        }

        int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(gridWeightCount, weightRange);
        if (weightBitCount is < MinWeightBits or > MaxWeightBits)
        {
            return false;
        }

        int maxColorBits = BlockBits - weightBitCount - colorStartBit;
        return BlockModeDecoder.TryResolveColorEncoding(colorValueCount, maxColorBits, out colorRange, out _);
    }

    /// <summary>
    /// Encodes the endpoint pair for <paramref name="mode"/> into <paramref name="colorValues"/>,
    /// then decodes those values back through the real <see cref="EndpointCodec"/> to recover the
    /// effective endpoints the decoder will interpolate. Routing the measurement through the actual
    /// decode path means any imperfection in an endpoint encoding only shows up as higher error
    /// (the mode loses the search) and can never produce an illegal block.
    /// </summary>
    private static void EncodeAndDecodeEndpoints<TTexel, TStrategy>(
        ColorEndpointMode mode,
        TTexel low,
        TTexel high,
        int colorRange,
        Span<int> colorValues,
        Span<int> unquantizedScratch,
        Span<int> effectiveLow,
        Span<int> effectiveHigh)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        int colorValueCount = mode.GetColorValuesCount();
        Span<int> values = colorValues[..colorValueCount];
        strategy.EncodeEndpoints(mode, low, high, colorRange, values);

        // Unquantise the stored colour values and decode the endpoint pair exactly as the decoder
        // does (its decode operates on unquantised values).
        Span<int> unquantizedSlice = unquantizedScratch[..colorValueCount];
        values.CopyTo(unquantizedSlice);
        Quantization.UnquantizeCEValuesBatch(unquantizedSlice, colorRange);

        ColorEndpointPair pair = EndpointCodec.Decode(unquantizedSlice, mode);
        strategy.StoreEffectiveChannels(in pair, effectiveLow, effectiveHigh);
    }

    /// <summary>
    /// Projects a texel onto the endpoint line over all channels and returns the nearest weight in
    /// [0, 64] (spec §C.2.19). Degenerate (low == high) endpoints map to weight 0.
    /// </summary>
    private static int ProjectWeight<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
        => ProjectWeightMasked<TTexel, TStrategy>(texel, low, high, AllChannelsMask);

    /// <summary>
    /// Projects a texel onto the endpoint line using only the channels selected by
    /// <paramref name="channelMask"/> (bit <c>c</c> set = include channel <c>c</c>), returning the
    /// nearest weight in [0, 64]. Dual-plane fitting uses this to weight the two planes from disjoint
    /// channel sets; whole-line projection passes <see cref="AllChannelsMask"/>.
    /// </summary>
    private static int ProjectWeightMasked<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int channelMask)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        long dirDotDir = 0;
        long pixelDotDir = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if ((channelMask & (1 << channel)) == 0)
            {
                continue;
            }

            int direction = high[channel] - low[channel];
            dirDotDir += (long)direction * direction;
            pixelDotDir += (long)(strategy.GetChannel(texel, channel) - low[channel]) * direction;
        }

        if (dirDotDir == 0)
        {
            return 0;
        }

        long weight = ((pixelDotDir * MaxWeight) + (dirDotDir / 2)) / dirDotDir;
        return (int)Math.Clamp(weight, 0, MaxWeight);
    }

    /// <summary>
    /// Sum-of-squared error between a texel and its reconstruction using the decoder's interpolation
    /// (spec §C.2.19) at the given weight.
    /// </summary>
    private static long ReconstructionError<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int weight)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
        => ReconstructionErrorDualPlane<TTexel, TStrategy>(texel, low, high, weight, dualPlaneChannel: -1, secondaryWeight: 0);

    /// <summary>
    /// Sum-of-squared error for a dual-plane texel: the channel named by
    /// <paramref name="dualPlaneChannel"/> interpolates with <paramref name="secondaryWeight"/>, all
    /// others with <paramref name="weight"/> — mirroring the decoder's dual-plane blend
    /// (spec §C.2.20). A <paramref name="dualPlaneChannel"/> of -1 makes this the single-plane case.
    /// </summary>
    private static long ReconstructionErrorDualPlane<TTexel, TStrategy>(
        TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int weight, int dualPlaneChannel, int secondaryWeight)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        long error = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            int channelWeight = channel == dualPlaneChannel ? secondaryWeight : weight;
            int reconstructed = strategy.Reconstruct(low[channel], high[channel], channelWeight);
            int diff = reconstructed - strategy.GetChannel(texel, channel);
            error += (long)diff * diff;
        }

        return error;
    }

    /// <summary>
    /// Rounds a fitted grid weight to the nearest integer, rounding halves away from zero to match
    /// the decoder's round-half-up infill convention (spec §C.2.18, <c>(… + 8) >> 4</c>). The
    /// default <see cref="Math.Round(double)"/> rounds halves to even, which would bias half-valued
    /// weights inconsistently against the decoder.
    /// </summary>
    private static int RoundWeight(double weight) => (int)Math.Round(weight, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Quantises a fitted grid to the weight range, writing both the stored quantised weights (into
    /// <paramref name="quantGridWeights"/>, for the bitstream) and the decoder's effective weights
    /// (into <paramref name="effectiveGrid"/>, for reconstruction) in one pass.
    /// </summary>
    private static void QuantizeGridToEffective(
        ReadOnlySpan<double> fittedGrid, int weightRange, Span<int> quantGridWeights, Span<int> effectiveGrid)
    {
        for (int i = 0; i < fittedGrid.Length; i++)
        {
            int quant = Quantization.QuantizeWeightToRange(RoundWeight(fittedGrid[i]), weightRange);
            quantGridWeights[i] = quant;
            effectiveGrid[i] = Quantization.UnquantizeWeightFromRange(quant, weightRange);
        }
    }
}
