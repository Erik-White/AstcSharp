using AstcSharp.Core;

namespace AstcSharp.Encoding;

/// <summary>
/// Computes weight-grid values from ideal per-texel weights — the inverse of the bilinear infill
/// in <see cref="DecimationTable.InfillWeights"/> (ASTC spec §C.2.18). The infill is a linear map
/// <c>w = A·g</c> (each texel weight is a bilinear blend of four grid weights), so recovering the
/// grid <c>g</c> from desired texel weights <c>w</c> is a least-squares problem. The fit starts
/// from the factor-weighted scatter (the transpose <c>Aᵀw</c>, normalised) and refines it with a
/// few Landweber iterations, which converge for this well-conditioned averaging operator.
/// </summary>
internal static class DecimationFit
{
    // Each texel is a bilinear blend of the four surrounding grid points (spec §C.2.18).
    private const int CornersPerTexel = 4;

    // The four bilinear factors of a texel sum to 16 (spec §C.2.18); the infill divides by 16.
    private const double FactorScale = 16.0;

    // Number of Landweber refinement passes after the initial scatter estimate.
    private const int RefinementIterations = 4;

    // Grid weights live in the [0, 64] interpolation domain (spec §C.2.19).
    private const int MaxWeight = 64;

    /// <summary>
    /// Returns grid weights (length = grid point count) in the [0, 64] weight domain that best
    /// reproduce <paramref name="idealTexelWeights"/> when run through the decoder's infill.
    /// </summary>
    public static void Fit(ReadOnlySpan<int> idealTexelWeights, DecimationInfo decimationInfo, int gridPointCount, Span<double> gridWeights)
    {
        ReadOnlySpan<int> indices = decimationInfo.WeightIndices;
        ReadOnlySpan<int> factors = decimationInfo.WeightFactors;
        int texelCount = decimationInfo.TexelCount;

        // Per-grid-point factor totals, used to normalise the scatter and the residual updates.
        Span<double> gridFactorTotals = stackalloc double[gridPointCount];
        AccumulateFactorTotals(indices, factors, texelCount, gridFactorTotals);

        // Initial estimate: each grid weight is the factor-weighted average of the texel weights it
        // influences (Aᵀw, normalised per grid point).
        ScatterAverage(idealTexelWeights, indices, factors, texelCount, gridFactorTotals, gridWeights);

        // Landweber refinement: reconstruct, scatter the residual back, repeat. Scratch buffers are
        // allocated once here and reused across iterations (each is fully overwritten per pass).
        Span<double> reconstructed = stackalloc double[texelCount];
        Span<double> residual = stackalloc double[texelCount];
        Span<double> correction = stackalloc double[gridPointCount];
        for (int iteration = 0; iteration < RefinementIterations; iteration++)
        {
            Reconstruct(gridWeights, indices, factors, texelCount, reconstructed);
            for (int t = 0; t < texelCount; t++)
            {
                residual[t] = idealTexelWeights[t] - reconstructed[t];
            }

            ApplyResidual(residual, indices, factors, texelCount, gridFactorTotals, correction, gridWeights);
        }

        for (int p = 0; p < gridPointCount; p++)
        {
            gridWeights[p] = Math.Clamp(gridWeights[p], 0, MaxWeight);
        }
    }

    private static void AccumulateFactorTotals(ReadOnlySpan<int> indices, ReadOnlySpan<int> factors, int texelCount, Span<double> gridFactorTotals)
    {
        for (int corner = 0; corner < CornersPerTexel; corner++)
        {
            int slot = corner * texelCount;
            for (int t = 0; t < texelCount; t++)
            {
                int factor = factors[slot + t];
                if (factor != 0)
                {
                    gridFactorTotals[indices[slot + t]] += factor;
                }
            }
        }
    }

    private static void ScatterAverage(
        ReadOnlySpan<int> texelValues,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<int> factors,
        int texelCount,
        ReadOnlySpan<double> gridFactorTotals,
        Span<double> gridWeights)
    {
        gridWeights.Clear();
        for (int corner = 0; corner < CornersPerTexel; corner++)
        {
            int slot = corner * texelCount;
            for (int t = 0; t < texelCount; t++)
            {
                int factor = factors[slot + t];
                if (factor != 0)
                {
                    gridWeights[indices[slot + t]] += factor * texelValues[t];
                }
            }
        }

        NormaliseByFactorTotals(gridWeights, gridFactorTotals);
    }

    private static void ApplyResidual(
        ReadOnlySpan<double> residual,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<int> factors,
        int texelCount,
        ReadOnlySpan<double> gridFactorTotals,
        Span<double> correction,
        Span<double> gridWeights)
    {
        correction.Clear();
        for (int corner = 0; corner < CornersPerTexel; corner++)
        {
            int slot = corner * texelCount;
            for (int t = 0; t < texelCount; t++)
            {
                int factor = factors[slot + t];
                if (factor != 0)
                {
                    correction[indices[slot + t]] += factor * residual[t];
                }
            }
        }

        NormaliseByFactorTotals(correction, gridFactorTotals);
        for (int p = 0; p < gridWeights.Length; p++)
        {
            gridWeights[p] += correction[p];
        }
    }

    private static void Reconstruct(
        ReadOnlySpan<double> gridWeights,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<int> factors,
        int texelCount,
        Span<double> reconstructed)
    {
        for (int t = 0; t < texelCount; t++)
        {
            double sum = 0;
            for (int corner = 0; corner < CornersPerTexel; corner++)
            {
                int slot = corner * texelCount;
                sum += gridWeights[indices[slot + t]] * factors[slot + t];
            }

            reconstructed[t] = sum / FactorScale;
        }
    }

    private static void NormaliseByFactorTotals(Span<double> gridValues, ReadOnlySpan<double> gridFactorTotals)
    {
        for (int p = 0; p < gridValues.Length; p++)
        {
            // A grid point with no influence (total 0) keeps its current value; it does not affect
            // any texel, so its weight is arbitrary.
            if (gridFactorTotals[p] > 0)
            {
                gridValues[p] /= gridFactorTotals[p];
            }
        }
    }
}
