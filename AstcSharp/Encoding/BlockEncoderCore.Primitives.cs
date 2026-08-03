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

        // Additive iterative endpoint⇄weight co-refinement, single-partition only. Converges the
        // endpoint/weight assignment toward the endpoint line's joint optimum, recovering the headroom
        // between the one-shot fit and the line's ceiling. Kept only on strict improvement, so it can
        // only lower the error or leave the config unchanged.
        TStrategy strategy = default;
        if (strategy.TryIterativeRefinement && block.PartitionCount == 1)
        {
            long refined = TryIterativeRefinement<TTexel, TStrategy>(
                in block, mode, gridWeightCount, weightRange, colorRange, decimation, baseError: error, in scratch);
            if (refined < error)
            {
                error = refined;
            }

            // Additive endpoint coordinate-descent: perturb the winning quantised colour values by +/- 1,
            // re-decode endpoints, re-fit/quantise weights, keep strict improvements. Catches the small
            // endpoint-field quantisation misses the least-squares recompute leaves on the table.
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
    /// Single-partition endpoint coordinate-descent: sweeps each quantised endpoint colour value in
    /// <see cref="ConfigScratch.CandidateColorValues"/> by ±<see cref="EndpointRefineRadius"/>,
    /// re-decoding the endpoints, re-projecting/fitting/quantising the weight grid, and re-measuring
    /// through the real path; keeps any move that lowers the block error. Updates the candidate colour
    /// values and grid weights in place on improvement and returns the new error, else returns
    /// <paramref name="currentError"/> unchanged. Purely additive — it can only lower error.
    /// </summary>
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
        int valueCount = mode.GetColorValuesCount();
        Span<int> values = scratch.CandidateColorValues[..valueCount];
        Span<int> bestGrid = scratch.CandidateGridWeights[..gridWeightCount];

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
                    long err = MeasureColorValues<TTexel, TStrategy>(
                        in block, mode, gridWeightCount, weightRange, colorRange, decimation, values,
                        scratch.AltGridWeights[..gridWeightCount], in scratch);
                    if (err < currentError)
                    {
                        currentError = err;
                        original = trial;
                        improved = true;
                        scratch.AltGridWeights[..gridWeightCount].CopyTo(bestGrid);
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

        return currentError;
    }

    /// <summary>
    /// Single-partition reconstruction error for a specific set of quantised colour
    /// <paramref name="values"/>: decodes the endpoints, projects/fits/quantises the weight grid (into
    /// <paramref name="quantGrid"/>), and measures through the real infill and interpolation. Used by
    /// the endpoint coordinate-descent to score a perturbed colour value.
    /// </summary>
    private static long MeasureColorValues<TTexel, TStrategy>(
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
        TStrategy strategy = default;
        int valueCount = values.Length;
        Span<int> unquantized = scratch.UnquantizedColors[..valueCount];
        values.CopyTo(unquantized);
        Quantization.UnquantizeCEValuesBatch(unquantized, colorRange);
        ColorEndpointPair pair = EndpointCodec.Decode(unquantized, mode);

        Span<int> effLow = scratch.AltEffectiveLow[..ChannelCount];
        Span<int> effHigh = scratch.AltEffectiveHigh[..ChannelCount];
        strategy.StoreEffectiveChannels(in pair, effLow, effHigh);

        ReadOnlySpan<TTexel> texels = block.Texels;
        Span<int> idealWeights = scratch.AltIdealWeights[..texels.Length];
        for (int t = 0; t < texels.Length; t++)
        {
            idealWeights[t] = ProjectWeight<TTexel, TStrategy>(texels[t], effLow, effHigh);
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
            error += ReconstructionError<TTexel, TStrategy>(texels[t], effLow, effHigh, perTexel[t]);
        }

        return error;
    }

    // The iterative refiner stops early once a pass fails to lower error by at least this fraction of
    // the previous error, and after at most MaxRefinementPasses passes — a joint-optimum fixed point.
    private const int MaxRefinementPasses = 8;
    private const double RefinementMinRelativeGain = 0.001;

    /// <summary>
    /// Iterative endpoint⇄weight co-refinement for a single-partition config.
    /// Starting from the prepared endpoints/weights, repeatedly:
    /// (1) Solve the least-squares endpoints at the current quantised per-texel weights, re-encode/decode
    /// them through the real codec
    /// (2) Re-project the texels onto the new endpoint line, re-fit and re-quantise the grid.
    /// Each pass is measured through the actual decode path, the loop keeps the best pass and stops when a pass
    /// stops improving. On a strict improvement over <paramref name="baseError"/> it overwrites the
    /// candidate colour values / grid weights and returns the new error, otherwise returns
    /// <see cref="long.MaxValue"/> and leaves the candidate untouched (purely additive).
    /// </summary>
    private static long TryIterativeRefinement<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int gridWeightCount,
        int weightRange,
        int colorRange,
        DecimationInfo decimation,
        long baseError,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        ReadOnlySpan<TTexel> texels = block.Texels;
        int texelCount = texels.Length;
        int valueCount = mode.GetColorValuesCount();

        // Working buffers: current per-texel weights (seeded from the standard candidate just measured),
        // the refined endpoint colour values, and the decoded endpoint channels.
        Span<int> perTexelWeights = scratch.AltPerTexelWeights[..texelCount];
        DecimationTable.InfillWeights(scratch.EffectiveGrid[..gridWeightCount], decimation, perTexelWeights);

        Span<int> refinedValues = scratch.AltColorValues[..valueCount];
        Span<int> refinedLow = scratch.AltEffectiveLow[..ChannelCount];
        Span<int> refinedHigh = scratch.AltEffectiveHigh[..ChannelCount];

        long bestError = baseError;
        bool improved = false;
        double previousError = baseError;

        for (int pass = 0; pass < MaxRefinementPasses; pass++)
        {
            // (1) Solve endpoints at the current weights, encode/decode through the real codec.
            if (!SolveEndpointsAtWeights<TTexel, TStrategy>(texels, perTexelWeights, out TTexel low, out TTexel high))
            {
                break;
            }

            EncodeAndDecodeEndpoints<TTexel, TStrategy>(
                mode, low, high, colorRange, refinedValues, scratch.UnquantizedColors, refinedLow, refinedHigh);

            // (2) Re-project texels onto the new line, re-fit and re-quantise the grid.
            Span<int> idealWeights = scratch.AltIdealWeights[..texelCount];
            for (int t = 0; t < texelCount; t++)
            {
                idealWeights[t] = ProjectWeight<TTexel, TStrategy>(texels[t], refinedLow, refinedHigh);
            }

            Span<double> fittedGrid = scratch.AltFittedGrid[..gridWeightCount];
            DecimationFit.Fit(idealWeights, decimation, gridWeightCount, fittedGrid);

            Span<int> quantGrid = scratch.AltGridWeights[..gridWeightCount];
            Span<int> effectiveGrid = scratch.AltEffectiveGrid[..gridWeightCount];
            QuantizeGridToEffective(fittedGrid, weightRange, quantGrid, effectiveGrid);
            DecimationTable.InfillWeights(effectiveGrid, decimation, perTexelWeights);

            long error = 0;
            for (int t = 0; t < texelCount; t++)
            {
                error += ReconstructionError<TTexel, TStrategy>(texels[t], refinedLow, refinedHigh, perTexelWeights[t]);
            }

            if (error < bestError)
            {
                bestError = error;
                improved = true;
                refinedValues.CopyTo(scratch.CandidateColorValues[..valueCount]);
                quantGrid.CopyTo(scratch.CandidateGridWeights[..gridWeightCount]);
            }

            // Stop once a pass no longer meaningfully lowers error (converged or oscillating).
            if (error >= previousError - (previousError * RefinementMinRelativeGain))
            {
                break;
            }

            previousError = error;
        }

        return improved ? bestError : long.MaxValue;
    }

    /// <summary>
    /// Solves for the endpoint pair minimising squared reconstruction error at fixed per-texel weights
    /// (the analytic least squares behind ARM's <c>recompute_ideal_colors</c>). Per channel this is a
    /// 2×2 normal-equation system in the endpoint pair, the shared 2×2 matrix (built from the weights)
    /// is inverted once and applied per channel. Returns false if the system is singular (all weights
    /// equal), leaving the caller to skip refinement.
    /// </summary>
    private static bool SolveEndpointsAtWeights<TTexel, TStrategy>(
        ReadOnlySpan<TTexel> texels, ReadOnlySpan<int> perTexelWeights, out TTexel low, out TTexel high)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        low = default;
        high = default;

        // Normal-equation matrix entries: a = Σ(1-w)², b = Σ(1-w)w, d = Σw² (w in [0,1]).
        double a = 0, b = 0, d = 0;
        Span<double> rhsLow = stackalloc double[ChannelCount];
        Span<double> rhsHigh = stackalloc double[ChannelCount];
        rhsLow.Clear();
        rhsHigh.Clear();

        for (int t = 0; t < texels.Length; t++)
        {
            double w = perTexelWeights[t] / (double)MaxWeight;
            double om = 1.0 - w;
            a += om * om;
            b += om * w;
            d += w * w;
            for (int c = 0; c < ChannelCount; c++)
            {
                double value = strategy.GetChannel(texels[t], c);
                rhsLow[c] += om * value;
                rhsHigh[c] += w * value;
            }
        }

        double det = (a * d) - (b * b);
        if (Math.Abs(det) < 1e-9)
        {
            return false;
        }

        double invDet = 1.0 / det;
        Span<double> lowChannels = stackalloc double[ChannelCount];
        Span<double> highChannels = stackalloc double[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            // Inverse of [[a,b],[b,d]] applied to (rhsLow, rhsHigh).
            lowChannels[c] = ((d * rhsLow[c]) - (b * rhsHigh[c])) * invDet;
            highChannels[c] = ((a * rhsHigh[c]) - (b * rhsLow[c])) * invDet;
        }

        low = strategy.EndpointFromChannels(lowChannels);
        high = strategy.EndpointFromChannels(highChannels);
        return true;
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
