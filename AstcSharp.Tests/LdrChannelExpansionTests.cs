using AstcSharp.Core;

namespace AstcSharp.Tests;

public class LdrChannelExpansionTests
{
    /// <summary>
    /// Reproduces the LDR-channel scalar interpolation (ASTC spec §C.2.19): expand both 8-bit
    /// endpoints to 16 bits, blend, and take the top 8 bits.
    /// </summary>
    private static int InterpolateTopByte(int expandedP0, int expandedP1, int weight)
        => (Interpolation.BlendWeighted(expandedP0, expandedP1, weight) >> 8) & 0xFF;

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

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0xFF)]
    public void SrgbMode_ColorUsesSrgb_AlphaStaysLinear(int input)
    {
        Assert.Equal(SrgbExpand.Expand(input), SrgbMode.ExpandColor(input));
        Assert.Equal(LinearExpand.Expand(input), SrgbMode.ExpandAlpha(input));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x7F)]
    [InlineData(0xFF)]
    public void LinearMode_ColorAndAlphaBothLinear(int input)
    {
        Assert.Equal(LinearExpand.Expand(input), LinearMode.ExpandColor(input));
        Assert.Equal(LinearExpand.Expand(input), LinearMode.ExpandAlpha(input));
    }

    /// <summary>
    /// At weight 0 (the endpoint itself) the output is the top 8 bits of the expansion, so both
    /// modes return the original byte — the low-byte difference (replication vs 0x80) is shifted
    /// away. The modes only diverge at intermediate weights (ASTC spec §C.2.19).
    /// </summary>
    [Theory]
    [InlineData(0x40)]
    [InlineData(0xC3)]
    public void ColorInterpolation_AtEndpointWeights_MatchesAcrossModes(int value)
    {
        int linearLow = InterpolateTopByte(LinearMode.ExpandColor(value), LinearMode.ExpandColor(0xFF), weight: 0);
        int srgbLow = InterpolateTopByte(SrgbMode.ExpandColor(value), SrgbMode.ExpandColor(0xFF), weight: 0);

        Assert.Equal(value, linearLow);
        Assert.Equal(value, srgbLow);
    }

    [Fact]
    public void ColorInterpolation_AtMidWeights_CanDivergeBetweenModes()
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
                    int linear = InterpolateTopByte(LinearMode.ExpandColor(p0), LinearMode.ExpandColor(p1), w);
                    int srgb = InterpolateTopByte(SrgbMode.ExpandColor(p0), SrgbMode.ExpandColor(p1), w);
                    if (linear != srgb)
                    {
                        found = true;
                        break;
                    }
                }
            }
        }

        Assert.True(found, "linear and sRGB colour expansion should differ at some weight");
    }

    /// <summary>
    /// Wherever the two modes' colour expansion differs, it differs by at most 1 (ASTC spec
    /// §C.2.19 — the only change is the 16-bit low byte, which can shift the truncated top byte
    /// by a single LSB).
    /// </summary>
    [Fact]
    public void ColorInterpolation_ModesDifferByAtMostOne()
    {
        for (int p0 = 0; p0 < 256; p0++)
        {
            for (int p1 = 0; p1 < 256; p1++)
            {
                for (int w = 0; w <= 64; w++)
                {
                    int linear = InterpolateTopByte(LinearMode.ExpandColor(p0), LinearMode.ExpandColor(p1), w);
                    int srgb = InterpolateTopByte(SrgbMode.ExpandColor(p0), SrgbMode.ExpandColor(p1), w);
                    Assert.True(Math.Abs(linear - srgb) <= 1);
                }
            }
        }
    }
}
