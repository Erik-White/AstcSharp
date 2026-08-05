using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Colour-space geometry and codec operations the per-block encoder builds on
/// </summary>
internal static class ColorGeometry
{
    // RGBA channels per texel.
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // The maximum weight value the decoder interpolates with (spec §C.2.19): weights span [0, 64].
    private const int MaxWeight = 64;

    // Channel-mask covering all four RGBA channels, used for whole-line weight projection.
    private const int AllChannelsMask = 0b1111;

    /// <summary>
    /// Encodes the endpoint pair for <paramref name="mode"/> into <paramref name="colorValues"/>,
    /// then decodes those values back through the real <see cref="EndpointCodec"/> to recover the
    /// effective endpoints the decoder will interpolate. Routing the measurement through the actual
    /// decode path means any imperfection in an endpoint encoding only shows up as higher error
    /// (the mode loses the search) and can never produce an illegal block.
    /// </summary>
    public static void EncodeAndDecodeEndpoints<TTexel, TStrategy>(
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
    public static int ProjectWeight<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
        => ProjectWeightMasked<TTexel, TStrategy>(texel, low, high, AllChannelsMask);

    /// <summary>
    /// Projects a texel onto the endpoint line using only the channels selected by
    /// <paramref name="channelMask"/> (bit <c>c</c> set = include channel <c>c</c>), returning the
    /// nearest weight in [0, 64]. Dual-plane fitting uses this to weight the two planes from disjoint
    /// channel sets; whole-line projection passes the all-channels mask.
    /// </summary>
    public static int ProjectWeightMasked<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int channelMask)
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
    public static long ReconstructionError<TTexel, TStrategy>(TTexel texel, ReadOnlySpan<int> low, ReadOnlySpan<int> high, int weight)
        where TTexel : unmanaged
        where TStrategy : struct, IColorSpaceStrategy<TTexel>
        => ReconstructionErrorDualPlane<TTexel, TStrategy>(texel, low, high, weight, dualPlaneChannel: -1, secondaryWeight: 0);

    /// <summary>
    /// Sum-of-squared error for a dual-plane texel. The channel named by
    /// <paramref name="dualPlaneChannel"/> interpolates with <paramref name="secondaryWeight"/>, all
    /// others with <paramref name="weight"/>, mirroring the decoder's dual-plane blend
    /// (spec §C.2.20). A <paramref name="dualPlaneChannel"/> of -1 makes this the single-plane case.
    /// </summary>
    public static long ReconstructionErrorDualPlane<TTexel, TStrategy>(
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
    /// Quantises a fitted grid to the weight range, writing both the stored quantised weights (into
    /// <paramref name="quantGridWeights"/>, for the bitstream) and the decoder's effective weights
    /// (into <paramref name="effectiveGrid"/>, for reconstruction) in one pass.
    /// </summary>
    public static void QuantizeGridToEffective(
        ReadOnlySpan<double> fittedGrid, int weightRange, Span<int> quantGridWeights, Span<int> effectiveGrid)
    {
        for (int i = 0; i < fittedGrid.Length; i++)
        {
            int quant = Quantization.QuantizeWeightToRange(RoundWeight(fittedGrid[i]), weightRange);
            quantGridWeights[i] = quant;
            effectiveGrid[i] = Quantization.UnquantizeWeightFromRange(quant, weightRange);
        }
    }

    /// <summary>
    /// Rounds a fitted grid weight to the nearest integer, rounding halves away from zero to match
    /// the decoder's round-half-up infill convention (spec §C.2.18, <c>(… + 8) >> 4</c>). The
    /// default <see cref="Math.Round(double)"/> rounds halves to even, which would bias half-valued
    /// weights inconsistently against the decoder.
    /// </summary>
    private static int RoundWeight(double weight) => (int)Math.Round(weight, MidpointRounding.AwayFromZero);
}
