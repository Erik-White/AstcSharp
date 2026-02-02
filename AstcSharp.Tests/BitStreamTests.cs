using AstcSharp.IO;
using FluentAssertions;

namespace AstcSharp.Tests;

public class BitStreamTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithBitsAndLength_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var stream = new BitStream(0b1010101010101010UL, 32);

        // Assert
        stream.Bits.Should().Be(32);
    }

    [Fact]
    public void Constructor_WithoutParameters_ShouldInitializeEmpty()
    {
        // Arrange & Act
        var stream = new BitStream();

        // Assert
        stream.Bits.Should().Be(0);
    }

    #endregion

    #region TryGetBits Tests

    [Fact]
    public void TryGetBits_WithSingleBitFromZero_ShouldReturnZero()
    {
        // Arrange
        var stream = new BitStream(0UL, 1);

        // Act
        var success = stream.TryGetBits<uint>(1, out var bits);

        // Assert
        success.Should().BeTrue();
        bits.Should().Be(0U);
    }

    [Fact]
    public void TryGetBits_AfterExhausted_ShouldReturnFalse()
    {
        // Arrange
        var stream = new BitStream(0UL, 1);
        stream.TryGetBits<uint>(1, out _);

        // Act
        var success = stream.TryGetBits<uint>(1, out var bits);

        // Assert
        success.Should().BeFalse();
    }

    [Fact]
    public void TryGetBits_WithAlternatingBitPattern_ShouldExtractCorrectly()
    {
        // Arrange
        var stream = new BitStream(0b1010101010101010UL, 32);

        // Act & Assert - first bit
        stream.TryGetBits<uint>(1, out var bits1).Should().BeTrue();
        bits1.Should().Be(0U);

        // Act & Assert - next 3 bits
        stream.TryGetBits<uint>(3, out var bits2).Should().BeTrue();
        bits2.Should().Be(0b101U);

        // Act & Assert - next 8 bits
        stream.TryGetBits<uint>(8, out var bits3).Should().BeTrue();
        bits3.Should().Be(0b10101010U);

        // Assert - remaining bits
        stream.Bits.Should().Be(20);

        // Act & Assert - remaining 20 bits
        stream.TryGetBits<uint>(20, out var bits4).Should().BeTrue();
        bits4.Should().Be(0b1010U);
        stream.Bits.Should().Be(0);
    }

    [Fact]
    public void TryGetBits_With64BitsOfOnes_ShouldReturnAllOnes()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        var stream = new BitStream(allBits, 64);

        // Assert initial state
        stream.Bits.Should().Be(64);

        // Act
        var success = stream.TryGetBits<ulong>(64, out var bits);

        // Assert
        success.Should().BeTrue();
        bits.Should().Be(allBits);
        stream.Bits.Should().Be(0);
    }

    [Fact]
    public void TryGetBits_With40BitsFromFullBits_ShouldReturnLower40Bits()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        const ulong expected40Bits = 0x000000FFFFFFFFFFUL;
        var stream = new BitStream(allBits, 64);

        // Assert initial state
        stream.Bits.Should().Be(64);

        // Act
        var success = stream.TryGetBits<ulong>(40, out var bits);

        // Assert
        success.Should().BeTrue();
        bits.Should().Be(expected40Bits);
        stream.Bits.Should().Be(24);
    }

    [Fact]
    public void TryGetBits_WithZeroBits_ShouldReturnZeroAndNotConsume()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        const ulong expected40Bits = 0x000000FFFFFFFFFFUL;
        var stream = new BitStream(allBits, 32);

        // Act & Assert - get 0 bits
        stream.TryGetBits<ulong>(0, out var bits1).Should().BeTrue();
        bits1.Should().Be(0UL);

        // Act & Assert - get 32 bits
        stream.TryGetBits<ulong>(32, out var bits2).Should().BeTrue();
        bits2.Should().Be(expected40Bits & 0xFFFFFFFFUL);

        // Act & Assert - get 0 bits again
        stream.TryGetBits<ulong>(0, out var bits3).Should().BeTrue();
        bits3.Should().Be(0UL);
        stream.Bits.Should().Be(0);
    }

    #endregion

    #region PutBits Tests

    [Fact]
    public void PutBits_WithSmallValues_ShouldAccumulateCorrectly()
    {
        // Arrange
        var stream = new BitStream();

        // Act
        stream.PutBits(0U, 1);
        stream.PutBits(0b11U, 2);

        // Assert
        stream.Bits.Should().Be(3);
        stream.TryGetBits<uint>(3, out var bits).Should().BeTrue();
        bits.Should().Be(0b110U);
    }

    [Fact]
    public void PutBits_With64BitsOfOnes_ShouldStoreCorrectly()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        var stream = new BitStream();

        // Act
        stream.PutBits(allBits, 64);

        // Assert
        stream.Bits.Should().Be(64);
        stream.TryGetBits<ulong>(64, out var bits).Should().BeTrue();
        bits.Should().Be(allBits);
        stream.Bits.Should().Be(0);
    }

    [Fact]
    public void PutBits_With40BitsOfOnes_ShouldMaskTo40Bits()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        const ulong expected40Bits = 0x000000FFFFFFFFFFUL;
        var stream = new BitStream();

        // Act
        stream.PutBits(allBits, 40);

        // Assert
        stream.TryGetBits<ulong>(40, out var bits).Should().BeTrue();
        bits.Should().Be(expected40Bits);
        stream.Bits.Should().Be(0);
    }

    [Fact]
    public void PutBits_WithZeroBitsInterspersed_ShouldHandleCorrectly()
    {
        // Arrange
        const ulong allBits = 0xFFFFFFFFFFFFFFFFUL;
        const ulong expected40Bits = 0x000000FFFFFFFFFFUL;
        var stream = new BitStream();

        // Act
        stream.PutBits(0U, 0);
        stream.PutBits((uint)(allBits & 0xFFFFFFFFUL), 32);
        stream.PutBits(0U, 0);

        // Assert
        stream.TryGetBits<ulong>(32, out var bits).Should().BeTrue();
        bits.Should().Be(expected40Bits & 0xFFFFFFFFUL);
        stream.Bits.Should().Be(0);
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void PutBits_ThenGetBits_ShouldRoundTripCorrectly()
    {
        // Arrange
        var stream = new BitStream();
        const uint value1 = 0b101;
        const uint value2 = 0b11001100;

        // Act
        stream.PutBits(value1, 3);
        stream.PutBits(value2, 8);

        // Assert
        stream.TryGetBits<uint>(3, out var retrieved1).Should().BeTrue();
        retrieved1.Should().Be(value1);
        stream.TryGetBits<uint>(8, out var retrieved2).Should().BeTrue();
        retrieved2.Should().Be(value2);
    }

    #endregion
}
