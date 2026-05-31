using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Fits an LDR endpoint pair to a set of texels by least squares: the endpoints lie on the
/// principal axis (the direction of greatest variance) of the texel cloud, through its mean,
/// extended to span the texels' projections. This tracks correlated and anti-correlated channel
/// variation that a per-channel bounding box would mis-orient.
/// </summary>
internal static class EndpointFitter
{
    private const int ChannelCount = BlockInfo.ChannelsPerPixel;

    // Power-iteration passes used to estimate the principal axis. The covariance is 4x4, so a
    // handful of iterations converges to the dominant eigenvector.
    private const int PrincipalAxisIterations = 8;

    // Below this vector norm the covariance has no dominant direction (a constant or near-constant
    // block). The principal-axis search then falls back to a fixed diagonal.
    private const double DegenerateAxisNorm = 1e-9;

    /// <summary>
    /// Fits an endpoint pair to <paramref name="texels"/>. Endpoints are clamped to the [0, 255]
    /// byte range and ordered so the high endpoint's RGB channel sum is at least the low's, which
    /// keeps the decoder's blue-contract swap from firing (explained inline below).
    /// </summary>
    public static (RgbaColor Low, RgbaColor High) Fit(ReadOnlySpan<RgbaColor> texels)
    {
        Span<double> mean = stackalloc double[ChannelCount];
        foreach (RgbaColor texel in texels)
        {
            mean[0] += texel.R; mean[1] += texel.G; mean[2] += texel.B; mean[3] += texel.A;
        }

        for (int c = 0; c < ChannelCount; c++)
        {
            mean[c] /= texels.Length;
        }

        Span<double> axis = stackalloc double[ChannelCount];
        PrincipalAxis(texels, mean, axis);
        (double minProjection, double maxProjection) = ProjectionExtents(texels, mean, axis);

        RgbaColor a = EndpointAt(mean, axis, minProjection);
        RgbaColor b = EndpointAt(mean, axis, maxProjection);

        // The decoder's RGBA-direct mode applies a "blue contract" swap when the second endpoint's
        // channel sum is the smaller one (spec §C.2.14 mode 12). The principal axis has an arbitrary
        // sign, so order the endpoints by channel sum to keep the high endpoint's sum >= the low's
        // and avoid triggering that branch — making this the exact inverse of the decode path.
        return ChannelSum(a) <= ChannelSum(b) ? (a, b) : (b, a);
    }

    /// <summary>
    /// Fits an endpoint pair for each partition subset of <paramref name="assignment"/> (the texels
    /// assigned to each partition index in turn).
    /// </summary>
    /// <returns>False if any partition is empty and can't be fitted</returns>
    public static bool FitSubsets(
        ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<int> assignment, int partitionCount, Span<RgbaColor> subsetLow, Span<RgbaColor> subsetHigh)
    {
        // Gather buffer reused across subsets (fully overwritten per subset before use).
        Span<RgbaColor> subset = stackalloc RgbaColor[texels.Length];
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

    /// <summary>
    /// Copies the texels assigned to partition <paramref name="partition"/> into
    /// <paramref name="subset"> in order, returning the count.</paramref>
    /// </summary>
    private static int GatherSubset(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<int> assignment, int partition, Span<RgbaColor> subset)
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

    /// <summary>
    /// Returns the smallest and largest projections of the texels onto the fitted axis.
    /// </summary>
    private static (double Min, double Max) ProjectionExtents(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<double> mean, ReadOnlySpan<double> axis)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (RgbaColor texel in texels)
        {
            double projection = ((texel.R - mean[0]) * axis[0])
                + ((texel.G - mean[1]) * axis[1])
                + ((texel.B - mean[2]) * axis[2])
                + ((texel.A - mean[3]) * axis[3]);
            min = Math.Min(min, projection);
            max = Math.Max(max, projection);
        }

        return (min, max);
    }

    /// <summary>
    /// Estimates the principal axis (unit vector) of the texel cloud's covariance via power
    /// iteration. Falls back to a fixed diagonal when the cloud has no dominant direction.
    /// </summary>
    private static void PrincipalAxis(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<double> mean, Span<double> axis)
    {
        Span<double> covariance = stackalloc double[ChannelCount * ChannelCount];
        BuildCovariance(texels, mean, covariance);
        PowerIterate(covariance, axis);
    }

    /// <summary>
    /// Accumulates the covariance matrix of the centred texels into <paramref name="covariance"/>.
    /// </summary>
    private static void BuildCovariance(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<double> mean, Span<double> covariance)
    {
        // The centred-texel vector is reused each iteration (fully overwritten before use).
        Span<double> centred = stackalloc double[ChannelCount];
        foreach (RgbaColor texel in texels)
        {
            centred[0] = texel.R - mean[0]; centred[1] = texel.G - mean[1]; centred[2] = texel.B - mean[2]; centred[3] = texel.A - mean[3];
            for (int i = 0; i < ChannelCount; i++)
            {
                for (int j = 0; j < ChannelCount; j++)
                {
                    covariance[(i * ChannelCount) + j] += centred[i] * centred[j];
                }
            }
        }
    }

    /// <summary>
    /// Finds the dominant eigenvector of <paramref name="covariance"/> by power iteration, into <paramref name="axis"/>.
    /// </summary>
    private static void PowerIterate(ReadOnlySpan<double> covariance, Span<double> axis)
    {
        // Reused each iteration (fully overwritten before use).
        Span<double> next = stackalloc double[ChannelCount];
        axis[0] = 1; axis[1] = 1; axis[2] = 1; axis[3] = 1;
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

            double norm = Math.Sqrt((next[0] * next[0]) + (next[1] * next[1]) + (next[2] * next[2]) + (next[3] * next[3]));
            if (norm < DegenerateAxisNorm)
            {
                // No variance along the current estimate; keep a fixed diagonal axis.
                axis[0] = axis[1] = axis[2] = axis[3] = 0.5;
                return;
            }

            for (int i = 0; i < ChannelCount; i++)
            {
                axis[i] = next[i] / norm;
            }
        }
    }

    private static RgbaColor EndpointAt(ReadOnlySpan<double> mean, ReadOnlySpan<double> axis, double projection)
        => new(
            ClampByte(mean[0] + (axis[0] * projection)),
            ClampByte(mean[1] + (axis[1] * projection)),
            ClampByte(mean[2] + (axis[2] * projection)),
            ClampByte(mean[3] + (axis[3] * projection)));

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, byte.MaxValue);

    private static int ChannelSum(RgbaColor c) => c.R + c.G + c.B;
}
