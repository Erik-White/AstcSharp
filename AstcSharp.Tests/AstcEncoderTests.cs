using AstcSharp.Core;

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
        // The dimension guard runs before any buffer-size check, so the buffer length is irrelevant.
        byte[] pixels = new byte[Math.Max(1, width) * Math.Max(1, height) * 4];

        Assert.Throws<ArgumentOutOfRangeException>(() => AstcEncoder.CompressImage(pixels, width, height, Footprint4x4));
    }

    [Fact]
    public void CompressImage_BufferShorterThanImage_Throws()
    {
        // A 4x4 image needs 4 * 4 * 4 = 64 bytes; one byte short must be rejected.
        byte[] pixels = new byte[63];

        Assert.Throws<ArgumentOutOfRangeException>(() => AstcEncoder.CompressImage(pixels, 4, 4, Footprint4x4));
    }

    [Fact]
    public void CompressImage_BufferExactlyImageSize_EncodesOneBlock()
    {
        byte[] pixels = new byte[4 * 4 * 4];

        byte[] encoded = AstcEncoder.CompressImage(pixels, 4, 4, Footprint4x4);

        Assert.Equal(BlockInfo.SizeInBytes, encoded.Length);
    }
}
