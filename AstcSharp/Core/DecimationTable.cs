using System.Runtime.CompilerServices;

namespace AstcSharp.Core;

/// <summary>
/// Pre-computed weight infill data for a specific (footprint, weightGridX, weightGridY) combination.
/// Stores bilinear interpolation indices and factors in a transposed layout for SIMD-friendly access.
/// </summary>
internal sealed class DecimationInfo
{
    public readonly int TexelCount;
    public readonly int PaddedTexelCount; // rounded up to multiple of 4

    // Transposed layout: [contribution * PaddedTexelCount + texel]
    // 4 contributions per texel (bilinear interpolation from weight grid).
    // For edge texels where some grid points are out of bounds, factor is 0 and index is 0.
    // Sequential texels are adjacent in memory, enabling single Vector128 loads.
    public readonly int[] WeightIndices;  // size: 4 * PaddedTexelCount
    public readonly int[] WeightFactors;  // size: 4 * PaddedTexelCount

    public DecimationInfo(int texelCount, int paddedTexelCount, int[] weightIndices, int[] weightFactors)
    {
        TexelCount = texelCount;
        PaddedTexelCount = paddedTexelCount;
        WeightIndices = weightIndices;
        WeightFactors = weightFactors;
    }
}

/// <summary>
/// Caches pre-computed DecimationInfo tables and provides SIMD-optimized weight infill.
/// For each unique (footprint, gridX, gridY) combination, the bilinear interpolation
/// indices and factors are computed once and reused for every block with that configuration.
/// Uses a flat array indexed by (footprintType, gridX, gridY) for O(1) lookup.
/// </summary>
internal static class DecimationTable
{
    // Grid dimensions range from 2 to 12 inclusive
    private const int GridMin = 2;
    private const int GridRange = 11; // 12 - 2 + 1
    private const int FootprintCount = 14;
    private static readonly DecimationInfo?[] _table = new DecimationInfo?[FootprintCount * GridRange * GridRange];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DecimationInfo Get(Footprint footprint, int gridX, int gridY)
    {
        int index = (int)footprint.Type * (GridRange * GridRange) + (gridX - GridMin) * GridRange + (gridY - GridMin);
        var decimationInfo = _table[index];
        if (decimationInfo is null)
        {
            decimationInfo = Compute(footprint.Width, footprint.Height, gridX, gridY);
            _table[index] = decimationInfo;
        }
        return decimationInfo;
    }

    private static int GetScaleFactorD(int blockDimensions)
    {
        return (int)((1024f + (blockDimensions >> 1)) / (blockDimensions - 1));
    }

    private static DecimationInfo Compute(int footprintWidth, int footprintHeight, int gridWidth, int gridHeight)
    {
        int texelCount = footprintWidth * footprintHeight;
        int paddedTexelCount = (texelCount + 3) & ~3;

        var indices = new int[4 * paddedTexelCount];
        var factors = new int[4 * paddedTexelCount];

        int scaleHorizontal = GetScaleFactorD(footprintWidth);
        int scaleVertical = GetScaleFactorD(footprintHeight);
        int gridLimit = gridWidth * gridHeight;
        int maxGridX = gridWidth - 1;
        int maxGridY = gridHeight - 1;

        int texelIndex = 0;
        for (int texelY = 0; texelY < footprintHeight; ++texelY)
        {
            int scaledY = scaleVertical * texelY;
            int gridY = (scaledY * maxGridY + 32) >> 6;
            int gridRowIndex = gridY >> 4;
            int fractionY = gridY & 0xF;

            for (int texelX = 0; texelX < footprintWidth; ++texelX)
            {
                int scaledX = scaleHorizontal * texelX;
                int gridX = (scaledX * maxGridX + 32) >> 6;
                int gridColIndex = gridX >> 4;
                int fractionX = gridX & 0xF;

                int gridPoint0 = gridColIndex + gridWidth * gridRowIndex;
                int gridPoint1 = gridPoint0 + 1;
                int gridPoint2 = gridColIndex + gridWidth * (gridRowIndex + 1);
                int gridPoint3 = gridPoint2 + 1;

                int factor3 = (fractionX * fractionY + 8) >> 4;
                int factor2 = fractionY - factor3;
                int factor1 = fractionX - factor3;
                int factor0 = 16 - fractionX - fractionY + factor3;

                // For out-of-bounds grid points, zero the factor and use index 0 (safe dummy)
                if (gridPoint3 >= gridLimit) { factor3 = 0; gridPoint3 = 0; }
                if (gridPoint2 >= gridLimit) { factor2 = 0; gridPoint2 = 0; }
                if (gridPoint1 >= gridLimit) { factor1 = 0; gridPoint1 = 0; }
                if (gridPoint0 >= gridLimit) { factor0 = 0; gridPoint0 = 0; }

                indices[0 * paddedTexelCount + texelIndex] = gridPoint0;
                indices[1 * paddedTexelCount + texelIndex] = gridPoint1;
                indices[2 * paddedTexelCount + texelIndex] = gridPoint2;
                indices[3 * paddedTexelCount + texelIndex] = gridPoint3;

                factors[0 * paddedTexelCount + texelIndex] = factor0;
                factors[1 * paddedTexelCount + texelIndex] = factor1;
                factors[2 * paddedTexelCount + texelIndex] = factor2;
                factors[3 * paddedTexelCount + texelIndex] = factor3;

                texelIndex++;
            }
        }

        // Padding slots are already zeroed (factors=0, indices=0)
        return new DecimationInfo(texelCount, paddedTexelCount, indices, factors);
    }

    /// <summary>
    /// Performs weight infill using pre-computed tables.
    /// Maps unquantized grid weights to per-texel weights via bilinear interpolation
    /// with pre-computed indices and factors.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void InfillWeights(ReadOnlySpan<int> gridWeights, DecimationInfo di, Span<int> result)
    {
        int texelCount = di.TexelCount;
        int padded = di.PaddedTexelCount;
        int[] weightIndices = di.WeightIndices;
        int[] weightFactors = di.WeightFactors;

        for (int i = 0; i < texelCount; i++)
        {
            int sum = 8;
            for (int j = 0; j < 4; j++)
            {
                int baseIdx = j * padded + i;
                sum += gridWeights[weightIndices[baseIdx]] * weightFactors[baseIdx];
            }
            result[i] = sum >> 4;
        }
    }
}
