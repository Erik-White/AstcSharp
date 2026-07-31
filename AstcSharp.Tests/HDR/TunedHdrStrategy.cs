using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Compile-time provider of an early-out threshold, so a sweep can drive
/// <see cref="BlockEncoderCore"/> with several thresholds without a mutable shared field (the search
/// bakes the strategy into a generic type argument, so each threshold needs its own type).
/// </summary>
internal interface IEarlyOutThreshold
{
    static abstract long Value { get; }
}

/// <summary>
/// Test-only <see cref="IColorSpaceStrategy{TTexel}"/> that behaves exactly like
/// <see cref="HdrColorStrategy"/> but reports the early-out threshold from
/// <typeparamref name="TThreshold"/>. Used only to measure how the threshold affects the HDR
/// encoder's quality/layout choices during tuning.
/// </summary>
internal readonly struct TunedHdrStrategy<TThreshold> : IColorSpaceStrategy<RgbaHdrColor>
    where TThreshold : struct, IEarlyOutThreshold
{
    public long EarlyOutPerSampleError => TThreshold.Value;

    public (RgbaHdrColor Low, RgbaHdrColor High) Fit(ReadOnlySpan<RgbaHdrColor> texels)
        => default(HdrColorStrategy).Fit(texels);

    public bool FitSubsets(
        ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<RgbaHdrColor> subsetLow, Span<RgbaHdrColor> subsetHigh)
        => default(HdrColorStrategy).FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh);

    public int SelectCandidateModes(ReadOnlySpan<RgbaHdrColor> texels, Span<ColorEndpointMode> modes)
        => default(HdrColorStrategy).SelectCandidateModes(texels, modes);

    public void EncodeEndpoints(ColorEndpointMode mode, RgbaHdrColor low, RgbaHdrColor high, int colorRange, Span<int> colorValues)
        => default(HdrColorStrategy).EncodeEndpoints(mode, low, high, colorRange, colorValues);

    public void StoreEffectiveChannels(in ColorEndpointPair pair, Span<int> low, Span<int> high)
        => default(HdrColorStrategy).StoreEffectiveChannels(in pair, low, high);

    public void StoreChannels(RgbaHdrColor endpoint, Span<int> channels)
        => default(HdrColorStrategy).StoreChannels(endpoint, channels);

    public int GetChannel(RgbaHdrColor texel, int channel)
        => default(HdrColorStrategy).GetChannel(texel, channel);

    public int Reconstruct(int low, int high, int weight)
        => default(HdrColorStrategy).Reconstruct(low, high, weight);
}
