using AstcSharp.BiseEncoding;
using AstcSharp.Core;
using FluentAssertions;

namespace AstcSharp.Tests;

public class WeightInfillTests
{
    #region CountBitsForWeights Tests

    [Theory]
    [InlineData(4, 4, 3, 32)]
    [InlineData(4, 4, 7, 48)]
    [InlineData(2, 4, 7, 24)]
    [InlineData(2, 4, 1, 8)]
    [InlineData(4, 5, 2, 32)]
    [InlineData(4, 4, 2, 26)]
    [InlineData(4, 5, 5, 52)]
    [InlineData(4, 4, 5, 42)]
    [InlineData(3, 3, 4, 21)]
    [InlineData(4, 4, 4, 38)]
    [InlineData(3, 7, 4, 49)]
    [InlineData(4, 3, 19, 52)]
    [InlineData(4, 4, 19, 70)]
    public void CountBitsForWeights_WithVariousParameters_ShouldReturnCorrectBitCount(
        int width, int height, int range, int expectedBitCount)
    {
        // Act
        var bitCount = WeightInfill.CountBitsForWeights(width, height, range);

        // Assert
        bitCount.Should().Be(expectedBitCount);
    }

    #endregion

    #region InfillWeights Tests

    [Fact]
    public void InfillWeights_With3x3Grid_ShouldBilinearlyInterpolateTo5x5()
    {
        // Arrange
        var weights = new List<int> { 1, 3, 5, 3, 5, 7, 5, 7, 9 };
        var expected = new List<int> { 1, 2, 3, 4, 5, 2, 3, 4, 5, 6, 3, 4, 5, 6, 7, 4, 5, 6, 7, 8, 5, 6, 7, 8, 9 };

        // Act
        var result = WeightInfill.InfillWeights(weights, Footprint.Get5x5(), 3, 3);

        // Assert
        result.Should().HaveCount(expected.Count);
        result.Should().Equal(expected);
    }

    #endregion
}
