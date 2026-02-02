using AstcSharp.Core;
using FluentAssertions;

namespace AstcSharp.Tests;

public class BitOperationsTests
{
    #region GetBits UInt128 Tests

    [Fact]
    public void GetBits_UInt128WithLowBits_ShouldExtractCorrectly()
    {
        // Arrange
        UInt128 value = new UInt128(0x1234567890ABCDEF, 0xFEDCBA0987654321);

        // Act
        var result = BitOperations.GetBits(value, 0, 8);

        // Assert
        result.Low().Should().Be(0x21UL);
    }

    [Fact]
    public void GetBits_UInt128WithZeroLength_ShouldReturnZero()
    {
        // Arrange
        UInt128 value = new UInt128(0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF);

        // Act
        var result = BitOperations.GetBits(value, 0, 0);

        // Assert
        result.Should().Be(UInt128.Zero);
    }

    #endregion

    #region GetBits ULong Tests

    [Fact]
    public void GetBits_ULongWithLowBits_ShouldExtractCorrectly()
    {
        // Arrange
        ulong value = 0xFEDCBA0987654321;

        // Act
        var result = BitOperations.GetBits(value, 0, 8);

        // Assert
        result.Should().Be(0x21UL);
    }

    [Fact]
    public void GetBits_ULongWithZeroLength_ShouldReturnZero()
    {
        // Arrange
        ulong value = 0xFFFFFFFFFFFFFFFF;

        // Act
        var result = BitOperations.GetBits(value, 0, 0);

        // Assert
        result.Should().Be(0UL);
    }

    #endregion

    #region TransferPrecision Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 20)]
    [InlineData(128, 255)]
    [InlineData(255, 128)]
    [InlineData(64, 64)]
    public void TransferPrecision_WithSameInput_ShouldBeDeterministic(int inputA, int inputB)
    {
        // Act
        var (a1, b1) = BitOperations.TransferPrecision(inputA, inputB);
        var (a2, b2) = BitOperations.TransferPrecision(inputA, inputB);

        // Assert
        a1.Should().Be(a2);
        b1.Should().Be(b2);
    }

    [Fact]
    public void TransferPrecision_WithAllValidByteInputs_ShouldNotThrow()
    {
        // Act & Assert
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                var action = () => BitOperations.TransferPrecision(a, b);
                action.Should().NotThrow();
            }
        }
    }

    #endregion

    #region TransferPrecisionInverse Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 10)]
    [InlineData(10, 255)]
    [InlineData(31, 128)]
    [InlineData(-32, 200)]
    [InlineData(-1, 100)]
    public void TransferPrecisionInverse_WithSameInput_ShouldBeDeterministic(int inputA, int inputB)
    {
        // Act
        var (a1, b1) = BitOperations.TransferPrecisionInverse(inputA, inputB);
        var (a2, b2) = BitOperations.TransferPrecisionInverse(inputA, inputB);

        // Assert
        a1.Should().Be(a2);
        b1.Should().Be(b2);
    }

    [Theory]
    [InlineData(-33, 128)]  // a too small
    [InlineData(32, 128)]   // a too large
    [InlineData(0, -1)]     // b too small
    [InlineData(0, 256)]    // b too large
    public void TransferPrecisionInverse_WithInvalidInput_ShouldThrowArgumentOutOfRangeException(int a, int b)
    {
        // Act
        var action = () => BitOperations.TransferPrecisionInverse(a, b);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Round-Trip Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 20)]
    [InlineData(31, 255)]
    [InlineData(-32, 128)]
    [InlineData(-1, 200)]
    public void TransferPrecision_AfterInverse_ShouldReturnOriginalValues(int originalA, int originalB)
    {
        // Arrange & Act - apply inverse to encode
        var (encodedA, encodedB) = BitOperations.TransferPrecisionInverse(originalA, originalB);

        // Apply regular to decode
        var (decodedA, decodedB) = BitOperations.TransferPrecision(encodedA, encodedB);

        // Assert
        decodedA.Should().Be(originalA);
        decodedB.Should().Be(originalB);
    }

    #endregion
}
