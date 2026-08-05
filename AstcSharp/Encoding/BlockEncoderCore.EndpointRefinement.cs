using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// The additive endpoint coordinate-descent for <see cref="BlockEncoderCore"/>. After a
/// configuration is measured, perturb its quantised endpoint colour values by +/- 1 to find a better
/// reconstruction. Descent scores trials against fixed per-texel weights (a cheap proxy), then a
/// single real grid re-fit decides the kept result — applied only on a strict improvement, so it can
/// never worsen a block.
/// </summary>
internal static partial class BlockEncoderCore
{
    // Endpoint coordinate-descent radius and sweep cap. Small: it refines the already-fitted endpoint
    // values, and per-value trials each re-fit the whole grid.
    private const int EndpointRefineRadius = 1;
    private const int MaxEndpointRefineSweeps = 2;

    /// <summary>
    /// Endpoint coordinate-descent: sweeps each quantised endpoint colour value in
    /// <see cref="ConfigScratch.CandidateColorValues"/> by +/- <see cref="EndpointRefineRadius"/>, keeping
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

        // The descent moved the endpoints (on the fixed-weight proxy), re-fit the grid once for the new
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
    /// Fixed weight proxy error for a set of quantised colour <paramref name="values"/>. Decodes each
    /// partition's endpoints and sums the reconstruction error against the caller-supplied
    /// <paramref name="fixedWeights"/> (no grid re-fit). O(texels) per call, the cheap inner step of
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
            error += ColorGeometry.ReconstructionError<TTexel, TStrategy>(
                texels[t], effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount), fixedWeights[t]);
        }

        return error;
    }

    /// <summary>
    /// Real reconstruction error for a set of quantised colour <paramref name="values"/>. Decodes the
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
            idealWeights[t] = ColorGeometry.ProjectWeight<TTexel, TStrategy>(
                texels[t], effLow.Slice(p * ChannelCount, ChannelCount), effHigh.Slice(p * ChannelCount, ChannelCount));
        }

        Span<double> fittedGrid = scratch.AltFittedGrid[..gridWeightCount];
        DecimationFit.Fit(idealWeights, decimation, gridWeightCount, fittedGrid);
        Span<int> effectiveGrid = scratch.AltEffectiveGrid[..gridWeightCount];
        ColorGeometry.QuantizeGridToEffective(fittedGrid, weightRange, quantGrid, effectiveGrid);

        Span<int> perTexel = scratch.AltPerTexelWeights[..texels.Length];
        DecimationTable.InfillWeights(effectiveGrid, decimation, perTexel);

        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            error += ColorGeometry.ReconstructionError<TTexel, TStrategy>(
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
}
