using AstcSharp.Core;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests;

public class AstcEncoderTests
{
    private static readonly Footprint Footprint4x4 = Footprint.FromFootprintType(FootprintType.Footprint4x4);

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    public void CompressImage_NonPositiveDimension_Throws(int width, int height)
    {
        using MemoryStream source = new(new byte[Math.Max(1, width) * Math.Max(1, height) * 4]);
        using MemoryStream destination = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => AstcEncoder.CompressImage(source, destination, width, height, Footprint4x4));
    }

    [Fact]
    public void CompressImage_SourceShorterThanImage_Throws()
    {
        // A 4x4 image needs 4 * 4 * 4 = 64 bytes; one byte short must be rejected.
        using MemoryStream source = new(new byte[63]);
        using MemoryStream destination = new();

        Assert.Throws<EndOfStreamException>(() => AstcEncoder.CompressImage(source, destination, 4, 4, Footprint4x4));
    }

    [Fact]
    public void CompressImage_SourceExactlyImageSize_EncodesOneBlock()
    {
        byte[] encoded = StreamCodec.Encode(new byte[4 * 4 * 4], 4, 4, Footprint4x4);

        Assert.Equal(BlockInfo.SizeInBytes, encoded.Length);
    }
}
