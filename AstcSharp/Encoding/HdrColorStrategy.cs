using System.Runtime.CompilerServices;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// The HDR (<see cref="RgbaHdrColor"/>, 16-bit LNS-domain channels) implementation of
/// <see cref="IColorSpaceStrategy{TTexel}"/>: principal-axis endpoint fitting via
/// <see cref="HdrEndpointFitter"/>, HDR endpoint encoding via <see cref="HdrEndpointEncoder"/>, and
/// the decoder's HDR interpolation (<see cref="Interpolation.BlendWeighted"/>, spec §C.2.19) for
/// reconstruction.
/// </summary>
/// <remarks>
/// <para>
/// The whole search runs in the LNS (log) domain the HDR decoder interpolates in: texels are handed
/// in already converted from FP16 to LNS via <see cref="Fp16.ToLns"/>, endpoints decode to
/// LNS-domain channels, and error is measured there — the domain ASTC uses so squared error tracks
/// perceived HDR error rather than raw magnitude.
/// </para>
/// <para>
/// Coverage matches <see cref="HdrEndpointEncoder"/>: CEM 2 (luminance) for grey content and CEM 11
/// (RGB direct) otherwise. Both decode alpha to <see cref="Fp16.One"/>; opaque input alpha (1.0)
/// converts to that same LNS value, so opaque blocks incur no alpha error.
/// </para>
/// </remarks>
internal readonly struct HdrColorStrategy : IColorSpaceStrategy<RgbaHdrColor>
{
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // Per-channel-sample squared-error early-out target in the LNS domain. Scaled from the LDR value
    // (4 on the 0-255 byte domain) by the domain-width ratio squared (~256^2), since LNS channels
    // span the full 16-bit range. Not yet tuned against a quality metric — see the HDR encoding plan.
    private const long LnsEarlyOutPerSampleError = 4L * 256 * 256;

    public long EarlyOutPerSampleError => LnsEarlyOutPerSampleError;

    public (RgbaHdrColor Low, RgbaHdrColor High) Fit(ReadOnlySpan<RgbaHdrColor> texels)
        => HdrEndpointFitter.Fit(texels);

    public bool FitSubsets(
        ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<RgbaHdrColor> subsetLow, Span<RgbaHdrColor> subsetHigh)
        => HdrEndpointFitter.FitSubsets(texels, assignment, partitionCount, subsetLow, subsetHigh);

    /// <summary>
    /// Picks the HDR endpoint modes worth trying, cheapest-content-fit first. Grey blocks add the
    /// luminance mode (CEM 2, 2 values); the RGB-direct mode (CEM 11) always applies and is the only
    /// choice for chromatic content. Both force alpha to 1.0, so alpha does not affect the choice.
    /// </summary>
    public int SelectCandidateModes(ReadOnlySpan<RgbaHdrColor> texels, Span<ColorEndpointMode> modes)
    {
        bool grey = true;
        foreach (RgbaHdrColor texel in texels)
        {
            grey &= texel.R == texel.G && texel.G == texel.B;
        }

        int count = 0;
        if (grey)
        {
            modes[count++] = ColorEndpointMode.HdrLumaLargeRange;
        }

        modes[count++] = ColorEndpointMode.HdrRgbDirect;
        return count;
    }

    public void EncodeEndpoints(ColorEndpointMode mode, RgbaHdrColor low, RgbaHdrColor high, int colorRange, Span<int> colorValues)
        => HdrEndpointEncoder.Encode(mode, low, high, colorRange, colorValues);

    public void StoreEffectiveChannels(in ColorEndpointPair pair, Span<int> low, Span<int> high)
    {
        StoreChannels(pair.HdrLow, low);
        StoreChannels(pair.HdrHigh, high);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreChannels(RgbaHdrColor endpoint, Span<int> channels)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            channels[channel] = endpoint.GetChannel(channel);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetChannel(RgbaHdrColor texel, int channel) => texel.GetChannel(channel);

    /// <summary>
    /// HDR reconstruction: the decoder blends the two 16-bit LNS endpoint channels directly
    /// (spec §C.2.19), with no 8→16 expansion (endpoints are already 16-bit), matching
    /// <see cref="BlockDecoding.HdrPixelWriter"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Reconstruct(int low, int high, int weight) => Interpolation.BlendWeighted(low, high, weight);
}
