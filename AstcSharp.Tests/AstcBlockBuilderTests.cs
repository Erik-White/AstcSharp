using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Encoding;
using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests;

/// <summary>
/// Block-assembly tests: the <see cref="AstcFileHeader.WriteTo"/> serialiser round-trips through
/// <see cref="AstcFileHeader.FromMemory"/>, and <see cref="AstcBlockBuilder"/> places low-bit
/// fields where the decoder reads them. A constant-colour void-extent block (spec §C.2.23) is the
/// simplest full block, exercising the marker, reserved bits, and 16-bit RGBA payload placement.
/// </summary>
public class AstcBlockBuilderTests
{
    [Theory]
    [InlineData(4, 4, 256, 128)]
    [InlineData(8, 8, 1920, 1080)]
    [InlineData(12, 12, 17, 13)]
    public void Header_WriteTo_RoundTripsThroughFromMemory(byte blockWidth, byte blockHeight, int imageWidth, int imageHeight)
    {
        var header = new AstcFileHeader(blockWidth, blockHeight, BlockDepth: 1, imageWidth, imageHeight, ImageDepth: 1);

        Span<byte> buffer = stackalloc byte[AstcFileHeader.SizeInBytes];
        header.WriteTo(buffer);
        AstcFileHeader parsed = AstcFileHeader.FromMemory(buffer);

        Assert.Equal(header, parsed);
    }

    [Fact]
    public void Builder_VoidExtentBlock_DecodesToConstantColor()
    {
        // Assemble an LDR void-extent block: marker 0x1FC in bits[0:8], LDR (bit 9 = 0),
        // reserved bits[10:11] = 0x3, all-ones texel coordinates (bits[12:51]), and four
        // UNORM16 channels in the high 64 bits (spec §C.2.23).
        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(0x1FC, startBit: 0, count: 9);
        builder.PlaceLowField(0x3, startBit: 10, count: 2);
        // 4 × 13-bit texel coordinates (bits 12..63), all ones = the "no constraint" sentinel.
        builder.PlaceLowField(0xFFFFFFFFFFFFF, startBit: 12, count: 52);

        // R=0x8000, G=0x4000, B=0xC000, A=0xFFFF as UNORM16 (decoder takes the high byte for LDR).
        builder.PlaceLowField(0x8000, startBit: 64, count: 16);
        builder.PlaceLowField(0x4000, startBit: 80, count: 16);
        builder.PlaceLowField(0xC000, startBit: 96, count: 16);
        builder.PlaceLowField(0xFFFF, startBit: 112, count: 16);

        Span<byte> blockBytes = stackalloc byte[BlockInfo.SizeInBytes];
        builder.WriteTo(blockBytes);

        // The block-mode parser must recognise it as a well-formed void-extent block.
        BlockInfo info = BlockModeDecoder.Decode(builder.Build());
        Assert.True(info.IsValid);
        Assert.True(info.IsVoidExtent);
        Assert.False(info.IsHdr);

        // The full decode path must fill every texel with the constant colour (high byte of each
        // UNORM16 channel): R=0x80, G=0x40, B=0xC0, A=0xFF.
        var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);
        byte[] pixels = StreamCodec.DecodeLdr(blockBytes.ToArray(), 4, 4, footprint);

        Assert.Equal(4 * 4 * BlockInfo.ChannelsPerPixel, pixels.Length);
        for (int i = 0; i < pixels.Length; i += BlockInfo.ChannelsPerPixel)
        {
            Assert.Equal(0x80, pixels[i + 0]);
            Assert.Equal(0x40, pixels[i + 1]);
            Assert.Equal(0xC0, pixels[i + 2]);
            Assert.Equal(0xFF, pixels[i + 3]);
        }
    }
}
