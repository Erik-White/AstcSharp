using AstcSharp;
using AstcSharp.Core;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests.HDR;

public class HdrDecoderTests
{
    [Fact]
    public void DecompressHdr_WithValidBlock_ShouldProduceCorrectOutputSize()
    {
        // A single 4x4 block of zeros (a valid void-extent block).
        byte[] astcData = new byte[16];
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        float[] hdrResult = StreamCodec.DecodeHdr(astcData, 4, 4, footprint);

        // 4x4 pixels, 4 float values (RGBA) per pixel.
        Assert.Equal(4 * 4 * 4, hdrResult.Length);

        foreach (float value in hdrResult)
        {
            Assert.False(float.IsNaN(value));
            Assert.False(float.IsInfinity(value));

            // Values should be in reasonable range for normalized colors.
            Assert.True(value >= 0.0f);
            Assert.True(value <= 1.1f); // Allow slight overshoot for HDR
        }
    }

    [Fact]
    public void DecompressHdr_WithDifferentFootprints_ShouldWork()
    {
        FootprintType[] footprints =
        [
            FootprintType.Footprint4x4,
            FootprintType.Footprint5x5,
            FootprintType.Footprint6x6,
            FootprintType.Footprint8x8
        ];

        foreach (FootprintType footprint in footprints)
        {
            // One ASTC block (all zeros = void-extent block).
            Footprint fp = Footprint.FromFootprintType(footprint);
            byte[] astcData = new byte[16];

            float[] result = StreamCodec.DecodeHdr(astcData, fp.Width, fp.Height, fp);

            // footprint.Width * footprint.Height pixels, each with 4 float values.
            Assert.Equal(fp.Width * fp.Height * 4, result.Length);
        }
    }

    [Fact]
    public void DecompressHdr_WithTruncatedData_ShouldThrow()
    {
        // 64x64 at a 4x4 footprint needs 256 blocks; an empty source is far too short.
        using MemoryStream source = new([]);
        using MemoryStream destination = new();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<EndOfStreamException>(() =>
            AstcDecoder.DecompressHdrImage(source, destination, 64, 64, footprint));
    }

    [Fact]
    public void DecompressHdr_WithZeroDimensions_ShouldThrowArgumentOutOfRangeException()
    {
        using MemoryStream source = new(new byte[16]);
        using MemoryStream destination = new();
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AstcDecoder.DecompressHdrImage(source, destination, 0, 0, footprint));
    }
}
