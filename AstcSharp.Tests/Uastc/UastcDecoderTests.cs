using AstcSharp.IO;
using AstcSharp.Tests.Utils;

namespace AstcSharp.Tests.Uastc;

public class UastcDecoderTests
{
    // Fixtures are raw UASTC blocks wrapped in the ARM .astc file header.
    // Collectively the fixtures cover modes 0,1,2,3,4,6,8,9,10,11,15,17 — all structural
    // categories (single/multi-subset, dual-plane) and all CEMs (RGB, RGBA, LA, solid).
    [Theory]
    [InlineData("uastc-rgb-m1")]
    [InlineData("uastc-solid-m8")]
    [InlineData("uastc-la-m15")]
    [InlineData("uastc-rgb-m4-6")]
    [InlineData("uastc-rgb-m6")]
    [InlineData("uastc-rgb-la-m4-15")]
    [InlineData("uastc-la-solid-m8-17")]
    [InlineData("uastc-rgba-m9-10-11")]
    [InlineData("uastc-rgb-solid-m0-3-8")]
    [InlineData("uastc-rgba-rgb-solid-m0-1-2-3-4-6-8-11")]
    public void DecompressImage_MatchesExpected(string name)
    {
        (byte[] levelData, int width, int height) = LoadFixture(name);
        byte[] expected = LoadExpected(name);

        byte[] actual = StreamCodec.DecodeUastc(levelData, width, height);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecompressImage_ReservedMode_EmitsMagenta()
    {
        // Byte 0 = 0x45 (7-bit reserved mode 19 huff code) -> error colour for the whole block.
        byte[] block = new byte[16];
        block[0] = 0x45;

        byte[] decoded = StreamCodec.DecodeUastc(block, 4, 4);

        AssertAllMagenta(decoded);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, -1)]
    public void DecompressImage_InvalidDimensions_Throws(int width, int height)
    {
        using MemoryStream source = new(new byte[16]);
        using MemoryStream destination = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UastcDecoder.DecompressImage(source, destination, width, height));
    }

    [Fact]
    public void DecompressImage_InsufficientData_Throws()
    {
        // 16x16 needs 16 blocks (256 bytes); provide one block.
        using MemoryStream source = new(new byte[16]);
        using MemoryStream destination = new();

        Assert.Throws<EndOfStreamException>(() =>
            UastcDecoder.DecompressImage(source, destination, 16, 16));
    }

    // The .uastc fixtures use the ARM .astc file header (magic + footprint + dimensions) wrapping
    // raw UASTC blocks, so AstcFile parses the container even though the payload is UASTC.
    private static (byte[] LevelData, int Width, int Height) LoadFixture(string name)
    {
        byte[] bytes = File.ReadAllBytes(TestFile.GetInputFileFullPath(Path.Combine("Uastc", name + ".uastc")));
        AstcFile file = AstcFile.FromMemory(bytes);
        return (file.Blocks.ToArray(), file.Width, file.Height);
    }

    private static byte[] LoadExpected(string name)
        => File.ReadAllBytes(Path.Combine("TestData", "Expected", "Uastc", name + ".raw"));

    private static void AssertAllMagenta(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 4)
        {
            Assert.Equal(0xFF, buffer[i]);
            Assert.Equal(0x00, buffer[i + 1]);
            Assert.Equal(0xFF, buffer[i + 2]);
            Assert.Equal(0xFF, buffer[i + 3]);
        }
    }
}
