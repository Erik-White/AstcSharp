using System.Runtime.CompilerServices;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// The LDR (<see cref="RgbaColor"/>, byte channels) implementation. Principal-axis endpoint fitting via
/// <see cref="EndpointFitter"/>, LDR endpoint encoding via <see cref="EndpointEncoder"/>, and the
/// decoder's LDR interpolation (spec §C.2.19) for reconstruction.
/// </summary>
internal readonly struct LdrColorStrategy : IColorSpaceStrategy<RgbaColor>
{
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // Once a config reconstructs the block to within this mean squared error per channel sample, the
    // costlier searches cannot meaningfully improve it (byte domain).
    private const long MaxPerSampleSquaredError = 4;

    public long EarlyOutPerSampleError => MaxPerSampleSquaredError;

    public (RgbaColor Low, RgbaColor High) Fit(ReadOnlySpan<RgbaColor> texels)
        => EndpointFitter.Fit(texels);

    public bool FitSubsets(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<RgbaColor> subsetLow, Span<RgbaColor> subsetHigh)
        => EndpointFitter.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh);

    /// <summary>
    /// Picks the colour endpoint modes worth trying for a block, cheapest-content-fit first.
    /// Grey blocks add the luminance modes (2 values); opaque blocks add the RGB modes (no alpha);
    /// blocks with varying alpha or chroma fall back to the full RGBA modes. Each "direct" mode is
    /// paired with its "base+offset" sibling (fewer bits when the endpoints are close) and, for RGB,
    /// a "base+scale" sibling (fewer values still — 4 vs 6 — when the dark endpoint is a uniformly
    /// darkened version of the bright one, e.g. lit surfaces ramping toward black).
    /// </summary>
    public int SelectCandidateModes(ReadOnlySpan<RgbaColor> texels, Span<ColorEndpointMode> modes)
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
            modes[count++] = ColorEndpointMode.LdrRgbBaseScale;
        }

        // The full RGBA modes always apply and are the only legal choice when alpha varies.
        modes[count++] = ColorEndpointMode.LdrRgbaDirect;
        modes[count++] = ColorEndpointMode.LdrRgbaBaseOffset;
        modes[count++] = ColorEndpointMode.LdrRgbBaseScaleTwoA;
        return count;
    }

    public void EncodeEndpoints(ColorEndpointMode mode, RgbaColor low, RgbaColor high, int colorRange, Span<int> colorValues)
        => EndpointEncoder.Encode(mode, low, high, colorRange, colorValues);

    public void StoreEffectiveChannels(in ColorEndpointPair pair, Span<int> low, Span<int> high)
    {
        StoreChannels(pair.LdrLow, low);
        StoreChannels(pair.LdrHigh, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreChannels(RgbaColor endpoint, Span<int> channels)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            channels[channel] = endpoint.GetChannel(channel);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetChannel(RgbaColor texel, int channel) => texel.GetChannel(channel);

    public RgbaColor EndpointFromChannels(ReadOnlySpan<double> channels)
        => new(ClampChannel(channels[0]), ClampChannel(channels[1]), ClampChannel(channels[2]), ClampChannel(channels[3]));

    private static byte ClampChannel(double value)
        => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, byte.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Reconstruct(int low, int high, int weight)
        => (Interpolation.BlendLdrReplicated(low, high, weight) >> 8) & byte.MaxValue;
}
