using AstcSharp.Core;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests;

/// <summary>
/// Round-trip tests: a constant-colour image encoded via <see cref="AstcEncoder"/> (void-extent
/// blocks, spec §C.2.23) must decode back to exactly that colour through this library's decoder.
/// </summary>
public class VoidExtentEncoderTests
{
    public static TheoryData<FootprintType> AllFootprintTypes =>
    [
        FootprintType.Footprint4x4, FootprintType.Footprint5x4, FootprintType.Footprint5x5,
        FootprintType.Footprint6x5, FootprintType.Footprint6x6, FootprintType.Footprint8x5,
        FootprintType.Footprint8x6, FootprintType.Footprint8x8, FootprintType.Footprint10x5,
        FootprintType.Footprint10x6, FootprintType.Footprint10x8, FootprintType.Footprint10x10,
        FootprintType.Footprint12x10, FootprintType.Footprint12x12,
    ];

    [Theory]
    [MemberData(nameof(AllFootprintTypes))]
    public void Compress_SolidColor_RoundTripsExactly(FootprintType footprintType)
    {
        Footprint footprint = Footprint.FromFootprintType(footprintType);
        // A 2x2 block grid so interior and (when non-aligned) edge blocks are exercised.
        int width = footprint.Width * 2;
        int height = footprint.Height * 2;

        byte[] pixels = SolidImage(width, height, 0x80, 0x40, 0xC0, 0xFF);

        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);
        byte[] decoded = StreamCodec.DecodeLdr(encoded, width, height, footprint);

        Assert.Equal(pixels, decoded);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(255, 255, 255, 255)]
    [InlineData(1, 2, 3, 4)]
    [InlineData(170, 85, 17, 204)]
    public void Compress_SolidColor_PreservesEachChannelValue(byte r, byte g, byte b, byte a)
    {
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint6x6);
        int width = footprint.Width;
        int height = footprint.Height;

        byte[] pixels = SolidImage(width, height, r, g, b, a);

        byte[] encoded = StreamCodec.Encode(pixels, width, height, footprint);
        byte[] decoded = StreamCodec.DecodeLdr(encoded, width, height, footprint);

        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void Compress_NearConstantBlock_EncodesValidlyViaGeneralPath()
    {
        // One perturbed texel makes the block non-constant, so it takes the general (non-void-extent)
        // encoding path rather than throwing. The result must still be a legal block (no magenta).
        Footprint footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        byte[] pixels = SolidImage(4, 4, 10, 20, 30, 40);
        pixels[^4] = 99;

        byte[] encoded = StreamCodec.Encode(pixels, 4, 4, footprint);
        byte[] decoded = StreamCodec.DecodeLdr(encoded, 4, 4, footprint);

        Assert.Equal(pixels.Length, decoded.Length);
        for (int i = 0; i < decoded.Length; i += 4)
        {
            bool isMagenta = decoded[i] == 255 && decoded[i + 1] == 0 && decoded[i + 2] == 255 && decoded[i + 3] == 255;
            Assert.False(isMagenta, "near-constant block should encode to a legal (non-magenta) block");
        }
    }

    private static byte[] SolidImage(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return pixels;
    }
}
