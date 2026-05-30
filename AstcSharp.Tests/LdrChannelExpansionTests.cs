using AstcSharp.Core;

namespace AstcSharp.Tests;

public class LdrChannelExpansionTests
{
    [Theory]
    [InlineData(0x00, 0x0000)]
    [InlineData(0x01, 0x0101)]
    [InlineData(0x7F, 0x7F7F)]
    [InlineData(0x80, 0x8080)]
    [InlineData(0xFF, 0xFFFF)]
    public void LinearExpand_ReplicatesByte(int input, int expected)
    {
        Assert.Equal(expected, LinearExpand.Expand(input));
    }

    [Theory]
    [InlineData(0x00, 0x0080)]
    [InlineData(0x01, 0x0180)]
    [InlineData(0x7F, 0x7F80)]
    [InlineData(0x80, 0x8080)]
    [InlineData(0xFF, 0xFF80)]
    public void SrgbExpand_UsesFixedLowByte(int input, int expected)
    {
        Assert.Equal(expected, SrgbExpand.Expand(input));
    }

    /// <summary>
    /// At weight 0 (the endpoint itself) the output is the top 8 bits of the expansion, so both
    /// modes return the original byte — the low-byte difference (replication vs 0x80) is shifted
    /// away. The modes only diverge at intermediate weights (ASTC spec §C.2.19).
    /// </summary>
    [Theory]
    [InlineData(0x40)]
    [InlineData(0xC3)]
    public void ScalarInterpolation_AtEndpointWeights_MatchesAcrossModes(int value)
    {
        int linearLow = SimdHelpers.InterpolateChannelScalar<LinearExpand>(value, 0xFF, weight: 0);
        int srgbLow = SimdHelpers.InterpolateChannelScalar<SrgbExpand>(value, 0xFF, weight: 0);

        Assert.Equal(value, linearLow);
        Assert.Equal(value, srgbLow);
    }

    [Fact]
    public void ScalarInterpolation_AtMidWeights_CanDivergeBetweenModes()
    {
        // The low-byte difference only tips the top-8-bit result at certain rounding
        // boundaries (ASTC spec §C.2.19). Confirm such cases exist rather than guessing one.
        bool found = false;
        for (int p0 = 0; p0 < 256 && !found; p0++)
        {
            for (int p1 = 0; p1 < 256 && !found; p1++)
            {
                for (int w = 1; w < 64; w++)
                {
                    int linear = SimdHelpers.InterpolateChannelScalar<LinearExpand>(p0, p1, w);
                    int srgb = SimdHelpers.InterpolateChannelScalar<SrgbExpand>(p0, p1, w);
                    if (linear != srgb)
                    {
                        found = true;
                        break;
                    }
                }
            }
        }

        Assert.True(found, "linear and sRGB expansion should differ at some weight");
    }

    /// <summary>
    /// Wherever the two modes differ, they differ by at most 1 (ASTC spec §C.2.19 — the only
    /// change is the 16-bit low byte, which can shift the truncated top byte by a single LSB).
    /// </summary>
    [Fact]
    public void ScalarInterpolation_ModesDifferByAtMostOne()
    {
        for (int p0 = 0; p0 < 256; p0++)
        {
            for (int p1 = 0; p1 < 256; p1++)
            {
                for (int w = 0; w <= 64; w++)
                {
                    int linear = SimdHelpers.InterpolateChannelScalar<LinearExpand>(p0, p1, w);
                    int srgb = SimdHelpers.InterpolateChannelScalar<SrgbExpand>(p0, p1, w);
                    Assert.True(Math.Abs(linear - srgb) <= 1);
                }
            }
        }
    }
}
