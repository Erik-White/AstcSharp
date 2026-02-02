using AstcSharp.BiseEncoding;
using FluentAssertions;

namespace AstcSharp.Tests;

public class QuantizationTests
{
    #region Range Validation Tests

    [Fact]
    public void QuantizeCEValueToRange_WithMaxValue_ShouldNotExceedRange()
    {
        // Act & Assert
        for (int range = Quantization.kEndpointRangeMinValue; range < 256; ++range)
        {
            Quantization.QuantizeCEValueToRange(255, range).Should().BeLessOrEqualTo(range);
        }
    }

    [Fact]
    public void QuantizeWeightToRange_WithMaxValue_ShouldNotExceedRange()
    {
        // Act & Assert
        for (int range = 1; range < Quantization.kWeightRangeMaxValue; ++range)
        {
            Quantization.QuantizeWeightToRange(64, range).Should().BeLessOrEqualTo(range);
        }
    }

    [Fact]
    public void QuantizeCEValueToRange_WithVariousValues_ShouldNotExceedRange()
    {
        // Arrange
        var ranges = BoundedIntegerSequenceCodec.MaxRanges;
        var testValues = new[] { 0, 4, 15, 22, 66, 91, 126 };

        // Act & Assert
        foreach (var range in ranges.Where(r => r >= Quantization.kEndpointRangeMinValue))
        {
            foreach (var value in testValues)
            {
                Quantization.QuantizeCEValueToRange(value, range).Should().BeLessOrEqualTo(range);
            }
        }
    }

    [Fact]
    public void QuantizeWeightToRange_WithVariousValues_ShouldNotExceedRange()
    {
        // Arrange
        var ranges = BoundedIntegerSequenceCodec.MaxRanges;
        var testValues = new[] { 0, 4, 15, 22 };

        // Act & Assert
        foreach (var range in ranges.Where(r => r <= Quantization.kWeightRangeMaxValue))
        {
            foreach (var value in testValues)
            {
                Quantization.QuantizeWeightToRange(value, range).Should().BeLessOrEqualTo(range);
            }
        }
    }

    #endregion

    #region Reversibility Tests

    [Fact]
    public void QuantizeWeight_ThenUnquantize_ShouldReturnOriginalQuantizedValue()
    {
        // Arrange
        var ranges = BoundedIntegerSequenceCodec.MaxRanges;

        // Act & Assert
        foreach (var range in ranges.Where(r => r <= Quantization.kWeightRangeMaxValue))
        {
            for (int quantizedValue = 0; quantizedValue <= range; ++quantizedValue)
            {
                var unquantized = Quantization.UnquantizeWeightFromRange(quantizedValue, range);
                var requantized = Quantization.QuantizeWeightToRange(unquantized, range);

                requantized.Should().Be(quantizedValue);
            }
        }
    }

    [Fact]
    public void QuantizeCEValue_ThenUnquantize_ShouldReturnOriginalQuantizedValue()
    {
        // Arrange
        var ranges = BoundedIntegerSequenceCodec.MaxRanges;

        // Act & Assert
        foreach (var range in ranges.Where(r => r >= Quantization.kEndpointRangeMinValue))
        {
            for (int quantizedValue = 0; quantizedValue <= range; ++quantizedValue)
            {
                var unquantized = Quantization.UnquantizeCEValueFromRange(quantizedValue, range);
                var requantized = Quantization.QuantizeCEValueToRange(unquantized, range);

                requantized.Should().Be(quantizedValue);
            }
        }
    }

    #endregion

    #region Unquantization Output Range Tests

    [Theory]
    [InlineData(2, 7)]
    [InlineData(7, 7)]
    [InlineData(39, 63)]
    [InlineData(66, 79)]
    [InlineData(91, 191)]
    [InlineData(126, 255)]
    [InlineData(255, 255)]
    public void UnquantizeCEValueFromRange_ShouldProduceValidByteValue(int quantizedValue, int range)
    {
        // Act
        var result = Quantization.UnquantizeCEValueFromRange(quantizedValue, range);

        // Assert
        result.Should().BeLessThan(256);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 7)]
    [InlineData(7, 7)]
    [InlineData(29, 31)]
    public void UnquantizeWeightFromRange_ShouldNotExceed64(int quantizedValue, int range)
    {
        // Act
        var result = Quantization.UnquantizeWeightFromRange(quantizedValue, range);

        // Assert
        result.Should().BeLessOrEqualTo(64);
    }

    #endregion

    #region Upper Bound Range Tests

    [Fact]
    public void Quantize_WithDesiredRange_ShouldMatchExpectedRangeOutput()
    {
        // Arrange
        var ranges = BoundedIntegerSequenceCodec.MaxRanges;
        int rangeIndex = 0;

        // Act & Assert
        for (int desiredRange = 1; desiredRange < 256; ++desiredRange)
        {
            while (rangeIndex + 1 < ranges.Length && ranges[rangeIndex + 1] <= desiredRange)
                ++rangeIndex;

            int expectedRange = ranges[rangeIndex];

            // Test CE values
            if (desiredRange >= Quantization.kEndpointRangeMinValue)
            {
                var testValues = new[] { 0, 13, 173, 208, 255 };
                foreach (var value in testValues)
                {
                    Quantization.QuantizeCEValueToRange(value, desiredRange)
                        .Should().Be(Quantization.QuantizeCEValueToRange(value, expectedRange));
                }
            }

            // Test weight values
            if (desiredRange <= Quantization.kWeightRangeMaxValue)
            {
                var testValues = new[] { 0, 12, 23, 63 };
                foreach (var value in testValues)
                {
                    Quantization.QuantizeWeightToRange(value, desiredRange)
                        .Should().Be(Quantization.QuantizeWeightToRange(value, expectedRange));
                }
            }
        }

        rangeIndex.Should().Be(ranges.Length - 1);
    }

    #endregion

    #region Identity Tests

    [Fact]
    public void QuantizeCEValueToRange_WithRange255_ShouldBeIdentity()
    {
        // Act & Assert
        for (int value = 0; value < 256; ++value)
        {
            Quantization.QuantizeCEValueToRange(value, 255).Should().Be(value);
        }
    }

    #endregion

    #region Monotonicity Tests

    [Fact]
    public void QuantizeCEValueToRange_ShouldBeMonotonicIncreasing()
    {
        // Act & Assert
        for (int numBits = 3; numBits < 8; ++numBits)
        {
            int range = (1 << numBits) - 1;
            int lastQuantizedValue = -1;

            for (int value = 0; value < 256; ++value)
            {
                int quantizedValue = Quantization.QuantizeCEValueToRange(value, range);

                quantizedValue.Should().BeGreaterOrEqualTo(lastQuantizedValue);
                lastQuantizedValue = quantizedValue;
            }

            lastQuantizedValue.Should().Be(range);
        }
    }

    [Fact]
    public void QuantizeWeightToRange_ShouldBeMonotonicIncreasing()
    {
        // Act & Assert
        for (int numBits = 3; numBits < 8; ++numBits)
        {
            int range = (1 << numBits) - 1;

            if (range > Quantization.kWeightRangeMaxValue)
                continue;

            int lastQuantizedValue = -1;

            for (int value = 0; value <= 64; ++value)
            {
                int quantizedValue = Quantization.QuantizeWeightToRange(value, range);

                quantizedValue.Should().BeGreaterOrEqualTo(lastQuantizedValue);
                lastQuantizedValue = quantizedValue;
            }

            lastQuantizedValue.Should().Be(range);
        }
    }

    #endregion

    #region Small Bit Packing Tests

    [Fact]
    public void QuantizeCEValueToRange_WithSmallBitRanges_ShouldQuantizeLowValuesToZero()
    {
        // Act & Assert
        for (int numBits = 1; numBits <= 8; ++numBits)
        {
            int range = (1 << numBits) - 1;

            if (range < Quantization.kEndpointRangeMinValue)
                continue;

            const int cevBits = 8;
            int halfMaxQuantBits = Math.Max(0, cevBits - numBits - 1);
            int largestCevToZero = (1 << halfMaxQuantBits) - 1;

            Quantization.QuantizeCEValueToRange(largestCevToZero, range).Should().Be(0);
        }
    }

    [Fact]
    public void QuantizeWeightToRange_WithSmallBitRanges_ShouldQuantizeLowValuesToZero()
    {
        // Act & Assert
        for (int numBits = 1; numBits <= 8; ++numBits)
        {
            int range = (1 << numBits) - 1;

            if (range > Quantization.kWeightRangeMaxValue)
                continue;

            const int weightBits = 6;
            int halfMaxQuantBits = Math.Max(0, weightBits - numBits - 1);
            int largestWeightToZero = (1 << halfMaxQuantBits) - 1;

            Quantization.QuantizeWeightToRange(largestWeightToZero, range).Should().Be(0);
        }
    }

    #endregion

    #region Specific Quint/Trit Packing Tests

    [Fact]
    public void UnquantizeWeightFromRange_WithQuintRange_ShouldMatchExpected()
    {
        // Arrange
        var values = new List<int> { 4, 6, 4, 6, 7, 5, 7, 5 };
        var quintExpected = new List<int> { 14, 21, 14, 21, 43, 50, 43, 50 };

        // Act
        var quantized = values.Select(v => Quantization.UnquantizeWeightFromRange(v, 9)).ToList();

        // Assert
        quantized.Should().Equal(quintExpected);
    }

    [Fact]
    public void UnquantizeWeightFromRange_WithTritRange_ShouldMatchExpected()
    {
        // Arrange
        var values = new List<int> { 4, 6, 4, 6, 7, 5, 7, 5 };
        var tritExpected = new List<int> { 5, 23, 5, 23, 41, 59, 41, 59 };

        // Act
        var quantized = values.Select(v => Quantization.UnquantizeWeightFromRange(v, 11)).ToList();

        // Assert
        quantized.Should().Equal(tritExpected);
    }

    #endregion

    #region Invalid Input Tests

    [Fact]
    public void QuantizeCEValueToRange_WithInvalidMinRange_ShouldThrowArgumentOutOfRangeException()
    {
        // Act & Assert
        for (int range = 0; range < Quantization.kEndpointRangeMinValue; ++range)
        {
            var action = () => Quantization.QuantizeCEValueToRange(0, range);
            action.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Fact]
    public void UnquantizeCEValueFromRange_WithInvalidMinRange_ShouldThrowArgumentOutOfRangeException()
    {
        // Act & Assert
        for (int range = 0; range < Quantization.kEndpointRangeMinValue; ++range)
        {
            var action = () => Quantization.UnquantizeCEValueFromRange(0, range);
            action.Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    [Fact]
    public void QuantizeWeightToRange_WithZeroRange_ShouldThrowArgumentOutOfRangeException()
    {
        // Act
        var action = () => Quantization.QuantizeWeightToRange(0, 0);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnquantizeWeightFromRange_WithZeroRange_ShouldThrowArgumentOutOfRangeException()
    {
        // Act
        var action = () => Quantization.UnquantizeWeightFromRange(0, 0);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(256, 7)]
    [InlineData(10000, 17)]
    public void QuantizeCEValueToRange_WithInvalidValue_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.QuantizeCEValueToRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(8, 7)]
    [InlineData(-1000, 17)]
    public void UnquantizeCEValueFromRange_WithInvalidValue_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.UnquantizeCEValueFromRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, -7)]
    [InlineData(0, 257)]
    public void QuantizeCEValueToRange_WithInvalidRange_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.QuantizeCEValueToRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, -17)]
    [InlineData(0, 256)]
    public void UnquantizeCEValueFromRange_WithInvalidRange_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.UnquantizeCEValueFromRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(256, 7)]
    [InlineData(10000, 17)]
    public void QuantizeWeightToRange_WithInvalidValue_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.QuantizeWeightToRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(8, 7)]
    [InlineData(-1000, 17)]
    public void UnquantizeWeightFromRange_WithInvalidValue_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.UnquantizeWeightFromRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, -7)]
    [InlineData(0, 32)]
    public void QuantizeWeightToRange_WithInvalidRange_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.QuantizeWeightToRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, -17)]
    [InlineData(0, 64)]
    public void UnquantizeWeightFromRange_WithInvalidRange_ShouldThrowArgumentOutOfRangeException(int value, int range)
    {
        // Act
        var action = () => Quantization.UnquantizeWeightFromRange(value, range);

        // Assert
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion
}
