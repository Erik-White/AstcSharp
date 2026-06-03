using AstcSharp.Uastc;

namespace AstcSharp.Tests.Uastc;

public class UastcBitReaderTests
{
    [Fact]
    public void ReadBits_IsLsbFirstWithinAndAcrossBytes()
    {
        // byte0 = 0b1010_0101 (0xA5), byte1 = 0b0000_0011 (0x03).
        byte[] data = [0xA5, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var reader = new UastcBitReader(data);

        // LSB-first: first 4 bits of 0xA5 = 0b0101 = 5.
        Assert.Equal(5u, reader.ReadBits(4));
        // Next 4 bits = high nibble 0b1010 = 10.
        Assert.Equal(10u, reader.ReadBits(4));
        // Next 2 bits come from byte1 LSBs: 0b11 = 3.
        Assert.Equal(3u, reader.ReadBits(2));
        Assert.Equal(10, reader.BitOffset); // 4 + 4 + 2 bits consumed
    }

    [Fact]
    public void ReadBit_ReturnsBitsInLsbOrder()
    {
        byte[] data = [0b0000_0110, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var reader = new UastcBitReader(data);

        Assert.Equal(0, reader.ReadBit()); // bit 0
        Assert.Equal(1, reader.ReadBit()); // bit 1
        Assert.Equal(1, reader.ReadBit()); // bit 2
        Assert.Equal(0, reader.ReadBit()); // bit 3
    }

    [Fact]
    public void ReadBits_SpanningThreeBytes()
    {
        byte[] data = [0xFF, 0x00, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var reader = new UastcBitReader(data);

        // 24 bits: 0xFF | (0x00<<8) | (0xFF<<16) = 0xFF00FF.
        Assert.Equal(0xFF00FFu, reader.ReadBits(24));
    }

    [Fact]
    public void Skip_AdvancesOffset()
    {
        byte[] data = new byte[16];
        data[1] = 0xFF; // bits 8..15
        var reader = new UastcBitReader(data);

        reader.Skip(8);
        Assert.Equal(8, reader.BitOffset);
        Assert.Equal(0xFFu, reader.ReadBits(8));
    }
}
