using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests;

/// <summary>
/// Direct unit tests for <see cref="DecimationFit"/>, which recovers weight-grid values from ideal
/// per-texel weights — the least-squares inverse of <see cref="DecimationTable.InfillWeights"/>
/// (ASTC spec §C.2.18). The core invariant: fitting a grid then infilling it should reproduce the
/// ideal weights closely.
/// </summary>
public class DecimationFitTests
{
    /// <summary>
    /// Fits a grid to <paramref name="idealWeights"/>, rounds it to integer grid weights, infills
    /// back to per-texel weights, and returns the reconstruction. Mirrors how the encoder uses the
    /// fit (it later quantises the grid, but rounding alone isolates the fit's own fidelity).
    /// </summary>
    private static int[] FitThenInfill(int[] idealWeights, FootprintType footprintType, int gridWidth, int gridHeight)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        DecimationInfo decimation = DecimationTable.Get(footprint, gridWidth, gridHeight);
        int gridPointCount = gridWidth * gridHeight;

        Span<double> fittedGrid = stackalloc double[gridPointCount];
        DecimationFit.Fit(idealWeights, decimation, gridPointCount, fittedGrid);

        Span<int> roundedGrid = stackalloc int[gridPointCount];
        for (int i = 0; i < gridPointCount; i++)
        {
            roundedGrid[i] = (int)Math.Round(fittedGrid[i]);
        }

        int[] reconstructed = new int[footprint.PixelCount];
        DecimationTable.InfillWeights(roundedGrid, decimation, reconstructed);
        return reconstructed;
    }

    [Fact]
    public void Fit_FullResolutionGrid_RecoversWeightsExactly()
    {
        // When the grid matches the footprint (no decimation), the infill is the identity, so the
        // fit must recover the ideal weights exactly.
        int[] ideal = new int[16];
        for (int i = 0; i < ideal.Length; i++)
        {
            ideal[i] = (i * 64) / (ideal.Length - 1);
        }

        int[] reconstructed = FitThenInfill(ideal, FootprintType.Footprint4x4, gridWidth: 4, gridHeight: 4);

        Assert.Equal(ideal, reconstructed);
    }

    [Fact]
    public void Fit_ConstantWeights_RecoversUniformGrid()
    {
        // Every texel wants the same weight; a decimated grid should reproduce it everywhere.
        int[] ideal = new int[36];
        Array.Fill(ideal, 32);

        int[] reconstructed = FitThenInfill(ideal, FootprintType.Footprint6x6, gridWidth: 3, gridHeight: 3);

        foreach (int w in reconstructed)
        {
            Assert.Equal(32, w);
        }
    }

    [Fact]
    public void Fit_LinearRamp_ReconstructsRampClosely()
    {
        // A smooth horizontal ramp is exactly representable by a coarse grid (bilinear infill
        // reproduces linear data), so a decimated fit should track it within rounding.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint8x8);
        int[] ideal = new int[footprint.PixelCount];
        for (int y = 0; y < footprint.Height; y++)
        {
            for (int x = 0; x < footprint.Width; x++)
            {
                ideal[(y * footprint.Width) + x] = (x * 64) / (footprint.Width - 1);
            }
        }

        int[] reconstructed = FitThenInfill(ideal, FootprintType.Footprint8x8, gridWidth: 5, gridHeight: 5);

        AssertMeanAbsErrorAtMost(ideal, reconstructed, maxMeanAbsError: 2.0);
    }

    [Fact]
    public void Fit_HeavilyDecimatedGrid_StaysWithinWeightDomain()
    {
        // A large footprint fitted to a tiny 2x2 grid forces heavy decimation. Whatever the content,
        // every fitted grid weight must stay in the [0, 64] interpolation domain.
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint12x12);
        DecimationInfo decimation = DecimationTable.Get(footprint, 2, 2);
        int[] ideal = new int[footprint.PixelCount];
        var rng = new Random(99);
        for (int i = 0; i < ideal.Length; i++)
        {
            ideal[i] = rng.Next(0, 65);
        }

        Span<double> fittedGrid = stackalloc double[4];
        DecimationFit.Fit(ideal, decimation, gridPointCount: 4, fittedGrid);

        foreach (double w in fittedGrid)
        {
            Assert.InRange(w, 0.0, 64.0);
        }
    }

    [Fact]
    public void Fit_ExtremeWeightsBeyondMidpoint_ClampToDomain()
    {
        // All-maximum ideal weights must not overshoot past 64 after the least-squares fit.
        int[] ideal = new int[36];
        Array.Fill(ideal, 64);

        int[] reconstructed = FitThenInfill(ideal, FootprintType.Footprint6x6, gridWidth: 4, gridHeight: 4);

        foreach (int w in reconstructed)
        {
            Assert.InRange(w, 0, 64);
            Assert.Equal(64, w);
        }
    }

    private static void AssertMeanAbsErrorAtMost(int[] ideal, int[] reconstructed, double maxMeanAbsError)
    {
        Assert.Equal(ideal.Length, reconstructed.Length);
        double sum = 0;
        for (int i = 0; i < ideal.Length; i++)
        {
            sum += Math.Abs(ideal[i] - reconstructed[i]);
        }

        double meanAbsError = sum / ideal.Length;
        Assert.True(meanAbsError <= maxMeanAbsError, $"mean abs error {meanAbsError:F2} exceeded {maxMeanAbsError}");
    }
}
