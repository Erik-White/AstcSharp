using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// The multi-partition search of <see cref="BlockEncoderCore"/> (spec §C.2.21): for each partition
/// count it scores all 1024 partition seeds by a cheap endpoint-fit proxy, then runs the full
/// weight-grid / range search only on the best few seeds.
/// </summary>
internal static partial class BlockEncoderCore
{
    /// <summary>
    /// Tries multi-partition encodings (2..4 partitions, spec §C.2.21) and returns the lowest-error
    /// block found with its reconstruction error. Uses a shared colour endpoint mode across
    /// partitions (the simplest legal multi-partition layout). For each partition count it scores
    /// all 1024 partition seeds by a cheap endpoint-fit metric, then runs the full weight-grid /
    /// range search only on the best few seeds.
    /// </summary>
    private static UInt128 TryEncodeMultiPartition<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, Footprint footprint, out long bestErrorOut)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        bestErrorOut = long.MaxValue;
        UInt128 bestBlock = default;

        Span<long> seedErrors = stackalloc long[SeedFinalists];
        Span<int> seedFinalists = stackalloc int[SeedFinalists];

        // The candidate modes depend only on block content, not on the partition count or seed, so
        // select them once here rather than per seed.
        Span<ColorEndpointMode> candidateModes = stackalloc ColorEndpointMode[MaxCandidateModes];
        int modeCount = strategy.SelectCandidateModes(texels, candidateModes);
        candidateModes = candidateModes[..modeCount];

        // The cheapest candidate sets the smallest per-partition colour-value count, which decides
        // whether a partition count can fit the colour-value budget at all.
        int minValuesPerPartition = MinColorValuesPerPartition(candidateModes);

        // Search every legal partition count (2..4, spec §C.2.21), skipping any whose cheapest shared
        // mode would still exceed the colour-value budget — for those, the seed scan and per-config
        // search below could only ever produce pruned (illegal) configurations.
        for (int partitionCount = MinMultiPartitions; partitionCount <= MaxPartitions; partitionCount++)
        {
            if (minValuesPerPartition * partitionCount > MaxColorValuesPerBlock)
            {
                continue;
            }

            var finalists = new FinalistSelector(seedFinalists, seedErrors);
            SelectSeedFinalists<TTexel, TStrategy>(texels, footprint, partitionCount, ref finalists);

            foreach (int seed in finalists.Finalists)
            {
                UInt128 block = EncodeMultiPartitionSeed<TTexel, TStrategy>(texels, footprint, partitionCount, seed, candidateModes, out long error);
                if (error < bestErrorOut)
                {
                    bestErrorOut = error;
                    bestBlock = block;
                }
            }
        }

        return bestBlock;
    }

    /// <summary>
    /// Scores every partition seed by the summed per-subset endpoint-line fit error (a cheap proxy
    /// for final quality that ignores weight quantisation) and records the lowest-error seeds in
    /// <paramref name="finalists"/>. Seeds whose hash leaves a subset empty are skipped — an empty
    /// subset wastes endpoint budget.
    /// </summary>
    private static void SelectSeedFinalists<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, Footprint footprint, int partitionCount, ref FinalistSelector finalists)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        for (int seed = 0; seed < PartitionSeedCount; seed++)
        {
            ReadOnlySpan<int> assignment = Partition.GetASTCPartition(footprint, partitionCount, seed).Assignment;
            long error = PartitionFitError<TTexel, TStrategy>(texels, assignment, partitionCount);
            if (error < 0)
            {
                continue; // an empty subset; skip this seed.
            }

            finalists.TryInsert(seed, error);
        }
    }

    /// <summary>
    /// Returns the total squared distance of each texel from its subset's fitted endpoint line, or
    /// -1 if any subset is empty under this assignment.
    /// </summary>
    private static long PartitionFitError<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, ReadOnlySpan<int> assignment, int partitionCount)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        Span<TTexel> subsetLow = stackalloc TTexel[MaxPartitions];
        Span<TTexel> subsetHigh = stackalloc TTexel[MaxPartitions];
        if (!strategy.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh))
        {
            return -1;
        }

        // Expand each subset's endpoints to int channels once, rather than per texel.
        Span<int> low = stackalloc int[ChannelCount * MaxPartitions];
        Span<int> high = stackalloc int[ChannelCount * MaxPartitions];
        for (int p = 0; p < partitionCount; p++)
        {
            strategy.StoreChannels(subsetLow[p], low.Slice(p * ChannelCount, ChannelCount));
            strategy.StoreChannels(subsetHigh[p], high.Slice(p * ChannelCount, ChannelCount));
        }

        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            Span<int> pLow = low.Slice(p * ChannelCount, ChannelCount);
            Span<int> pHigh = high.Slice(p * ChannelCount, ChannelCount);
            int weight = ProjectWeight<TTexel, TStrategy>(texels[t], pLow, pHigh);
            error += ReconstructionError<TTexel, TStrategy>(texels[t], pLow, pHigh, weight);
        }

        return error;
    }

    /// <summary>
    /// Encodes the block for a fixed partition count and seed: fits per-subset endpoints, searches
    /// shared weight grid / range / colour range for the lowest reconstruction error, and assembles
    /// the multi-partition block. Returns the block and its error (<see cref="long.MaxValue"/> if no
    /// legal configuration fits).
    /// </summary>
    private static UInt128 EncodeMultiPartitionSeed<TTexel, TStrategy>(
        ReadOnlySpan<TTexel> texels, Footprint footprint, int partitionCount, int seed,
        ReadOnlySpan<ColorEndpointMode> candidateModes, out long bestErrorOut)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        bestErrorOut = long.MaxValue;
        ReadOnlySpan<int> assignment = Partition.GetASTCPartition(footprint, partitionCount, seed).Assignment;

        Span<TTexel> subsetLow = stackalloc TTexel[MaxPartitions];
        Span<TTexel> subsetHigh = stackalloc TTexel[MaxPartitions];
        if (!strategy.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh))
        {
            return default;
        }

        int texelCount = footprint.PixelCount;
        var block = new BlockInput<TTexel>(
            texels, footprint, assignment, partitionCount, subsetLow[..partitionCount], subsetHigh[..partitionCount]);

        var scratch = new ConfigScratch(
            bestColorValues: stackalloc int[MaxColorValueCount * MaxPartitions],
            bestGridWeights: stackalloc int[MaxGridWeights],
            candidateColorValues: stackalloc int[MaxColorValueCount * MaxPartitions],
            candidateGridWeights: stackalloc int[MaxGridWeights],
            effectiveLow: stackalloc int[ChannelCount * MaxPartitions],
            effectiveHigh: stackalloc int[ChannelCount * MaxPartitions],
            unquantizedColors: stackalloc int[MaxColorValueCount],
            idealWeights: stackalloc int[texelCount],
            fittedGrid: stackalloc double[MaxGridWeights],
            effectiveGrid: stackalloc int[MaxGridWeights],
            perTexelWeights: stackalloc int[texelCount],
            altColorValues: stackalloc int[MaxColorValueCount * MaxPartitions],
            altEffectiveLow: stackalloc int[ChannelCount * MaxPartitions],
            altEffectiveHigh: stackalloc int[ChannelCount * MaxPartitions],
            altIdealWeights: stackalloc int[texelCount],
            altFittedGrid: stackalloc double[MaxGridWeights],
            altGridWeights: stackalloc int[MaxGridWeights],
            altEffectiveGrid: stackalloc int[MaxGridWeights],
            altPerTexelWeights: stackalloc int[texelCount]);

        // All partitions share one colour endpoint mode (the simplest legal multi-partition layout).
        // SearchConfigs keeps only those that fit the colour-value budget (see MaxColorValuesPerBlock).
        if (SearchConfigs<TTexel, TStrategy>(in block, candidateModes, MultiColorStartBit, in scratch) is not { } result)
        {
            return default;
        }

        BestConfig best = result.Config;
        bestErrorOut = result.Error;
        int bestGridCount = best.GridWidth * best.GridHeight;
        ushort blockMode = BlockModeEncoder.Encode(best.GridWidth, best.GridHeight, best.WeightRange, isDualPlane: false);

        return BlockAssembler.AssembleMultiPartition(
            blockMode, partitionCount, seed, best.Mode, best.ColorRange, scratch.BestColorValues[..best.ColorValueCount],
            best.WeightRange, scratch.BestGridWeights[..bestGridCount]);
    }
}
