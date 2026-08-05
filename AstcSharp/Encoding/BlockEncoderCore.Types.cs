using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Nested data and scratch types shared across <see cref="BlockEncoderCore"/>
/// </summary>
internal static partial class BlockEncoderCore
{
    /// <summary>
    /// The winning configuration of the per-block search.
    /// </summary>
    private readonly record struct BestConfig(ColorEndpointMode Mode, int GridWidth, int GridHeight, int WeightRange, int ColorRange, int ColorValueCount);

    /// <summary>
    /// A winning configuration of type <typeparamref name="TConfig"/> and its reconstruction error.
    /// Returned by the config searches, <c>null</c> means nothing legal fit.
    /// </summary>
    private readonly record struct SearchResult<TConfig>(TConfig Config, long Error);

    /// <summary>
    /// The fixed inputs of a per-block configuration search: the block's texels and footprint, the
    /// partition assignment (all-zero for single-partition), the partition count, and the fitted
    /// per-partition endpoints. Threaded as one <c>in</c> parameter through the search so the
    /// individual config methods stay readable.
    /// </summary>
    private readonly ref struct BlockInput<TTexel>(
        ReadOnlySpan<TTexel> texels,
        Footprint footprint,
        ReadOnlySpan<int> assignment,
        int partitionCount,
        ReadOnlySpan<TTexel> subsetLow,
        ReadOnlySpan<TTexel> subsetHigh)
        where TTexel : unmanaged
    {
        public ReadOnlySpan<TTexel> Texels { get; } = texels;
        public Footprint Footprint { get; } = footprint;
        public ReadOnlySpan<int> Assignment { get; } = assignment;
        public int PartitionCount { get; } = partitionCount;
        public ReadOnlySpan<TTexel> SubsetLow { get; } = subsetLow;
        public ReadOnlySpan<TTexel> SubsetHigh { get; } = subsetHigh;
    }

    /// <summary>
    /// Reusable buffers for one block's configuration search, allocated once on the caller's stack
    /// frame and threaded through <see cref="SearchConfigs"/>. <c>Candidate*</c> hold the config
    /// currently under test; <c>Best*</c> retain the lowest-error config found so far. The remainder
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
        Span<int> perTexelWeights,
        Span<int> altColorValues,
        Span<int> altEffectiveLow,
        Span<int> altEffectiveHigh,
        Span<int> altIdealWeights,
        Span<double> altFittedGrid,
        Span<int> altGridWeights,
        Span<int> altEffectiveGrid,
        Span<int> altPerTexelWeights)
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

        // Iterative endpoint/weight refinement working buffers
        public Span<int> AltColorValues { get; } = altColorValues;
        public Span<int> AltEffectiveLow { get; } = altEffectiveLow;
        public Span<int> AltEffectiveHigh { get; } = altEffectiveHigh;
        public Span<int> AltIdealWeights { get; } = altIdealWeights;
        public Span<double> AltFittedGrid { get; } = altFittedGrid;
        public Span<int> AltGridWeights { get; } = altGridWeights;
        public Span<int> AltEffectiveGrid { get; } = altEffectiveGrid;
        public Span<int> AltPerTexelWeights { get; } = altPerTexelWeights;
    }
}
