using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests;

/// <summary>
/// Direct unit tests for <see cref="EndpointFitter"/>, the least-squares endpoint fitter every
/// non-constant block routes through. These pin its invariants — clamping, RGB-sum ordering, axis
/// orientation, and the degenerate fallback — rather than only exercising it transitively through a
/// full encode.
/// </summary>
public class EndpointFitterTests
{
    /// <summary>
    /// Builds a texel cloud sampled along the line from <paramref name="low"/> to
    /// <paramref name="high"/> (inclusive), the ideal input for a single endpoint line.
    /// </summary>
    private static RgbaColor[] LineCloud(RgbaColor low, RgbaColor high, int count)
    {
        var texels = new RgbaColor[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            texels[i] = new RgbaColor(Lerp(low.R, high.R, t), Lerp(low.G, high.G, t), Lerp(low.B, high.B, t), Lerp(low.A, high.A, t));
        }

        return texels;
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));

    private static int ChannelSum(RgbaColor c) => c.R + c.G + c.B;

    [Fact]
    public void Fit_ColinearCloud_RecoversEndpointsSpanningTheLine()
    {
        // Texels sampled along a known RGB line; the fitted endpoints should land at (or very near)
        // the line's extremes, since the principal axis is exactly that line.
        var lineLow = new RgbaColor(20, 40, 60, 255);
        var lineHigh = new RgbaColor(200, 180, 160, 255);
        RgbaColor[] texels = LineCloud(lineLow, lineHigh, 16);

        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        // Within a small tolerance for the rounding through mean/axis/projection.
        AssertChannelsClose(lineLow, low, tolerance: 3);
        AssertChannelsClose(lineHigh, high, tolerance: 3);
    }

    [Fact]
    public void Fit_OrdersEndpointsByRgbSum()
    {
        // Regardless of input order, the high endpoint's RGB sum must be at least the low's — the
        // invariant that keeps the decoder's blue-contract swap from firing.
        var lineLow = new RgbaColor(10, 10, 10, 255);
        var lineHigh = new RgbaColor(240, 230, 220, 255);
        RgbaColor[] ascending = LineCloud(lineLow, lineHigh, 12);
        RgbaColor[] descending = (RgbaColor[])ascending.Clone();
        Array.Reverse(descending);

        (RgbaColor lowA, RgbaColor highA) = EndpointFitter.Fit(ascending);
        (RgbaColor lowD, RgbaColor highD) = EndpointFitter.Fit(descending);

        Assert.True(ChannelSum(highA) >= ChannelSum(lowA));
        Assert.True(ChannelSum(highD) >= ChannelSum(lowD));
    }

    [Fact]
    public void Fit_AntiCorrelatedChannels_TracksTheAxis()
    {
        // R rises while B falls (with unequal spans, as in real content). A per-channel bounding box
        // mis-orients this; the principal axis should track both so the fitted endpoints capture the
        // high-R/low-B and low-R/high-B extremes rather than collapsing to a grey average.
        var texels = new RgbaColor[16];
        for (int i = 0; i < texels.Length; i++)
        {
            double t = (double)i / (texels.Length - 1);
            texels[i] = new RgbaColor(Lerp(0, 255, t), 128, Lerp(200, 40, t), 255);
        }

        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        Assert.True(Math.Abs(high.R - low.R) > 150, $"R should span the range, got {low.R}..{high.R}");
        Assert.True(Math.Abs(high.B - low.B) > 100, $"B should span the range, got {low.B}..{high.B}");
        // The endpoints sit on opposite corners: where R is high, B is low.
        Assert.True((high.R > low.R) != (high.B > low.B), "R and B should move in opposite directions across the endpoints");
    }

    [Fact]
    public void Fit_PerfectlySymmetricAntiCorrelation_TracksTheAxis()
    {
        // The hard case: two channels anti-correlated with EQUAL variance (R: 0->255 while
        // B: 255->0), whose principal axis (1,0,-1,0) is exactly orthogonal to a uniform
        // power-iteration seed. Seeding from the highest-variance channel instead lets the fitter
        // find the axis here, where a uniform seed would collapse to the grey diagonal.
        var texels = new RgbaColor[16];
        for (int i = 0; i < texels.Length; i++)
        {
            byte up = (byte)(i * 255 / (texels.Length - 1));
            texels[i] = new RgbaColor(up, 128, (byte)(255 - up), 255);
        }

        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        Assert.True(Math.Abs(high.R - low.R) > 150, $"R should span the range, got {low.R}..{high.R}");
        Assert.True(Math.Abs(high.B - low.B) > 150, $"B should span the range, got {low.B}..{high.B}");
        Assert.True((high.R > low.R) != (high.B > low.B), "R and B should move in opposite directions across the endpoints");
    }

    [Fact]
    public void Fit_ConstantCloud_ReturnsThatColourForBothEndpoints()
    {
        // No variance: the degenerate-axis fallback fires, and both endpoints collapse to the mean,
        // which is the constant colour itself.
        var colour = new RgbaColor(123, 45, 200, 255);
        var texels = new RgbaColor[10];
        Array.Fill(texels, colour);

        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        AssertChannelsClose(colour, low, tolerance: 1);
        AssertChannelsClose(colour, high, tolerance: 1);
    }

    [Fact]
    public void Fit_IncludesAlphaInTheFittedLine()
    {
        // Alpha varies along with RGB; the fitted endpoints must span the alpha range too, confirming
        // the fitter treats alpha as a fourth axis rather than ignoring it.
        var lineLow = new RgbaColor(30, 30, 30, 20);
        var lineHigh = new RgbaColor(200, 200, 200, 230);
        RgbaColor[] texels = LineCloud(lineLow, lineHigh, 12);

        (RgbaColor low, RgbaColor high) = EndpointFitter.Fit(texels);

        Assert.True(Math.Abs(high.A - low.A) > 150, $"alpha should span the range, got {low.A}..{high.A}");
    }

    [Fact]
    public void FitSubsets_PartitionsTexels_FitsEachSubsetIndependently()
    {
        // Two partitions with very different colour content; each subset's endpoints should reflect
        // only its own texels, not a blend across the partition boundary.
        RgbaColor[] redCloud = LineCloud(new RgbaColor(60, 0, 0, 255), new RgbaColor(220, 0, 0, 255), 4);
        RgbaColor[] blueCloud = LineCloud(new RgbaColor(0, 0, 60, 255), new RgbaColor(0, 0, 220, 255), 4);
        var texels = new RgbaColor[8];
        var assignment = new int[8];
        for (int i = 0; i < 4; i++)
        {
            texels[i] = redCloud[i];
            assignment[i] = 0;
            texels[i + 4] = blueCloud[i];
            assignment[i + 4] = 1;
        }

        Span<RgbaColor> subsetLow = stackalloc RgbaColor[2];
        Span<RgbaColor> subsetHigh = stackalloc RgbaColor[2];
        bool ok = EndpointFitter.FitSubsets(texels, assignment, partitionCount: 2, subsetLow, subsetHigh);

        Assert.True(ok);
        // Subset 0 is the red cloud (no blue), subset 1 is the blue cloud (no red).
        Assert.True(subsetHigh[0].R > 150 && subsetHigh[0].B == 0, $"subset 0 should be red, got {subsetHigh[0]}");
        Assert.True(subsetHigh[1].B > 150 && subsetHigh[1].R == 0, $"subset 1 should be blue, got {subsetHigh[1]}");
    }

    [Fact]
    public void FitSubsets_EmptyPartition_ReturnsFalse()
    {
        // An assignment that leaves partition 1 empty cannot be fitted; the fitter must report this
        // (the encoder skips such seeds rather than emitting a degenerate subset).
        var texels = new RgbaColor[4];
        Array.Fill(texels, new RgbaColor(100, 100, 100, 255));
        int[] assignment = [0, 0, 0, 0]; // nothing assigned to partition 1

        Span<RgbaColor> subsetLow = stackalloc RgbaColor[2];
        Span<RgbaColor> subsetHigh = stackalloc RgbaColor[2];
        bool ok = EndpointFitter.FitSubsets(texels, assignment, partitionCount: 2, subsetLow, subsetHigh);

        Assert.False(ok);
    }

    private static void AssertChannelsClose(RgbaColor expected, RgbaColor actual, int tolerance)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance, $"R: expected ~{expected.R}, got {actual.R}");
        Assert.True(Math.Abs(expected.G - actual.G) <= tolerance, $"G: expected ~{expected.G}, got {actual.G}");
        Assert.True(Math.Abs(expected.B - actual.B) <= tolerance, $"B: expected ~{expected.B}, got {actual.B}");
        Assert.True(Math.Abs(expected.A - actual.A) <= tolerance, $"A: expected ~{expected.A}, got {actual.A}");
    }
}
