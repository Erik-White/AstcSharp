using AstcSharp.ColorEncoding;

namespace AstcSharp.Encoding;

/// <summary>
/// The colour-space-specific operations of the per-block encoder search, factored out so
/// <see cref="BlockEncoderCore"/> can drive one search skeleton for both the LDR and HDR profiles.
/// Implementations are zero-size <c>readonly struct</c>s passed as a generic type argument
/// (<c>where TStrategy : struct, IColorSpaceStrategy&lt;TTexel&gt;</c>) so every call devirtualises
/// and inlines — the same idiom the decode side uses for <c>IBlockPipeline</c>/<c>IPixelWriter</c>.
/// </summary>
/// <typeparam name="TTexel">
/// The texel/endpoint value type: <see cref="Core.RgbaColor"/> for LDR (byte channels), a 16-bit
/// value type for HDR. Endpoints fitted by <see cref="Fit"/> share this type; everything the search
/// measures against is reduced to <c>int</c> channels via <see cref="StoreEffectiveChannels"/>.
/// </typeparam>
internal interface IColorSpaceStrategy<TTexel>
    where TTexel : unmanaged
{
    /// <summary>
    /// The per-channel-sample squared-error target below which the costlier multi-partition and
    /// dual-plane searches are skipped (the search's quality early-out). Expressed in the strategy's
    /// working domain — the byte domain for LDR, the wider HDR domain otherwise.
    /// </summary>
    long EarlyOutPerSampleError { get; }

    /// <summary>
    /// Fits an endpoint pair to <paramref name="texels"/> on their principal axis, ordered so the
    /// decode path is the exact inverse (no blue-contract/major-component swap fires).
    /// </summary>
    (TTexel Low, TTexel High) Fit(ReadOnlySpan<TTexel> texels);

    /// <summary>
    /// Fits an endpoint pair for each partition subset of <paramref name="assignment"/>. Returns
    /// false if any subset is empty.
    /// </summary>
    bool FitSubsets(
        ReadOnlySpan<TTexel> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<TTexel> subsetLow, Span<TTexel> subsetHigh);

    /// <summary>
    /// Picks the candidate colour endpoint modes worth trying for <paramref name="texels"/>,
    /// cheapest-content-fit first, into <paramref name="modes"/>; returns the count written.
    /// </summary>
    int SelectCandidateModes(ReadOnlySpan<TTexel> texels, Span<ColorEndpointMode> modes);

    /// <summary>
    /// Encodes the endpoint pair for <paramref name="mode"/> into quantised
    /// <paramref name="colorValues"/> — the inverse of <see cref="EndpointCodec.Decode"/> for this
    /// colour space.
    /// </summary>
    void EncodeEndpoints(ColorEndpointMode mode, TTexel low, TTexel high, int colorRange, Span<int> colorValues);

    /// <summary>
    /// Extracts the decoded endpoint pair's effective channels (the values the decoder interpolates)
    /// into <paramref name="low"/>/<paramref name="high"/>, reading the LDR or HDR side of
    /// <paramref name="pair"/> as appropriate for this colour space.
    /// </summary>
    void StoreEffectiveChannels(in ColorEndpointPair pair, Span<int> low, Span<int> high);

    /// <summary>
    /// Expands a fitted endpoint into its <c>int</c> channels — the cheap seed-scan proxy that does
    /// not route through the real codec.
    /// </summary>
    void StoreChannels(TTexel endpoint, Span<int> channels);

    /// <summary>
    /// Returns channel <paramref name="channel"/> of <paramref name="texel"/> in the working domain
    /// used for projection and error measurement.
    /// </summary>
    int GetChannel(TTexel texel, int channel);

    /// <summary>
    /// Reconstructs one channel from its endpoints and weight exactly as the decoder does for this
    /// colour space (spec §C.2.19), returning the value in the same domain as
    /// <see cref="GetChannel"/> so their difference is the reconstruction error.
    /// </summary>
    int Reconstruct(int low, int high, int weight);
}
