using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.Tests.HDR;

public class RgbaHdrColorExtensionsTests
{
    [Fact]
    public void GetChannel_WithValidIndices_ShouldReturnCorrectChannels()
    {
        RgbaHdrColor color = new(1000, 2000, 3000, 4000);

        Assert.Equal(1000, color.GetChannel(0));
        Assert.Equal(2000, color.GetChannel(1));
        Assert.Equal(3000, color.GetChannel(2));
        Assert.Equal(4000, color.GetChannel(3));
    }

    [Fact]
    public void GetChannel_WithInvalidIndex_ShouldThrowException()
    {
        RgbaHdrColor color = new(1000, 2000, 3000, 4000);

        void Act() => _ = color.GetChannel(4);

        Assert.Throws<ArgumentOutOfRangeException>(Act);
    }

    [Fact]
    public void IsCloseTo_WithSimilarColors_ShouldReturnTrue()
    {
        RgbaHdrColor color1 = new(1000, 2000, 3000, 4000);
        RgbaHdrColor color2 = new(1005, 1995, 3002, 3998);

        bool result = color1.IsCloseTo(color2, 10);

        Assert.True(result);
    }

    [Fact]
    public void IsCloseTo_WithDifferentColors_ShouldReturnFalse()
    {
        RgbaHdrColor color1 = new(1000, 2000, 3000, 4000);
        RgbaHdrColor color2 = new(1020, 2000, 3000, 4000);

        bool result = color1.IsCloseTo(color2, 10);

        Assert.False(result);
    }
}
