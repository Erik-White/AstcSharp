using AstcSharp.Core;
using AwesomeAssertions;

namespace AstcSharp.Tests;

public class HdrColorTests
{
    [Fact]
    public void Constructor_WithValidValues_ShouldInitializeCorrectly()
    {
        var color = new HdrColor(1000, 2000, 3000, 4000);

        color.R.Should().Be((ushort)1000);
        color.G.Should().Be((ushort)2000);
        color.B.Should().Be((ushort)3000);
        color.A.Should().Be((ushort)4000);
    }

    [Fact]
    public void Constructor_WithIntValues_ShouldClampToUshortRange()
    {
        var color = new HdrColor(-100, 70000, 30000, 80000);

        color.R.Should().Be((ushort)0);        // Clamped from -100
        color.G.Should().Be((ushort)65535);    // Clamped from 70000
        color.B.Should().Be((ushort)30000);    // Within range
        color.A.Should().Be((ushort)65535);    // Clamped from 80000
    }

    [Fact]
    public void Indexer_WithValidIndices_ShouldReturnCorrectChannels()
    {
        var color = new HdrColor(1000, 2000, 3000, 4000);

        color[0].Should().Be((ushort)1000);
        color[1].Should().Be((ushort)2000);
        color[2].Should().Be((ushort)3000);
        color[3].Should().Be((ushort)4000);
    }

    [Fact]
    public void Indexer_WithInvalidIndex_ShouldThrowException()
    {
        var color = new HdrColor(1000, 2000, 3000, 4000);

        Action act = () => _ = color[4];

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FromLdr_WithMinMaxValues_ShouldScaleCorrectly()
    {
        var ldrColor = new RgbaColor(0, 127, 255, 200);

        var hdrColor = HdrColor.FromLdr(ldrColor);

        hdrColor.R.Should().Be((ushort)0);        // 0 * 257 = 0
        hdrColor.G.Should().Be((ushort)32639);    // 127 * 257 = 32639
        hdrColor.B.Should().Be((ushort)65535);    // 255 * 257 = 65535
        hdrColor.A.Should().Be((ushort)51400);    // 200 * 257 = 51400
    }

    [Fact]
    public void ToLdr_WithHdrValues_ShouldDownscaleCorrectly()
    {
        var hdrColor = new HdrColor(0, 32639, 65535, 51400);

        var ldrColor = hdrColor.ToLdr();

        ldrColor.R.Should().Be((byte)0);     // 0 >> 8 = 0
        ldrColor.G.Should().Be((byte)127);   // 32639 >> 8 = 127
        ldrColor.B.Should().Be((byte)255);   // 65535 >> 8 = 255
        ldrColor.A.Should().Be((byte)200);   // 51400 >> 8 = 200
    }

    [Fact]
    public void FromLdr_ToLdr_RoundTrip_ShouldPreserveValues()
    {
        var original = new RgbaColor(50, 100, 150, 200);

        var hdrColor = HdrColor.FromLdr(original);
        var result = hdrColor.ToLdr();

        result.R.Should().Be(original.R);
        result.G.Should().Be(original.G);
        result.B.Should().Be(original.B);
        result.A.Should().Be(original.A);
    }

    [Fact]
    public void UshortToHalf_WithValidRange_ShouldNormalizeCorrectly()
    {
        var half0 = HdrColor.UshortToHalf(0);
        var halfMid = HdrColor.UshortToHalf(32767);
        var halfMax = HdrColor.UshortToHalf(65535);

        ((float)half0).Should().BeApproximately(0.0f, 0.001f);
        ((float)halfMid).Should().BeApproximately(0.5f, 0.001f);
        ((float)halfMax).Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public void HalfToUshort_WithValidRange_ShouldScaleCorrectly()
    {
        var ushort0 = HdrColor.HalfToUshort(Half.CreateSaturating(0.0f));
        var ushortMid = HdrColor.HalfToUshort(Half.CreateSaturating(0.5f));
        var ushortMax = HdrColor.HalfToUshort(Half.CreateSaturating(1.0f));

        ushort0.Should().Be((ushort)0);
        Math.Abs(ushortMid - 32767).Should().BeLessThanOrEqualTo(10);
        ushortMax.Should().Be((ushort)65535);
    }

    [Fact]
    public void ToHalfArray_ShouldReturnCorrectValues()
    {
        var hdrColor = new HdrColor(0, 32767, 65535, 16383);

        var halfArray = hdrColor.ToHalfArray();

        halfArray.Length.Should().Be(4);
        ((float)halfArray[0]).Should().BeApproximately(0.0f, 0.001f);
        ((float)halfArray[1]).Should().BeApproximately(0.5f, 0.001f);
        ((float)halfArray[2]).Should().BeApproximately(1.0f, 0.001f);
        ((float)halfArray[3]).Should().BeApproximately(0.25f, 0.001f);
    }

    [Fact]
    public void FromHalfArray_ShouldCreateCorrectHdrColor()
    {
        var halfArray = new Half[]
        {
            Half.CreateSaturating(0.0f),
            Half.CreateSaturating(0.5f),
            Half.CreateSaturating(1.0f),
            Half.CreateSaturating(0.25f)
        };

        var hdrColor = HdrColor.FromHalfArray(halfArray);

        hdrColor.R.Should().Be((ushort)0);
        Math.Abs(hdrColor.G - 32767).Should().BeLessThanOrEqualTo(10);
        hdrColor.B.Should().Be((ushort)65535);
        Math.Abs(hdrColor.A - 16383).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void FromHalfArray_WithInsufficientValues_ShouldThrowException()
    {
        var halfArray = new Half[] { Half.Zero, Half.Zero };

        Action act = () => HdrColor.FromHalfArray(halfArray);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsCloseTo_WithSimilarColors_ShouldReturnTrue()
    {
        var color1 = new HdrColor(1000, 2000, 3000, 4000);
        var color2 = new HdrColor(1005, 1995, 3002, 3998);

        var result = color1.IsCloseTo(color2, 10);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsCloseTo_WithDifferentColors_ShouldReturnFalse()
    {
        var color1 = new HdrColor(1000, 2000, 3000, 4000);
        var color2 = new HdrColor(1020, 2000, 3000, 4000);

        var result = color1.IsCloseTo(color2, 10);

        result.Should().BeFalse();
    }

    [Fact]
    public void Empty_ShouldReturnBlackTransparent()
    {
        var empty = HdrColor.Empty;

        empty.R.Should().Be((ushort)0);
        empty.G.Should().Be((ushort)0);
        empty.B.Should().Be((ushort)0);
        empty.A.Should().Be((ushort)0);
    }

    [Fact]
    public void BytesPerPixel_ShouldBe8()
    {
        HdrColor.BytesPerPixel.Should().Be(8);
    }
}
