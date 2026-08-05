using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// The colour-space-agnostic per-block encoder search, shared by the LDR and HDR profiles. Tries a
/// single-partition encoding and (for large enough footprints) multi-partition encodings
/// (spec §C.2.21), keeping whichever reconstructs the block best.
/// </summary>
/// <remarks>
/// <para>
/// Within each partition, endpoints are fitted to the principal axis of that subset's texels and every
/// texel's weight is its projection onto its subset's endpoint line. A weight grid (possibly decimated
/// below the footprint size, spec §C.2.18) is fitted to those weights.
/// The endpoint mode, grid size, weight range, and colour range are chosen by searching the configurations
/// that fit the 128-bit budget and keeping the one with the lowest reconstruction error.
/// </para>
/// <para>
/// The colour-space-specific operations (endpoint fitting, candidate-mode selection, endpoint
/// encoding, per-texel channel access, and reconstruction) are supplied by an <see cref="IColorSpaceStrategy{TTexel}"/>.
/// </para>
/// </remarks>
internal static partial class BlockEncoderCore
{
    // RGBA channels per texel.
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // The widest single-partition colour value count (RGBA modes: r0,r1,g0,g1,b0,b1,a0,a1),
    // used to size the colour-value scratch buffers. HDR RGBA (CEM 15) also uses 8 values.
    private const int MaxColorValueCount = 8;

    // Upper bound on candidate endpoint modes tried per block; sizes the candidate-mode scratch span.
    private const int MaxCandidateModes = 8;

    // Partition counts the encoder searches (spec §C.2.10 allows 1..4). The seed space is 10 bits
    // (1024 patterns). MaxPartitions sizes the per-subset scratch buffers (the spec maximum). The
    // field bit positions live in BlockLayout.
    private const int MinMultiPartitions = 2;
    private const int MaxPartitions = 4;

    // The multi-partition search stops at this partition count — a speed/quality knob distinct from
    // MaxPartitions (which sizes the spec-maximum scratch buffers).
    private const int MaxSearchedPartitions = 2;
    private const int PartitionSeedCount = 1 << PartitionSeedBits;

    // A single-plane block may hold at most 18 colour endpoint values (spec §C.2.11); the decoder
    // rejects any block exceeding this. With one shared colour endpoint mode across partitions, this
    // budget bounds the partition count by the mode's per-partition value count: RGBA (8) fits 2
    // partitions, RGB (6) fits 3, luma (2) fits 4 (also the MaxPartitions cap). SearchConfigs prunes
    // any mode/partition-count combination that exceeds the budget.
    private const int MaxColorValuesPerBlock = 18;

    // Partitioning only helps when there are enough texels to host distinct colour regions; below
    // this, the per-partition endpoint and seed overhead outweighs any benefit.
    private const int MinTexelsForPartitioning = 16;

    // Number of best seeds (by endpoint-fit error) carried into the full per-config search per
    // partition count — the seed space is searched cheaply first, then refined for a few finalists.
    private const int SeedFinalists = 3;

    // Candidate weight ranges to try, richest first (spec §C.2.7 Table 23 weight ranges).
    private static ReadOnlySpan<int> WeightRangeCandidates => [31, 23, 19, 15, 11, 9, 7, 5, 4, 3, 2, 1];

    // The maximum weight value the decoder interpolates with (spec §C.2.19): weights span [0, 64].
    private const int MaxWeight = 64;

    // Channel-mask covering all four RGBA channels, used for whole-line weight projection.
    private const int AllChannelsMask = 0b1111;

    // Dual-plane blocks (spec §C.2.20) carry two interleaved weight planes, so the grid holds twice
    // the weights and is capped at 32 points. The colour-component selector bit width lives in
    // BlockLayout.
    private const int MaxDualPlaneGridWeights = MaxGridWeights / 2;

    // Grid dimensions range from 2 to 12 (spec §C.2.8); a single weight plane holds at most 64
    // weights (spec §C.2.11), and the weight bit total must fall in [24, 96].
    private const int MinGridDim = 2;
    private const int MaxGridDim = 12;
    private const int MaxGridWeights = 64;
    private const int MinWeightBits = 24;
    private const int MaxWeightBits = 96;

    /// <summary>
    /// Encodes <paramref name="texels"/> (one texel per footprint texel, raster order) into a
    /// 128-bit block. Tries a single-partition encoding and, when the footprint is large enough,
    /// multi-partition encodings (spec §C.2.21), returning whichever reconstructs the block with the
    /// lowest error.
    /// </summary>
    public static UInt128 Encode<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, Footprint footprint)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        UInt128 bestBlock = EncodeSinglePartition<TTexel, TStrategy>(texels, footprint, out long bestError);

        // The costlier multi-partition and dual-plane searches can only help while there is error
        // worth chasing - once the block reconstructs below the early-out target, skip them. This is
        // where most of the encoder's time is saved on the smooth blocks typical of natural images.
        long earlyOutError = strategy.EarlyOutPerSampleError * texels.Length * ChannelCount;
        if (bestError <= earlyOutError)
        {
            return bestBlock;
        }

        if (footprint.PixelCount >= MinTexelsForPartitioning)
        {
            UInt128 multiBlock = TryEncodeMultiPartition<TTexel, TStrategy>(texels, footprint, out long multiError);
            if (multiError < bestError)
            {
                bestError = multiError;
                bestBlock = multiBlock;
            }

            if (bestError <= earlyOutError)
            {
                return bestBlock;
            }
        }

        // A second weight plane lets one channel vary independently of the other three (spec §C.2.20)
        // — a large win for content where, e.g. alpha is uncorrelated with RGB. Tried as an additive
        // single-partition candidate; kept only if it reconstructs better than the plane-1 result.
        UInt128 dualPlaneBlock = TryEncodeDualPlane<TTexel, TStrategy>(texels, footprint, earlyOutError, out long dualPlaneError);
        if (dualPlaneError < bestError)
        {
            bestBlock = dualPlaneBlock;
        }

        return bestBlock;
    }

    /// <summary>
    /// Encodes a single-partition block (the proven path) and reports its reconstruction error.
    /// Searches weight-grid sizes from the footprint down to 2x2 and, per grid, the weight ranges
    /// that fit the bit budget, keeping the configuration with the lowest error. A grid smaller than
    /// the footprint (decimation, spec §C.2.18) is what makes footprints larger than 64 texels
    /// encodable and lets large blocks spend more bits per weight.
    /// </summary>
    private static UInt128 EncodeSinglePartition<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, Footprint footprint, out long bestErrorOut)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        (TTexel low, TTexel high) = strategy.Fit(texels);

        int texelCount = footprint.PixelCount;

        // Single partition: one all-zero assignment, one endpoint pair.
        Span<TTexel> subsetLow = [low];
        Span<TTexel> subsetHigh = [high];
        var block = new BlockInput<TTexel>(
            texels, footprint, Partition.GetSinglePartition(footprint).Assignment, partitionCount: 1, subsetLow, subsetHigh);

        // Per-block scratch, allocated once on this frame (stackalloc can't escape into a helper).
        // Sized for up to MaxPartitions partitions; single-partition uses only the first slot.
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

        // Cheaper endpoint modes (fewer colour values) leave more of the 128-bit budget for weight
        // precision, so a mode that drops alpha or chroma can win on opaque or grey content.
        Span<ColorEndpointMode> candidateModes = stackalloc ColorEndpointMode[MaxCandidateModes];
        int modeCount = strategy.SelectCandidateModes(texels, candidateModes);

        if (SearchConfigs<TTexel, TStrategy>(in block, candidateModes[..modeCount], ColorStartBit, in scratch) is not { } result)
        {
            throw new InvalidOperationException(
                $"No legal single-partition encoding fits footprint {footprint.Width}x{footprint.Height}.");
        }

        BestConfig best = result.Config;
        bestErrorOut = result.Error;
        int bestGridCount = best.GridWidth * best.GridHeight;
        ushort blockMode = BlockModeEncoder.Encode(best.GridWidth, best.GridHeight, best.WeightRange, isDualPlane: false);

        return BlockAssembler.AssembleSinglePartition(
            blockMode, best.Mode, best.ColorRange, scratch.BestColorValues[..best.ColorValueCount],
            best.WeightRange, scratch.BestGridWeights[..bestGridCount]);
    }

    /// <summary>
    /// Searches grid sizes (footprint down to 2x2), weight ranges, and the candidate endpoint modes
    /// for the configuration that reconstructs the block with the lowest error.
    /// <paramref name="colorStartBit"/> is the block layout's first colour-data bit (17
    /// single-partition, 29 multi-partition), which sets the colour bit budget. The winning colour
    /// values and quantised grid weights are left in <see cref="ConfigScratch.BestColorValues"/> /
    /// <see cref="ConfigScratch.BestGridWeights"/>. Returns the winning configuration and its error,
    /// or <c>null</c> if nothing legal fits.
    /// </summary>
    private static SearchResult<BestConfig>? SearchConfigs<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ReadOnlySpan<ColorEndpointMode> candidateModes,
        int colorStartBit,
        in ConfigScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        long bestError = long.MaxValue;
        BestConfig best = default;

        int maxGridWidth = Math.Min(block.Footprint.Width, MaxGridDim);
        int maxGridHeight = Math.Min(block.Footprint.Height, MaxGridDim);

        foreach (ColorEndpointMode mode in candidateModes)
        {
            int colorValueCount = mode.GetColorValuesCount() * block.PartitionCount;
            if (colorValueCount > MaxColorValuesPerBlock)
            {
                continue;
            }

            for (int gridHeight = MinGridDim; gridHeight <= maxGridHeight; gridHeight++)
            {
                for (int gridWidth = MinGridDim; gridWidth <= maxGridWidth; gridWidth++)
                {
                    int gridWeightCount = gridWidth * gridHeight;
                    if (gridWeightCount > MaxGridWeights)
                    {
                        continue;
                    }

                    DecimationInfo decimation = DecimationTable.Get(block.Footprint, gridWidth, gridHeight);

                    // The endpoint encode/decode, texel projection, and grid fit depend only on the
                    // colour range, which is non-increasing as the weight range shrinks; reuse them
                    // across every weight range that resolves to the same colour range.
                    int preparedColorRange = 0;

                    foreach (int weightRange in WeightRangeCandidates)
                    {
                        if (!TryResolveConfig(gridWidth, gridHeight, gridWeightCount, weightRange, colorStartBit, colorValueCount, out int colorRange))
                        {
                            continue;
                        }

                        if (colorRange != preparedColorRange)
                        {
                            PrepareConfig<TTexel, TStrategy>(in block, mode, gridWeightCount, decimation, colorRange, in scratch);
                            preparedColorRange = colorRange;
                        }

                        long error = MeasureConfig<TTexel, TStrategy>(in block, mode, gridWeightCount, weightRange, colorRange, decimation, in scratch);
                        if (error < bestError)
                        {
                            bestError = error;
                            best = new BestConfig(mode, gridWidth, gridHeight, weightRange, colorRange, colorValueCount);
                            scratch.CandidateColorValues[..colorValueCount].CopyTo(scratch.BestColorValues);
                            scratch.CandidateGridWeights[..gridWeightCount].CopyTo(scratch.BestGridWeights);
                        }
                    }
                }
            }
        }

        return best.WeightRange != 0 ? new SearchResult<BestConfig>(best, bestError) : null;
    }
}
