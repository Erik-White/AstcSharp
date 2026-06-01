using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes an LDR block, trying a single-partition encoding and (for large enough footprints)
/// multi-partition encodings (spec §C.2.21), keeping whichever reconstructs the block best. Within
/// each partition, endpoints are fitted to the principal axis of that subset's texels and every
/// texel's weight is its projection onto its subset's endpoint line. A weight grid (possibly
/// decimated below the footprint size, spec §C.2.18) is fitted to those weights. The endpoint mode,
/// grid size, weight range, and colour range are chosen by searching the configurations that fit
/// the 128-bit budget and keeping the one with the lowest reconstruction error.
/// </summary>
internal static class LdrBlockEncoder
{
    // Total bits in an ASTC block (spec §C.2.7).
    private const int BlockBits = 128;

    // RGBA channels per texel.
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // The widest single-partition colour value count (RGBA modes: r0,r1,g0,g1,b0,b1,a0,a1),
    // used to size the colour-value scratch buffers.
    private const int MaxColorValueCount = 8;

    // Upper bound on candidate endpoint modes tried per block (luma, RGB, RGBA — each direct +
    // base+offset). Sizes the candidate-mode scratch span.
    private const int MaxCandidateModes = 6;

    // Block-mode layout (spec §C.2.10): the 11-bit block mode, then the 2-bit partition-count
    // field. Single-partition blocks store the colour endpoint mode at bit 13 and colour data at
    // bit 17. Multi-partition blocks store a 10-bit partition seed at bit 13, a 2-bit shared-CEM
    // marker (0) at bit 23, the shared CEM at bit 25, and colour data at bit 29.
    private const int BlockModeStartBit = 0;
    private const int BlockModeBits = 11;
    private const int PartitionCountStartBit = 11;
    private const int PartitionCountBits = 2;
    private const int CemStartBit = 13;
    private const int CemBits = 4;
    private const int ColorStartBit = 17;
    private const int SinglePartitionField = 0; // partition-count field value for 1 partition (count - 1)
    private const int PartitionSeedStartBit = 13;
    private const int PartitionSeedBits = 10;
    private const int SharedCemMarkerStartBit = 23;
    private const int SharedCemMarkerBits = 2;
    private const int SharedCemStartBit = 25;
    private const int MultiColorStartBit = 29;

    // Partition counts the encoder searches (spec §C.2.10 allows 1..4). The seed space is 10 bits
    // (1024 patterns). MaxPartitions sizes the per-subset scratch buffers (the spec maximum).
    private const int MinMultiPartitions = 2;
    private const int MaxPartitions = 4;
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
    private const int SeedFinalists = 4;

    // Candidate weight ranges to try, richest first (spec §C.2.7 Table 23 weight ranges).
    private static ReadOnlySpan<int> WeightRangeCandidates => [31, 23, 19, 15, 11, 9, 7, 5, 4, 3, 2, 1];

    // The maximum weight value the decoder interpolates with (spec §C.2.19): weights span [0, 64].
    private const int MaxWeight = 64;

    // Grid dimensions range from 2 to 12 (spec §C.2.8); a single weight plane holds at most 64
    // weights (spec §C.2.11), and the weight bit total must fall in [24, 96].
    private const int MinGridDim = 2;
    private const int MaxGridDim = 12;
    private const int MaxGridWeights = 64;
    private const int MinWeightBits = 24;
    private const int MaxWeightBits = 96;

    /// <summary>
    /// Encodes <paramref name="texels"/> (one <see cref="RgbaColor"/> per footprint texel, raster
    /// order) into a 128-bit block. Tries a single-partition encoding and, when the footprint is
    /// large enough, multi-partition encodings (spec §C.2.21), returning whichever reconstructs the
    /// block with the lowest error.
    /// </summary>
    public static UInt128 Encode(ReadOnlySpan<RgbaColor> texels, Footprint footprint)
    {
        UInt128 bestBlock = EncodeSinglePartition(texels, footprint, out long bestError);

        if (bestError > 0 && footprint.PixelCount >= MinTexelsForPartitioning)
        {
            UInt128 multiBlock = TryEncodeMultiPartition(texels, footprint, out long multiError);
            if (multiError < bestError)
            {
                bestBlock = multiBlock;
            }
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
    private static UInt128 EncodeSinglePartition(ReadOnlySpan<RgbaColor> texels, Footprint footprint, out long bestErrorOut)
    {
        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        int texelCount = footprint.PixelCount;

        // Single partition: one all-zero assignment, one endpoint pair.
        Span<RgbaColor> subsetLow = [low];
        Span<RgbaColor> subsetHigh = [high];
        var block = new BlockInput(
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
            perTexelWeights: stackalloc int[texelCount]);

        // Cheaper endpoint modes (fewer colour values) leave more of the 128-bit budget for weight
        // precision, so a mode that drops alpha or chroma can win on opaque or grey content.
        Span<ColorEndpointMode> candidateModes = stackalloc ColorEndpointMode[MaxCandidateModes];
        int modeCount = SelectCandidateModes(texels, candidateModes);

        if (!SearchConfigs(in block, candidateModes[..modeCount], ColorStartBit, in scratch, out BestConfig best, out bestErrorOut))
        {
            throw new InvalidOperationException(
                $"No legal single-partition encoding fits footprint {footprint.Width}x{footprint.Height}.");
        }

        int bestGridCount = best.GridWidth * best.GridHeight;
        return Assemble(best, scratch.BestColorValues[..best.ColorValueCount], scratch.BestGridWeights[..bestGridCount]);
    }

    /// <summary>
    /// The winning configuration of the per-block search.
    /// </summary>
    private readonly record struct BestConfig(
        ColorEndpointMode Mode, int GridWidth, int GridHeight, int WeightRange, int ColorRange, int ColorValueCount);

    /// <summary>
    /// The fixed inputs of a per-block configuration search: the block's texels and footprint, the
    /// partition assignment (all-zero for single-partition), the partition count, and the fitted
    /// per-partition endpoints. Threaded as one <c>in</c> parameter through the search so the
    /// individual config methods stay readable.
    /// </summary>
    private readonly ref struct BlockInput(
        ReadOnlySpan<RgbaColor> texels,
        Footprint footprint,
        ReadOnlySpan<int> assignment,
        int partitionCount,
        ReadOnlySpan<RgbaColor> subsetLow,
        ReadOnlySpan<RgbaColor> subsetHigh)
    {
        public ReadOnlySpan<RgbaColor> Texels { get; } = texels;
        public Footprint Footprint { get; } = footprint;
        public ReadOnlySpan<int> Assignment { get; } = assignment;
        public int PartitionCount { get; } = partitionCount;
        public ReadOnlySpan<RgbaColor> SubsetLow { get; } = subsetLow;
        public ReadOnlySpan<RgbaColor> SubsetHigh { get; } = subsetHigh;
    }

    /// <summary>
    /// Picks the colour endpoint modes worth trying for a block, cheapest-content-fit first.
    /// Grey blocks add the luminance modes (2 values); opaque blocks add the RGB modes (no alpha);
    /// blocks with varying alpha or chroma fall back to the full RGBA modes. Each "direct" mode is
    /// paired with its "base+offset" sibling, which can spend fewer bits when the endpoints are close.
    /// </summary>
    private static int SelectCandidateModes(ReadOnlySpan<RgbaColor> texels, Span<ColorEndpointMode> modes)
    {
        bool opaque = true;
        bool grey = true;
        foreach (RgbaColor texel in texels)
        {
            opaque &= texel.A == byte.MaxValue;
            grey &= texel.R == texel.G && texel.G == texel.B;
        }

        int count = 0;
        if (grey && opaque)
        {
            modes[count++] = ColorEndpointMode.LdrLumaDirect;
            modes[count++] = ColorEndpointMode.LdrLumaBaseOffset;
        }

        if (opaque)
        {
            modes[count++] = ColorEndpointMode.LdrRgbDirect;
            modes[count++] = ColorEndpointMode.LdrRgbBaseOffset;
        }

        // The full RGBA modes always apply and are the only legal choice when alpha varies.
        modes[count++] = ColorEndpointMode.LdrRgbaDirect;
        modes[count++] = ColorEndpointMode.LdrRgbaBaseOffset;
        return count;
    }

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
    /// Reusable buffers for one block's configuration search, allocated once on the caller's stack
    /// frame and threaded through <see cref="SearchConfigs"/>. <c>Candidate*</c> hold the config
    /// currently under test; <c>Best*</c> retain the lowest-error config found so far; the remainder
    /// are per-config working buffers shared between <see cref="PrepareConfig"/> (writes
    /// <see cref="EffectiveLow"/>/<see cref="EffectiveHigh"/>/<see cref="FittedGrid"/>) and
    /// <see cref="MeasureConfig"/> (reads them).
    /// </summary>
    private readonly ref struct ConfigScratch(
        Span<int> bestColorValues,
        Span<int> bestGridWeights,
        Span<int> candidateColorValues,
        Span<int> candidateGridWeights,
        Span<int> effectiveLow,
        Span<int> effectiveHigh,
        Span<int> unquantizedColors,
        Span<int> idealWeights,
        Span<double> fittedGrid,
        Span<int> effectiveGrid,
        Span<int> perTexelWeights)
    {
        public Span<int> BestColorValues { get; } = bestColorValues;
        public Span<int> BestGridWeights { get; } = bestGridWeights;
        public Span<int> CandidateColorValues { get; } = candidateColorValues;
        public Span<int> CandidateGridWeights { get; } = candidateGridWeights;
        public Span<int> EffectiveLow { get; } = effectiveLow;
        public Span<int> EffectiveHigh { get; } = effectiveHigh;
        public Span<int> UnquantizedColors { get; } = unquantizedColors;
        public Span<int> IdealWeights { get; } = idealWeights;
        public Span<double> FittedGrid { get; } = fittedGrid;
        public Span<int> EffectiveGrid { get; } = effectiveGrid;
        public Span<int> PerTexelWeights { get; } = perTexelWeights;
    }

    /// <summary>
    /// Computes the parts of a configuration that depend only on the colour range (not the weight
    /// range): encodes each partition's endpoints (decoding them back through the real codec into
    /// <see cref="ConfigScratch.EffectiveLow"/>/<see cref="ConfigScratch.EffectiveHigh"/>), projects
    /// each texel's ideal weight onto its partition's endpoint line, and fits the continuous grid
    /// weights (the decimation inverse, spec §C.2.18) into <see cref="ConfigScratch.FittedGrid"/>.
    /// The weight-range loop reuses this across every range that resolves to the same colour range.
    /// </summary>
    private static void PrepareConfig(
        in BlockInput block,
        ColorEndpointMode mode,
        int gridWeightCount,
        DecimationInfo decimation,
        int colorRange,
        in ConfigScratch scratch)
    {
        int valuesPerPartition = mode.GetColorValuesCount();
        Span<int> effectiveLow = scratch.EffectiveLow;
        Span<int> effectiveHigh = scratch.EffectiveHigh;
        for (int p = 0; p < block.PartitionCount; p++)
        {
            EncodeAndDecodeEndpoints(
                mode, block.SubsetLow[p], block.SubsetHigh[p], colorRange,
                scratch.CandidateColorValues.Slice(p * valuesPerPartition, valuesPerPartition),
                scratch.UnquantizedColors,
                effectiveLow.Slice(p * ChannelCount, ChannelCount),
                effectiveHigh.Slice(p * ChannelCount, ChannelCount));
        }

        ReadOnlySpan<RgbaColor> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        Span<int> idealWeights = scratch.IdealWeights;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            idealWeights[t] = ProjectWeight(
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
    private static long MeasureConfig(
        in BlockInput block,
        int gridWeightCount,
        int weightRange,
        DecimationInfo decimation,
        in ConfigScratch scratch)
    {
        Span<double> fittedGrid = scratch.FittedGrid[..gridWeightCount];
        Span<int> quantGridWeights = scratch.CandidateGridWeights;
        Span<int> effectiveGrid = scratch.EffectiveGrid[..gridWeightCount];
        for (int p = 0; p < gridWeightCount; p++)
        {
            int quant = Quantization.QuantizeWeightToRange(RoundWeight(fittedGrid[p]), weightRange);
            quantGridWeights[p] = quant;
            effectiveGrid[p] = Quantization.UnquantizeWeightFromRange(quant, weightRange);
        }

        // Infill the effective grid weights back to per-texel weights exactly as the decoder does.
        ReadOnlySpan<RgbaColor> texels = block.Texels;
        ReadOnlySpan<int> assignment = block.Assignment;
        Span<int> perTexelWeights = scratch.PerTexelWeights[..texels.Length];
        DecimationTable.InfillWeights(effectiveGrid, decimation, perTexelWeights);

        Span<int> effectiveLow = scratch.EffectiveLow;
        Span<int> effectiveHigh = scratch.EffectiveHigh;
        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            error += ReconstructionError(
                texels[t], effectiveLow.Slice(p * ChannelCount, ChannelCount), effectiveHigh.Slice(p * ChannelCount, ChannelCount), perTexelWeights[t]);
        }

        return error;
    }

    /// <summary>
    /// Searches grid sizes (footprint down to 2x2), weight ranges, and the candidate endpoint modes
    /// for the configuration that reconstructs the block with the lowest error.
    /// <paramref name="colorStartBit"/> is the block layout's first colour-data bit (17
    /// single-partition, 29 multi-partition), which sets the colour bit budget. The winning colour
    /// values and quantised grid weights are left in <see cref="ConfigScratch.BestColorValues"/> /
    /// <see cref="ConfigScratch.BestGridWeights"/>. Returns the winning configuration, or false if
    /// nothing legal fits.
    /// </summary>
    private static bool SearchConfigs(
        in BlockInput block,
        ReadOnlySpan<ColorEndpointMode> candidateModes,
        int colorStartBit,
        in ConfigScratch scratch,
        out BestConfig best,
        out long bestError)
    {
        bestError = long.MaxValue;
        best = default;

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
                            PrepareConfig(in block, mode, gridWeightCount, decimation, colorRange, in scratch);
                            preparedColorRange = colorRange;
                        }

                        long error = MeasureConfig(in block, gridWeightCount, weightRange, decimation, in scratch);
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

        return best.WeightRange != 0;
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
    private static void EncodeAndDecodeEndpoints(
        ColorEndpointMode mode,
        RgbaColor low,
        RgbaColor high,
        int colorRange,
        Span<int> colorValues,
        Span<int> unquantizedScratch,
        Span<int> effectiveLow,
        Span<int> effectiveHigh)
    {
        int colorValueCount = mode.GetColorValuesCount();
        Span<int> values = colorValues[..colorValueCount];
        EndpointEncoder.Encode(mode, low, high, colorRange, values);

        // Unquantise the stored colour values and decode the endpoint pair exactly as the decoder
        // does (its decode operates on unquantised values).
        Span<int> unquantizedSlice = unquantizedScratch[..colorValueCount];
        values.CopyTo(unquantizedSlice);
        Quantization.UnquantizeCEValuesBatch(unquantizedSlice, colorRange);

        ColorEndpointPair pair = EndpointCodec.Decode(unquantizedSlice, mode);
        StoreChannels(pair.LdrLow, effectiveLow);
        StoreChannels(pair.LdrHigh, effectiveHigh);
    }

    private static void StoreChannels(RgbaColor color, Span<int> channels)
    {
        channels[0] = color.R;
        channels[1] = color.G;
        channels[2] = color.B;
        channels[3] = color.A;
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
    /// Rounds a fitted grid weight to the nearest integer, rounding halves away from zero to match
    /// the decoder's round-half-up infill convention (spec §C.2.18, <c>(… + 8) >> 4</c>). The
    /// default <see cref="Math.Round(double)"/> rounds halves to even, which would bias half-valued
    /// weights inconsistently against the decoder.
    /// </summary>
    private static int RoundWeight(double weight) => (int)Math.Round(weight, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Tries multi-partition encodings (2..4 partitions, spec §C.2.21) and returns the lowest-error
    /// block found with its reconstruction error. Uses a shared colour endpoint mode across
    /// partitions (the simplest legal multi-partition layout). For each partition count it scores
    /// all 1024 partition seeds by a cheap endpoint-fit metric, then runs the full weight-grid /
    /// range search only on the best few seeds.
    /// </summary>
    private static UInt128 TryEncodeMultiPartition(ReadOnlySpan<RgbaColor> texels, Footprint footprint, out long bestErrorOut)
    {
        bestErrorOut = long.MaxValue;
        UInt128 bestBlock = default;

        Span<long> seedErrors = stackalloc long[SeedFinalists];
        Span<int> seedFinalists = stackalloc int[SeedFinalists];

        // The candidate modes depend only on block content, not on the partition count or seed, so
        // select them once here rather than per seed.
        Span<ColorEndpointMode> candidateModes = stackalloc ColorEndpointMode[MaxCandidateModes];
        int modeCount = SelectCandidateModes(texels, candidateModes);
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

            int finalistCount = SelectSeedFinalists(texels, footprint, partitionCount, seedFinalists, seedErrors);

            for (int f = 0; f < finalistCount; f++)
            {
                int seed = seedFinalists[f];
                UInt128 block = EncodeMultiPartitionSeed(texels, footprint, partitionCount, seed, candidateModes, out long error);
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
    /// for final quality that ignores weight quantisation) and returns the indices of the
    /// <see cref="SeedFinalists"/> lowest-error seeds. Seeds whose hash leaves a subset empty are
    /// skipped — an empty subset wastes endpoint budget.
    /// </summary>
    private static int SelectSeedFinalists(
        ReadOnlySpan<RgbaColor> texels, Footprint footprint, int partitionCount, Span<int> finalists, Span<long> finalistErrors)
    {
        finalistErrors.Fill(long.MaxValue);
        int count = 0;

        for (int seed = 0; seed < PartitionSeedCount; seed++)
        {
            ReadOnlySpan<int> assignment = Partition.GetASTCPartition(footprint, partitionCount, seed).Assignment;
            long error = PartitionFitError(texels, assignment, partitionCount);
            if (error < 0)
            {
                continue; // an empty subset; skip this seed.
            }

            if (TryInsertFinalist(finalists, finalistErrors, seed, error))
            {
                count = Math.Min(count + 1, SeedFinalists);
            }
        }

        return count;
    }

    /// <summary>
    /// Inserts <paramref name="seed"/>/<paramref name="error"/> into the fixed-size sorted finalist
    /// lists (ascending by error), evicting the current worst if the list is full. Returns false if
    /// the error does not beat the worst finalist.
    /// </summary>
    private static bool TryInsertFinalist(Span<int> finalists, Span<long> finalistErrors, int seed, long error)
    {
        if (error >= finalistErrors[SeedFinalists - 1])
        {
            return false;
        }

        int pos = SeedFinalists - 1;
        while (pos > 0 && finalistErrors[pos - 1] > error)
        {
            finalistErrors[pos] = finalistErrors[pos - 1];
            finalists[pos] = finalists[pos - 1];
            pos--;
        }

        finalistErrors[pos] = error;
        finalists[pos] = seed;
        return true;
    }

    /// <summary>
    /// Returns the total squared distance of each texel from its subset's fitted endpoint line, or
    /// -1 if any subset is empty under this assignment.
    /// </summary>
    private static long PartitionFitError(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<int> assignment, int partitionCount)
    {
        Span<RgbaColor> subsetLow = stackalloc RgbaColor[MaxPartitions];
        Span<RgbaColor> subsetHigh = stackalloc RgbaColor[MaxPartitions];
        if (!EndpointFitter.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh))
        {
            return -1;
        }

        // Expand each subset's endpoints to int channels once, rather than per texel.
        Span<int> low = stackalloc int[ChannelCount * MaxPartitions];
        Span<int> high = stackalloc int[ChannelCount * MaxPartitions];
        for (int p = 0; p < partitionCount; p++)
        {
            StoreChannels(subsetLow[p], low.Slice(p * ChannelCount, ChannelCount));
            StoreChannels(subsetHigh[p], high.Slice(p * ChannelCount, ChannelCount));
        }

        long error = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            int p = assignment[t];
            Span<int> pLow = low.Slice(p * ChannelCount, ChannelCount);
            Span<int> pHigh = high.Slice(p * ChannelCount, ChannelCount);
            int weight = ProjectWeight(texels[t], pLow, pHigh);
            error += ReconstructionError(texels[t], pLow, pHigh, weight);
        }

        return error;
    }

    /// <summary>
    /// Encodes the block for a fixed partition count and seed: fits per-subset endpoints, searches
    /// shared weight grid / range / colour range for the lowest reconstruction error, and assembles
    /// the multi-partition block. Returns the block and its error (<see cref="long.MaxValue"/> if no
    /// legal configuration fits).
    /// </summary>
    private static UInt128 EncodeMultiPartitionSeed(
        ReadOnlySpan<RgbaColor> texels, Footprint footprint, int partitionCount, int seed,
        ReadOnlySpan<ColorEndpointMode> candidateModes, out long bestErrorOut)
    {
        bestErrorOut = long.MaxValue;
        ReadOnlySpan<int> assignment = Partition.GetASTCPartition(footprint, partitionCount, seed).Assignment;

        Span<RgbaColor> subsetLow = stackalloc RgbaColor[MaxPartitions];
        Span<RgbaColor> subsetHigh = stackalloc RgbaColor[MaxPartitions];
        if (!EndpointFitter.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh))
        {
            return default;
        }

        int texelCount = footprint.PixelCount;
        var block = new BlockInput(
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
            perTexelWeights: stackalloc int[texelCount]);

        // All partitions share one colour endpoint mode (the simplest legal multi-partition layout);
        // SearchConfigs keeps only those that fit the colour-value budget (see MaxColorValuesPerBlock).
        if (!SearchConfigs(in block, candidateModes, MultiColorStartBit, in scratch, out BestConfig best, out bestErrorOut))
        {
            return default;
        }

        int bestGridCount = best.GridWidth * best.GridHeight;
        return AssembleMultiPartition(
            partitionCount, seed, best.Mode, best.GridWidth, best.GridHeight, best.WeightRange, best.ColorRange,
            scratch.BestColorValues[..best.ColorValueCount], scratch.BestGridWeights[..bestGridCount]);
    }

    private static UInt128 Assemble(in BestConfig config, ReadOnlySpan<int> colorValues, ReadOnlySpan<int> quantWeights)
    {
        ushort blockMode = BlockModeEncoder.Encode(config.GridWidth, config.GridHeight, config.WeightRange, isDualPlane: false);

        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, BlockModeStartBit, BlockModeBits);
        builder.PlaceLowField(SinglePartitionField, PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)config.Mode, CemStartBit, CemBits);

        var colorStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(config.ColorRange, colorValues, ref colorStream);
        builder.PlaceColorData(colorStream, ColorStartBit);

        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(config.WeightRange, quantWeights, ref weightStream);
        builder.PlaceWeightData(weightStream);

        return builder.Build();
    }

    /// <summary>
    /// Assembles a multi-partition block (spec §C.2.10): partition count, 10-bit seed, a
    /// shared-CEM marker of 0, the shared colour endpoint mode, the concatenated per-partition
    /// colour values from bit 29, and the weight data at the top.
    /// </summary>
    private static UInt128 AssembleMultiPartition(
        int partitionCount,
        int seed,
        ColorEndpointMode mode,
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
        builder.PlaceLowField((ulong)(partitionCount - 1), PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)seed, PartitionSeedStartBit, PartitionSeedBits);
        builder.PlaceLowField(0, SharedCemMarkerStartBit, SharedCemMarkerBits);
        builder.PlaceLowField((ulong)mode, SharedCemStartBit, CemBits);

        var colorStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(colorRange, colorValues, ref colorStream);
        builder.PlaceColorData(colorStream, MultiColorStartBit);

        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(weightRange, quantWeights, ref weightStream);
        builder.PlaceWeightData(weightStream);

        return builder.Build();
    }
}
