using AstcSharp.BiseEncoding;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// The single-partition dual-plane search of <see cref="BlockEncoderCore"/> (spec §C.2.20): one
/// channel is driven by an independent second weight plane. Each of the four channels is tried as
/// that channel, and for each the grid size, weight range, and colour mode are searched as in the
/// single-plane path.
/// </summary>
internal static partial class BlockEncoderCore
{
    /// <summary>
    /// Tries single-partition dual-plane encodings (spec §C.2.20) and returns the lowest-error block
    /// with its reconstruction error (<see cref="long.MaxValue"/> if none fits). One channel is
    /// driven by an independent second weight plane; each of the four channels is tried as that
    /// channel, and for each the grid size, weight range, and colour mode are searched as in the
    /// single-plane path. Dual-plane doubles the weight count, so the grid is capped at 32 points.
    /// Single-partition only: the colour/selector bit-budget arithmetic below assumes one partition
    /// (no per-partition extra-CEM bits), supporting multi-partition would require accounting for them.
    /// </summary>
    private static UInt128 TryEncodeDualPlane<TTexel, TStrategy>(ReadOnlySpan<TTexel> texels, Footprint footprint, long earlyOutError, out long bestErrorOut)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        TStrategy strategy = default;
        bestErrorOut = long.MaxValue;
        UInt128 bestBlock = default;

        (TTexel low, TTexel high) = strategy.Fit(texels);
        Span<TTexel> subsetLow = [low];
        Span<TTexel> subsetHigh = [high];
        var block = new BlockInput<TTexel>(
            texels, footprint, Partition.GetSinglePartition(footprint).Assignment, partitionCount: 1, subsetLow, subsetHigh);

        int texelCount = footprint.PixelCount;
        var scratch = new DualPlaneScratch(
            bestColorValues: stackalloc int[MaxColorValueCount],
            bestGridWeights0: stackalloc int[MaxDualPlaneGridWeights],
            bestGridWeights1: stackalloc int[MaxDualPlaneGridWeights],
            candidateColorValues: stackalloc int[MaxColorValueCount],
            candidateGridWeights0: stackalloc int[MaxDualPlaneGridWeights],
            candidateGridWeights1: stackalloc int[MaxDualPlaneGridWeights],
            effectiveLow: stackalloc int[ChannelCount],
            effectiveHigh: stackalloc int[ChannelCount],
            unquantizedColors: stackalloc int[MaxColorValueCount],
            idealWeights0: stackalloc int[texelCount],
            idealWeights1: stackalloc int[texelCount],
            fittedGrid0: stackalloc double[MaxDualPlaneGridWeights],
            fittedGrid1: stackalloc double[MaxDualPlaneGridWeights],
            effectiveGrid0: stackalloc int[MaxDualPlaneGridWeights],
            effectiveGrid1: stackalloc int[MaxDualPlaneGridWeights],
            perTexelWeights0: stackalloc int[texelCount],
            perTexelWeights1: stackalloc int[texelCount]);

        // Same content-aware candidate modes as the single-plane path; the search keeps whichever
        // (mode, channel) pairing reconstructs best.
        Span<ColorEndpointMode> candidateModes = stackalloc ColorEndpointMode[MaxCandidateModes];
        int modeCount = strategy.SelectCandidateModes(texels, candidateModes);

        for (int dualPlaneChannel = 0; dualPlaneChannel < ChannelCount; dualPlaneChannel++)
        {
            if (SearchDualPlaneConfigs<TTexel, TStrategy>(in block, candidateModes[..modeCount], dualPlaneChannel, in scratch) is { } result
                && result.Error < bestErrorOut)
            {
                bestErrorOut = result.Error;
                BestConfig c = result.Config.Config;
                int gridCount = c.GridWidth * c.GridHeight;
                ushort blockMode = BlockModeEncoder.Encode(c.GridWidth, c.GridHeight, c.WeightRange, isDualPlane: true);
                bestBlock = BlockAssembler.AssembleDualPlane(
                    blockMode, c.Mode, c.ColorRange, scratch.BestColorValues[..c.ColorValueCount],
                    c.WeightRange, result.Config.DualPlaneChannel, scratch.BestGridWeights0[..gridCount], scratch.BestGridWeights1[..gridCount]);

                // Once one second-plane channel reconstructs below the target, the remaining channels
                // cannot meaningfully improve it; stop searching them.
                if (bestErrorOut <= earlyOutError)
                {
                    break;
                }
            }
        }

        return bestBlock;
    }

    /// <summary>
    /// Searches grid sizes, weight ranges, and endpoint modes for the lowest-error dual-plane
    /// configuration with <paramref name="dualPlaneChannel"/> driven by the second plane. Plane 0 is
    /// fitted over the other three channels and plane 1 over the selected channel alone; both grids
    /// reconstruct through the decoder's infill and the dual-plane blend. Leaves the winning colour
    /// values and both grids in <paramref name="scratch"/>. Returns the winning configuration and its
    /// error, or <c>null</c> if nothing legal fits.
    /// </summary>
    private static SearchResult<DualPlaneConfig>? SearchDualPlaneConfigs<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ReadOnlySpan<ColorEndpointMode> candidateModes,
        int dualPlaneChannel,
        in DualPlaneScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        long bestError = long.MaxValue;
        DualPlaneConfig best = default;

        int maxGridWidth = Math.Min(block.Footprint.Width, MaxGridDim);
        int maxGridHeight = Math.Min(block.Footprint.Height, MaxGridDim);

        foreach (ColorEndpointMode mode in candidateModes)
        {
            // Single-partition, so the colour-value count is just the mode's own (at most 8 for RGBA)
            // and always within the 18-value budget; the bit budget is enforced by
            // TryResolveDualPlaneConfig, which also reserves the dual-plane selector bits.
            int colorValueCount = mode.GetColorValuesCount();

            for (int gridHeight = MinGridDim; gridHeight <= maxGridHeight; gridHeight++)
            {
                for (int gridWidth = MinGridDim; gridWidth <= maxGridWidth; gridWidth++)
                {
                    int gridWeightCount = gridWidth * gridHeight;
                    if (gridWeightCount > MaxDualPlaneGridWeights)
                    {
                        continue;
                    }

                    DecimationInfo decimation = DecimationTable.Get(block.Footprint, gridWidth, gridHeight);
                    int preparedColorRange = 0;

                    foreach (int weightRange in WeightRangeCandidates)
                    {
                        if (!TryResolveDualPlaneConfig(
                                gridWidth, gridHeight, gridWeightCount, weightRange, colorValueCount, out int colorRange))
                        {
                            continue;
                        }

                        if (colorRange != preparedColorRange)
                        {
                            PrepareDualPlaneConfig<TTexel, TStrategy>(in block, mode, dualPlaneChannel, gridWeightCount, decimation, colorRange, in scratch);
                            preparedColorRange = colorRange;
                        }

                        long error = MeasureDualPlaneConfig<TTexel, TStrategy>(in block, dualPlaneChannel, gridWeightCount, weightRange, decimation, in scratch);
                        if (error < bestError)
                        {
                            bestError = error;
                            best = new DualPlaneConfig(
                                new BestConfig(mode, gridWidth, gridHeight, weightRange, colorRange, colorValueCount), dualPlaneChannel);
                            scratch.CandidateColorValues[..colorValueCount].CopyTo(scratch.BestColorValues);
                            scratch.CandidateGridWeights0[..gridWeightCount].CopyTo(scratch.BestGridWeights0);
                            scratch.CandidateGridWeights1[..gridWeightCount].CopyTo(scratch.BestGridWeights1);
                        }
                    }
                }
            }
        }

        return best.Config.WeightRange != 0 ? new SearchResult<DualPlaneConfig>(best, bestError) : null;
    }

    /// <summary>
    /// Dual-plane variant of <see cref="TryResolveConfig"/>: validates the (grid, weight-range)
    /// candidate as a dual-plane block mode, checks the doubled weight bit count against the
    /// [24, 96] window, and resolves the colour range that fits the remaining budget. The 2-bit
    /// colour-component selector takes two bits from the colour budget (spec §C.2.20).
    /// </summary>
    private static bool TryResolveDualPlaneConfig(
        int gridWidth, int gridHeight, int gridWeightCount, int weightRange, int colorValueCount, out int colorRange)
    {
        colorRange = 0;
        if (!BlockModeEncoder.TryEncode(gridWidth, gridHeight, weightRange, isDualPlane: true, out _))
        {
            return false;
        }

        int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(gridWeightCount * 2, weightRange);
        if (weightBitCount is < MinWeightBits or > MaxWeightBits)
        {
            return false;
        }

        // Single-partition only: ColorStartBit is the 1-partition value (17) and there are no extra
        // per-partition CEM bits to reserve. Extending dual-plane to multiple partitions would need
        // the multi-partition colour start bit and the extra-CEM-bit accounting the decoder applies.
        int maxColorBits = BlockBits - weightBitCount - DualPlaneSelectorBits - ColorStartBit;
        return BlockModeDecoder.TryResolveColorEncoding(colorValueCount, maxColorBits, out colorRange, out _);
    }

    /// <summary>
    /// Dual-plane analogue of <see cref="PrepareConfig"/>: encodes/decodes the endpoints, then
    /// projects each texel twice — plane 0 over the channels other than
    /// <paramref name="dualPlaneChannel"/>, plane 1 over that channel alone — and fits both grids.
    /// </summary>
    private static void PrepareDualPlaneConfig<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        ColorEndpointMode mode,
        int dualPlaneChannel,
        int gridWeightCount,
        DecimationInfo decimation,
        int colorRange,
        in DualPlaneScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        ColorGeometry.EncodeAndDecodeEndpoints<TTexel, TStrategy>(
            mode, block.SubsetLow[0], block.SubsetHigh[0], colorRange,
            scratch.CandidateColorValues, scratch.UnquantizedColors, scratch.EffectiveLow, scratch.EffectiveHigh);

        int plane1Mask = 1 << dualPlaneChannel;
        int plane0Mask = AllChannelsMask & ~plane1Mask;
        ReadOnlySpan<TTexel> texels = block.Texels;
        Span<int> idealWeights0 = scratch.IdealWeights0;
        Span<int> idealWeights1 = scratch.IdealWeights1;
        for (int t = 0; t < texels.Length; t++)
        {
            idealWeights0[t] = ColorGeometry.ProjectWeightMasked<TTexel, TStrategy>(texels[t], scratch.EffectiveLow, scratch.EffectiveHigh, plane0Mask);
            idealWeights1[t] = ColorGeometry.ProjectWeightMasked<TTexel, TStrategy>(texels[t], scratch.EffectiveLow, scratch.EffectiveHigh, plane1Mask);
        }

        DecimationFit.Fit(idealWeights0[..texels.Length], decimation, gridWeightCount, scratch.FittedGrid0[..gridWeightCount]);
        DecimationFit.Fit(idealWeights1[..texels.Length], decimation, gridWeightCount, scratch.FittedGrid1[..gridWeightCount]);
    }

    /// <summary>
    /// Dual-plane analogue of <see cref="MeasureConfig"/>: quantises both grids to the weight range,
    /// infills both to per-texel weights, and sums the dual-plane reconstruction error (the selected
    /// channel using plane 1's weight, the rest plane 0's).
    /// </summary>
    private static long MeasureDualPlaneConfig<TTexel, TStrategy>(
        in BlockInput<TTexel> block,
        int dualPlaneChannel,
        int gridWeightCount,
        int weightRange,
        DecimationInfo decimation,
        in DualPlaneScratch scratch)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
    {
        Span<int> effectiveGrid0 = scratch.EffectiveGrid0[..gridWeightCount];
        Span<int> effectiveGrid1 = scratch.EffectiveGrid1[..gridWeightCount];
        ColorGeometry.QuantizeGridToEffective(
            scratch.FittedGrid0[..gridWeightCount], weightRange, scratch.CandidateGridWeights0[..gridWeightCount], effectiveGrid0);
        ColorGeometry.QuantizeGridToEffective(
            scratch.FittedGrid1[..gridWeightCount], weightRange, scratch.CandidateGridWeights1[..gridWeightCount], effectiveGrid1);

        ReadOnlySpan<TTexel> texels = block.Texels;
        Span<int> perTexelWeights0 = scratch.PerTexelWeights0[..texels.Length];
        Span<int> perTexelWeights1 = scratch.PerTexelWeights1[..texels.Length];
        DecimationTable.InfillWeights(effectiveGrid0, decimation, perTexelWeights0);
        DecimationTable.InfillWeights(effectiveGrid1, decimation, perTexelWeights1);

        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            error += ColorGeometry.ReconstructionErrorDualPlane<TTexel, TStrategy>(
                texels[t], scratch.EffectiveLow, scratch.EffectiveHigh, perTexelWeights0[t], dualPlaneChannel, perTexelWeights1[t]);
        }

        return error;
    }

    /// <summary>
    /// The winning configuration of the dual-plane search: a single-plane <see cref="BestConfig"/>
    /// plus the colour-component selector (which channel the second weight plane drives).
    /// </summary>
    private readonly record struct DualPlaneConfig(BestConfig Config, int DualPlaneChannel);

    /// <summary>
    /// Reusable buffers for the dual-plane search, mirroring <see cref="ConfigScratch"/> but holding
    /// two weight grids (one per plane). <c>Best*</c> retain the lowest-error config; <c>Candidate*</c>
    /// hold the config under test; the rest are per-config working buffers.
    /// </summary>
#pragma warning disable S107
    private readonly ref struct DualPlaneScratch(
        Span<int> bestColorValues,
        Span<int> bestGridWeights0,
        Span<int> bestGridWeights1,
        Span<int> candidateColorValues,
        Span<int> candidateGridWeights0,
        Span<int> candidateGridWeights1,
        Span<int> effectiveLow,
        Span<int> effectiveHigh,
        Span<int> unquantizedColors,
        Span<int> idealWeights0,
        Span<int> idealWeights1,
        Span<double> fittedGrid0,
        Span<double> fittedGrid1,
        Span<int> effectiveGrid0,
        Span<int> effectiveGrid1,
        Span<int> perTexelWeights0,
        Span<int> perTexelWeights1)
#pragma warning restore S107
    {
        public Span<int> BestColorValues { get; } = bestColorValues;
        public Span<int> BestGridWeights0 { get; } = bestGridWeights0;
        public Span<int> BestGridWeights1 { get; } = bestGridWeights1;
        public Span<int> CandidateColorValues { get; } = candidateColorValues;
        public Span<int> CandidateGridWeights0 { get; } = candidateGridWeights0;
        public Span<int> CandidateGridWeights1 { get; } = candidateGridWeights1;
        public Span<int> EffectiveLow { get; } = effectiveLow;
        public Span<int> EffectiveHigh { get; } = effectiveHigh;
        public Span<int> UnquantizedColors { get; } = unquantizedColors;
        public Span<int> IdealWeights0 { get; } = idealWeights0;
        public Span<int> IdealWeights1 { get; } = idealWeights1;
        public Span<double> FittedGrid0 { get; } = fittedGrid0;
        public Span<double> FittedGrid1 { get; } = fittedGrid1;
        public Span<int> EffectiveGrid0 { get; } = effectiveGrid0;
        public Span<int> EffectiveGrid1 { get; } = effectiveGrid1;
        public Span<int> PerTexelWeights0 { get; } = perTexelWeights0;
        public Span<int> PerTexelWeights1 { get; } = perTexelWeights1;
    }
}
