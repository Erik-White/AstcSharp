using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Fits an HDR endpoint pair to a set of texels by least squares, working in the LNS (log) domain
/// the HDR decoder interpolates in. Mirrors <see cref="EndpointFitter"/> (principal axis via power
/// iteration through the texel cloud's mean) but over <see cref="RgbaHdrColor"/> channels clamped to
/// the 16-bit LNS range rather than the 8-bit byte range.
/// </summary>
internal static class HdrEndpointFitter
{
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // LNS channel values occupy the full 16-bit range (5-bit exponent + 11-bit mantissa).
    private const int MaxChannel = ushort.MaxValue;

    // Power-iteration passes used to estimate the principal axis (see EndpointFitter).
    private const int PrincipalAxisIterations = 8;

    // Below this vector norm the covariance has no dominant direction (a constant or near-constant
    // block); the principal-axis search then falls back to a fixed diagonal.
    private const double DegenerateAxisNorm = 1e-9;

    /// <summary>
    /// Fits an endpoint pair to <paramref name="texels"/> (LNS-domain channels). Endpoints are
    /// clamped to the 16-bit LNS range and ordered so the high endpoint's RGB channel sum is at least
    /// the low's, matching the decoder's non-swapping paths (CEM 2's <c>v1 &gt;= v0</c> branch and
    /// CEM 11's direct sub-mode).
    /// </summary>
    public static (RgbaHdrColor Low, RgbaHdrColor High) Fit(ReadOnlySpan<RgbaHdrColor> texels)
    {
        Span<double> mean = stackalloc double[ChannelCount];
        foreach (RgbaHdrColor texel in texels)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                mean[c] += texel.GetChannel(c);
            }
        }

        for (int c = 0; c < ChannelCount; c++)
        {
            mean[c] /= texels.Length;
        }

        Span<double> axis = stackalloc double[ChannelCount];
        PrincipalAxis(texels, mean, axis);
        (double minProjection, double maxProjection) = ProjectionExtents(texels, mean, axis);

        RgbaHdrColor a = EndpointAt(mean, axis, minProjection);
        RgbaHdrColor b = EndpointAt(mean, axis, maxProjection);

        return ChannelSum(a) <= ChannelSum(b) ? (a, b) : (b, a);
    }

    /// <summary>
    /// Fits an endpoint pair for each partition subset of <paramref name="assignment"/>.
    /// </summary>
    /// <returns>False if any partition is empty and can't be fitted.</returns>
    public static bool FitSubsets(
        ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<RgbaHdrColor> subsetLow, Span<RgbaHdrColor> subsetHigh)
    {
        Span<RgbaHdrColor> subset = stackalloc RgbaHdrColor[texels.Length];
        for (int p = 0; p < partitionCount; p++)
        {
            int count = GatherSubset(texels, assignment, p, subset);
            if (count == 0)
            {
                return false;
            }

            (subsetLow[p], subsetHigh[p]) = Fit(subset[..count]);
        }

        return true;
    }

    private static int GatherSubset(ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<int> assignment, int partition, Span<RgbaHdrColor> subset)
    {
        int count = 0;
        for (int t = 0; t < texels.Length; t++)
        {
            if (assignment[t] == partition)
            {
                subset[count++] = texels[t];
            }
        }

        return count;
    }

    private static (double Min, double Max) ProjectionExtents(ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<double> mean, ReadOnlySpan<double> axis)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (RgbaHdrColor texel in texels)
        {
            double projection = 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                projection += (texel.GetChannel(c) - mean[c]) * axis[c];
            }

            min = Math.Min(min, projection);
            max = Math.Max(max, projection);
        }

        return (min, max);
    }

    private static void PrincipalAxis(ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<double> mean, Span<double> axis)
    {
        Span<double> covariance = stackalloc double[ChannelCount * ChannelCount];
        BuildCovariance(texels, mean, covariance);
        PowerIterate(covariance, axis);
    }

    private static void BuildCovariance(ReadOnlySpan<RgbaHdrColor> texels, ReadOnlySpan<double> mean, Span<double> covariance)
    {
        Span<double> centred = stackalloc double[ChannelCount];
        foreach (RgbaHdrColor texel in texels)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                centred[c] = texel.GetChannel(c) - mean[c];
            }

            for (int i = 0; i < ChannelCount; i++)
            {
                for (int j = 0; j < ChannelCount; j++)
                {
                    covariance[(i * ChannelCount) + j] += centred[i] * centred[j];
                }
            }
        }
    }

    private static void PowerIterate(ReadOnlySpan<double> covariance, Span<double> axis)
    {
        Span<double> next = stackalloc double[ChannelCount];

        // Seed from the highest-variance channel (see EndpointFitter for why a uniform seed can
        // collapse to the degenerate fallback on anti-correlated channels).
        SeedFromMaxVarianceChannel(covariance, axis);
        for (int iteration = 0; iteration < PrincipalAxisIterations; iteration++)
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                double sum = 0;
                for (int j = 0; j < ChannelCount; j++)
                {
                    sum += covariance[(i * ChannelCount) + j] * axis[j];
                }

                next[i] = sum;
            }

            double normSquared = 0;
            for (int i = 0; i < ChannelCount; i++)
            {
                normSquared += next[i] * next[i];
            }

            double norm = Math.Sqrt(normSquared);
            if (norm < DegenerateAxisNorm)
            {
                axis.Fill(0.5);
                return;
            }

            for (int i = 0; i < ChannelCount; i++)
            {
                axis[i] = next[i] / norm;
            }
        }
    }

    private static void SeedFromMaxVarianceChannel(ReadOnlySpan<double> covariance, Span<double> axis)
    {
        int maxChannel = 0;
        double maxVariance = covariance[0];
        for (int c = 1; c < ChannelCount; c++)
        {
            double variance = covariance[(c * ChannelCount) + c];
            if (variance > maxVariance)
            {
                maxVariance = variance;
                maxChannel = c;
            }
        }

        axis.Clear();
        axis[maxChannel] = 1;
    }

    private static RgbaHdrColor EndpointAt(ReadOnlySpan<double> mean, ReadOnlySpan<double> axis, double projection)
        => new(
            ClampChannel(mean[0] + (axis[0] * projection)),
            ClampChannel(mean[1] + (axis[1] * projection)),
            ClampChannel(mean[2] + (axis[2] * projection)),
            ClampChannel(mean[3] + (axis[3] * projection)));

    private static ushort ClampChannel(double value) => (ushort)Math.Clamp(Math.Round(value), 0, MaxChannel);

    private static int ChannelSum(RgbaHdrColor c) => c.R + c.G + c.B;
}
