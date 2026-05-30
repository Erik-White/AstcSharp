using AstcSharp.BiseEncoding;
using AstcSharp.BiseEncoding.Quantize;
using AstcSharp.BlockDecoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes a single-partition LDR block with an identity weight grid (one weight per texel) and
/// the RGBA-direct colour endpoint mode (CEM 12, spec §C.2.14). Endpoints are the per-channel
/// bounding box of the block's texels; per-texel weights are the projection of each texel onto the
/// endpoint line. The weight range is chosen by trying each that fits the 128-bit budget and
/// keeping the one with the lowest reconstruction error.
/// </summary>
internal static class LdrBlockEncoder
{
    private const ColorEndpointMode Mode = ColorEndpointMode.LdrRgbaDirect;
    private const int ColorValueCount = 8; // r0,r1,g0,g1,b0,b1,a0,a1
    private const int PartitionCountField = 0; // single partition: (partitionCount - 1)
    private const int CemStartBit = 13;
    private const int CemBits = 4;
    private const int ColorStartBit = 17;

    // Candidate weight ranges to try, richest first (spec §C.2.7 Table 23 weight ranges).
    private static ReadOnlySpan<int> WeightRangeCandidates => [31, 23, 19, 15, 11, 9, 7, 5, 4, 3, 2, 1];

    // The maximum weight value the decoder interpolates with (spec §C.2.19): weights span [0, 64].
    private const int MaxWeight = 64;

    /// <summary>
    /// Returns true if a single-partition identity-grid block can encode this footprint, i.e. the
    /// texel count does not exceed the 64-weight limit (spec §C.2.11).
    /// </summary>
    public static bool CanEncode(Footprint footprint) => footprint.PixelCount <= 64;

    /// <summary>
    /// Encodes <paramref name="texels"/> (one <see cref="RgbaColor"/> per footprint texel, raster
    /// order) into a 128-bit block.
    /// </summary>
    public static UInt128 Encode(ReadOnlySpan<RgbaColor> texels, Footprint footprint)
    {
        (RgbaColor boxLow, RgbaColor boxHigh) = FitEndpoints(texels);

        int numWeights = footprint.PixelCount;
        Span<int> bestQuantWeights = stackalloc int[numWeights];
        Span<int> candidateWeights = stackalloc int[numWeights];
        Span<int> bestColorValues = stackalloc int[ColorValueCount];
        Span<int> candidateColorValues = stackalloc int[ColorValueCount];

        long bestError = long.MaxValue;
        int bestWeightRange = 0;
        int bestColorRange = 0;

        foreach (int weightRange in WeightRangeCandidates)
        {
            if (!BlockModeEncoder.TryEncode(footprint.Width, footprint.Height, weightRange, isDualPlane: false, out _))
            {
                continue;
            }

            int weightBitCount = BoundedIntegerSequenceCodec.GetBitCountForRange(numWeights, weightRange);

            // Spec §C.2.11: the total weight bit count must fall in [24, 96] for a legal block.
            if (weightBitCount is < 24 or > 96)
            {
                continue;
            }

            int maxColorBits = 128 - weightBitCount - ColorStartBit;
            if (!BlockModeDecoder.TryResolveColorEncoding(ColorValueCount, maxColorBits, out int colorRange, out _))
            {
                continue;
            }

            long error = TryEncodeWithRanges(
                texels, boxLow, boxHigh, weightRange, colorRange, candidateColorValues, candidateWeights);

            if (error < bestError)
            {
                bestError = error;
                bestWeightRange = weightRange;
                bestColorRange = colorRange;
                candidateColorValues.CopyTo(bestColorValues);
                candidateWeights.CopyTo(bestQuantWeights);

                if (bestError == 0)
                {
                    break;
                }
            }
        }

        if (bestWeightRange == 0)
        {
            throw new InvalidOperationException(
                $"No legal single-partition encoding fits footprint {footprint.Width}x{footprint.Height}.");
        }

        return Assemble(footprint, bestWeightRange, bestColorRange, bestColorValues, bestQuantWeights);
    }

    /// <summary>
    /// Quantises endpoints and per-texel weights for the given ranges, fills
    /// <paramref name="colorValues"/> and <paramref name="quantWeights"/>, and returns the
    /// sum-of-squared reconstruction error against the decoder's interpolation.
    /// </summary>
    private static long TryEncodeWithRanges(
        ReadOnlySpan<RgbaColor> texels,
        RgbaColor boxLow,
        RgbaColor boxHigh,
        int weightRange,
        int colorRange,
        Span<int> colorValues,
        Span<int> quantWeights)
    {
        // Quantise the endpoint channels, then unquantise to the values the decoder will actually
        // interpolate with (the quantise round-trip is lossy).
        Span<int> effectiveLow = stackalloc int[4];
        Span<int> effectiveHigh = stackalloc int[4];
        QuantizeEndpoints(boxLow, boxHigh, colorRange, colorValues, effectiveLow, effectiveHigh);

        long error = 0;
        for (int i = 0; i < texels.Length; i++)
        {
            int idealWeight = ProjectWeight(texels[i], effectiveLow, effectiveHigh);
            int quantWeight = Quantization.QuantizeWeightToRange(idealWeight, weightRange);
            int effectiveWeight = Quantization.UnquantizeWeightFromRange(quantWeight, weightRange);
            quantWeights[i] = quantWeight;
            error += ReconstructionError(texels[i], effectiveLow, effectiveHigh, effectiveWeight);
        }

        return error;
    }

    /// <summary>
    /// Stores RGBA-direct colour values for the bounding box: interleaved (low, high) per channel
    /// (spec §C.2.14 mode 12). Because each low channel is &lt;= its high channel and quantisation
    /// is monotonic, the decoder's "blue contract" swap (triggered when the high triple is dimmer)
    /// never fires, so this is the exact inverse of the decode path.
    /// </summary>
    private static void QuantizeEndpoints(
        RgbaColor boxLow,
        RgbaColor boxHigh,
        int colorRange,
        Span<int> colorValues,
        Span<int> effectiveLow,
        Span<int> effectiveHigh)
    {
        ReadOnlySpan<byte> low = [boxLow.R, boxLow.G, boxLow.B, boxLow.A];
        ReadOnlySpan<byte> high = [boxHigh.R, boxHigh.G, boxHigh.B, boxHigh.A];

        for (int channel = 0; channel < 4; channel++)
        {
            int quantLow = Quantization.QuantizeCEValueToRange(low[channel], colorRange);
            int quantHigh = Quantization.QuantizeCEValueToRange(high[channel], colorRange);
            colorValues[channel * 2] = quantLow;
            colorValues[(channel * 2) + 1] = quantHigh;
            effectiveLow[channel] = Quantization.UnquantizeCEValueFromRange(quantLow, colorRange);
            effectiveHigh[channel] = Quantization.UnquantizeCEValueFromRange(quantHigh, colorRange);
        }
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
        for (int channel = 0; channel < 4; channel++)
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
        for (int channel = 0; channel < 4; channel++)
        {
            int reconstructed = (Interpolation.BlendLdrReplicated(low[channel], high[channel], weight) >> 8) & 0xFF;
            int diff = reconstructed - pixel[channel];
            error += (long)diff * diff;
        }

        return error;
    }

    /// <summary>
    /// Fits the endpoint pair to the texel cloud by least-squares: the endpoints lie on the
    /// principal axis (the direction of greatest variance) through the mean, extended to span the
    /// texels' projections. This tracks correlated and anti-correlated channel variation that a
    /// per-channel bounding box would mis-orient. Endpoints are clamped to the [0, 255] byte range.
    /// </summary>
    private static (RgbaColor Low, RgbaColor High) FitEndpoints(ReadOnlySpan<RgbaColor> texels)
    {
        Span<double> mean = stackalloc double[4];
        foreach (RgbaColor texel in texels)
        {
            mean[0] += texel.R; mean[1] += texel.G; mean[2] += texel.B; mean[3] += texel.A;
        }

        for (int c = 0; c < 4; c++)
        {
            mean[c] /= texels.Length;
        }

        Span<double> axis = stackalloc double[4];
        PrincipalAxis(texels, mean, axis);

        // Project each texel onto the axis; the min/max projections bound the endpoints.
        double minProj = double.MaxValue;
        double maxProj = double.MinValue;
        foreach (RgbaColor texel in texels)
        {
            double projection = ((texel.R - mean[0]) * axis[0])
                + ((texel.G - mean[1]) * axis[1])
                + ((texel.B - mean[2]) * axis[2])
                + ((texel.A - mean[3]) * axis[3]);
            minProj = Math.Min(minProj, projection);
            maxProj = Math.Max(maxProj, projection);
        }

        RgbaColor a = EndpointAt(mean, axis, minProj);
        RgbaColor b = EndpointAt(mean, axis, maxProj);

        // The decoder's RGBA-direct mode applies a "blue contract" swap when the second endpoint's
        // channel sum is the smaller one (spec §C.2.14 mode 12). The principal axis has an arbitrary
        // sign, so order the endpoints by channel sum to keep the high endpoint's sum >= the low's
        // and avoid triggering that branch — making this the exact inverse of the decode path.
        return ChannelSum(a) <= ChannelSum(b) ? (a, b) : (b, a);
    }

    private static int ChannelSum(RgbaColor c) => c.R + c.G + c.B;

    /// <summary>
    /// Estimates the principal axis (unit vector) of the texel cloud's covariance via power
    /// iteration. Falls back to a fixed axis when the cloud has no variance.
    /// </summary>
    private static void PrincipalAxis(ReadOnlySpan<RgbaColor> texels, ReadOnlySpan<double> mean, Span<double> axis)
    {
        // Covariance matrix (4x4, symmetric) of the centred texels. The centred-texel vector and the
        // power-iteration scratch are allocated once and reused; both are fully overwritten before
        // use each iteration, so hoisting them out of the loops avoids per-iteration stack growth.
        Span<double> cov = stackalloc double[16];
        Span<double> d = stackalloc double[4];
        foreach (RgbaColor texel in texels)
        {
            d[0] = texel.R - mean[0]; d[1] = texel.G - mean[1]; d[2] = texel.B - mean[2]; d[3] = texel.A - mean[3];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    cov[(i * 4) + j] += d[i] * d[j];
                }
            }
        }

        // Power iteration from a non-degenerate start vector.
        Span<double> next = stackalloc double[4];
        axis[0] = 1; axis[1] = 1; axis[2] = 1; axis[3] = 1;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            for (int i = 0; i < 4; i++)
            {
                double sum = 0;
                for (int j = 0; j < 4; j++)
                {
                    sum += cov[(i * 4) + j] * axis[j];
                }

                next[i] = sum;
            }

            double norm = Math.Sqrt((next[0] * next[0]) + (next[1] * next[1]) + (next[2] * next[2]) + (next[3] * next[3]));
            if (norm < 1e-9)
            {
                // No variance along the current estimate; keep a fixed diagonal axis.
                axis[0] = axis[1] = axis[2] = axis[3] = 0.5;
                return;
            }

            for (int i = 0; i < 4; i++)
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

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static UInt128 Assemble(
        Footprint footprint,
        int weightRange,
        int colorRange,
        ReadOnlySpan<int> colorValues,
        ReadOnlySpan<int> quantWeights)
    {
        ushort blockMode = BlockModeEncoder.Encode(footprint.Width, footprint.Height, weightRange, isDualPlane: false);

        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, startBit: 0, count: 11);
        builder.PlaceLowField(PartitionCountField, startBit: 11, count: 2);
        builder.PlaceLowField((ulong)Mode, CemStartBit, CemBits);

        var colorStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(colorRange, colorValues, ref colorStream);
        builder.PlaceColorData(colorStream, ColorStartBit);

        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(weightRange, quantWeights, ref weightStream);
        builder.PlaceWeightData(weightStream);

        return builder.Build();
    }
}
