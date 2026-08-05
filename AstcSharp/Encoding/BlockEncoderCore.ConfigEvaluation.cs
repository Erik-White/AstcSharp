using AstcSharp.BiseEncoding;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// Scores one candidate configuration for <see cref="BlockEncoderCore"/>'s searches: resolves the
/// colour range a (grid, weight-range) leaves room for, prepares the colour-range-dependent parts
/// (endpoints + fitted grid), and measures the real decoded error for a weight range. The three
/// search variants (single-partition, multi-partition, dual-plane) all drive these.
/// </summary>
internal static partial class BlockEncoderCore
{
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
            ColorGeometry.EncodeAndDecodeEndpoints<TTexel, TStrategy>(
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
            idealWeights[t] = ColorGeometry.ProjectWeight<TTexel, TStrategy>(
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
        ColorGeometry.QuantizeGridToEffective(
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
            error += ColorGeometry.ReconstructionError<TTexel, TStrategy>(
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
}
