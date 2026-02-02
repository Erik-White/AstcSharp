using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using FluentAssertions;

namespace AstcSharp.Tests;

public class EndpointCodecTests
{
    #region Helper Methods

    private static (RgbaColor Low, RgbaColor High) EncodeAndDecodeColors(
        RgbaColor low, RgbaColor high, int quantRange, EndpointEncodingMode mode)
    {
        var values = new List<int>();
        var needsSwap = EndpointCodec.EncodeColorsForMode(low, high, quantRange, mode, out var astcMode, values);
        var (decLow, decHigh) = EndpointCodec.DecodeColorsForMode(values, quantRange, astcMode);

        return needsSwap ? (decHigh, decLow) : (decLow, decHigh);
    }

    private static bool ColorsAreEqual(RgbaColor a, RgbaColor b)
        => a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];

    private static bool ColorsAreClose(RgbaColor a, RgbaColor b, int tolerance)
        => Math.Abs(a[0] - b[0]) <= tolerance &&
           Math.Abs(a[1] - b[1]) <= tolerance &&
           Math.Abs(a[2] - b[2]) <= tolerance &&
           Math.Abs(a[3] - b[3]) <= tolerance;

    #endregion

    #region Quantization Range Tests

    [Fact]
    public void EncodeColorsForMode_WithVariousRanges_ShouldProduceValidQuantizedValues()
    {
        // Arrange
        var modes = new[]
        {
            EndpointEncodingMode.DirectLuma,
            EndpointEncodingMode.DirectLumaAlpha,
            EndpointEncodingMode.BaseScaleRgb,
            EndpointEncodingMode.BaseScaleRgba,
            EndpointEncodingMode.DirectRbg,
            EndpointEncodingMode.DirectRgba
        };

        var low = new RgbaColor(0, 0, 0, 0);
        var high = new RgbaColor(255, 255, 255, 255);

        // Act & Assert
        foreach (var mode in modes)
        {
            for (int quantRange = 5; quantRange < 256; quantRange++)
            {
                var values = new List<int>();
                EndpointCodec.EncodeColorsForMode(low, high, quantRange, mode, out var astcMode, values);

                // Assert value count matches expected
                values.Should().HaveCount(mode.GetValuesCount());

                // Assert all values are within quantization range
                values.Should().AllSatisfy(v => v.Should().BeInRange(0, quantRange));
            }
        }
    }

    #endregion

    #region Extreme Value Tests

    [Fact]
    public void EncodeDecodeColors_WithBlackAndWhite_ShouldPreserveColors()
    {
        // Arrange
        var modes = new[]
        {
            EndpointEncodingMode.DirectLuma,
            EndpointEncodingMode.DirectLumaAlpha,
            EndpointEncodingMode.BaseScaleRgb,
            EndpointEncodingMode.BaseScaleRgba,
            EndpointEncodingMode.DirectRbg,
            EndpointEncodingMode.DirectRgba
        };

        var white = new RgbaColor(255, 255, 255, 255);
        var black = new RgbaColor(0, 0, 0, 255);

        // Act & Assert
        foreach (var mode in modes)
        {
            for (int quantRange = 5; quantRange < 256; ++quantRange)
            {
                var (low, high) = EncodeAndDecodeColors(white, black, quantRange, mode);

                ColorsAreEqual(low, white).Should().BeTrue();
                ColorsAreEqual(high, black).Should().BeTrue();
            }
        }
    }

    #endregion

    #region Blue Contract Tests

    [Fact]
    public void UsesBlueContract_WithDirectModes_ShouldDetectCorrectly()
    {
        // Arrange
        var values = new List<int> { 132, 127, 116, 112, 183, 180, 31, 22 };

        // Act & Assert
        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbDirect, values).Should().BeTrue();
        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbaDirect, values).Should().BeTrue();
    }

    [Fact]
    public void UsesBlueContract_WithOffsetModes_ShouldDetectBasedOnBitFlags()
    {
        // Arrange
        var baseValues = new List<int> { 132, 127, 116, 112, 183, 180, 31, 22 };

        // Act & Assert - with bit 6 cleared (should return false)
        var valuesClearedBit6 = new List<int>(baseValues);
        valuesClearedBit6[1] &= 0xBF;
        valuesClearedBit6[3] &= 0xBF;
        valuesClearedBit6[5] &= 0xBF;
        valuesClearedBit6[7] &= 0xBF;

        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbBaseOffset, valuesClearedBit6).Should().BeFalse();
        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbaBaseOffset, valuesClearedBit6).Should().BeFalse();

        // Act & Assert - with bit 6 set (should return true)
        var valuesSetBit6 = new List<int>(baseValues);
        valuesSetBit6[1] |= 0x40;
        valuesSetBit6[3] |= 0x40;
        valuesSetBit6[5] |= 0x40;
        valuesSetBit6[7] |= 0x40;

        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbBaseOffset, valuesSetBit6).Should().BeTrue();
        EndpointCodec.UsesBlueContract(255, ColorEndpointMode.LdrRgbaBaseOffset, valuesSetBit6).Should().BeTrue();
    }

    [Fact]
    public void EncodeColorsForMode_WithRgbDirectAndSpecificPairs_ShouldUseBlueContract()
    {
        // Arrange
        var pairs = new[]
        {
            (new RgbaColor(22, 18, 30, 59), new RgbaColor(162, 148, 155, 59)),
            (new RgbaColor(22, 30, 27, 36), new RgbaColor(228, 221, 207, 36)),
            (new RgbaColor(54, 60, 55, 255), new RgbaColor(23, 30, 27, 255))
        };

        const int endpointRange = 31;

        // Act & Assert
        foreach (var (low, high) in pairs)
        {
            var values = new List<int>();
            EndpointCodec.EncodeColorsForMode(low, high, endpointRange, EndpointEncodingMode.DirectRbg, out var astcMode, values);

            EndpointCodec.UsesBlueContract(endpointRange, astcMode, values).Should().BeTrue();
        }
    }

    #endregion

    #region Luma Direct Tests

    [Fact]
    public void EncodeDecodeColors_WithLumaDirect_ShouldProduceLumaValues()
    {
        // Arrange
        var mode = EndpointEncodingMode.DirectLuma;

        // Act & Assert - Test case 1
        var result1 = EncodeAndDecodeColors(
            new RgbaColor(247, 248, 246, 255),
            new RgbaColor(2, 3, 1, 255),
            255, mode);

        ColorsAreEqual(result1.Low, new RgbaColor(247, 247, 247, 255)).Should().BeTrue();
        ColorsAreEqual(result1.High, new RgbaColor(2, 2, 2, 255)).Should().BeTrue();

        // Act & Assert - Test case 2
        var result2 = EncodeAndDecodeColors(
            new RgbaColor(80, 80, 50, 255),
            new RgbaColor(99, 255, 6, 255),
            255, mode);

        ColorsAreEqual(result2.Low, new RgbaColor(70, 70, 70, 255)).Should().BeTrue();
        ColorsAreEqual(result2.High, new RgbaColor(120, 120, 120, 255)).Should().BeTrue();

        // Act & Assert - Test case 3 (lower quantization)
        var result3 = EncodeAndDecodeColors(
            new RgbaColor(247, 248, 246, 255),
            new RgbaColor(2, 3, 1, 255),
            15, mode);

        ColorsAreEqual(result3.Low, new RgbaColor(255, 255, 255, 255)).Should().BeTrue();
        ColorsAreEqual(result3.High, new RgbaColor(0, 0, 0, 255)).Should().BeTrue();

        // Act & Assert - Test case 4
        var result4 = EncodeAndDecodeColors(
            new RgbaColor(64, 127, 192, 255),
            new RgbaColor(0, 0, 0, 255),
            63, mode);

        ColorsAreEqual(result4.Low, new RgbaColor(130, 130, 130, 255)).Should().BeTrue();
        ColorsAreEqual(result4.High, new RgbaColor(0, 0, 0, 255)).Should().BeTrue();
    }

    #endregion

    #region Luma-Alpha Direct Tests

    [Fact]
    public void EncodeDecodeColors_WithLumaAlphaDirect_ShouldPreserveLumaAndAlpha()
    {
        // Arrange
        var mode = EndpointEncodingMode.DirectLumaAlpha;

        // Act - Grey with varying alpha
        var result1 = EncodeAndDecodeColors(
            new RgbaColor(64, 127, 192, 127),
            new RgbaColor(0, 0, 0, 20),
            63, mode);

        // Assert
        (ColorsAreEqual(result1.Low, new RgbaColor(130, 130, 130, 125)) ||
         ColorsAreClose(result1.Low, new RgbaColor(130, 130, 130, 125), 1)).Should().BeTrue();
        (ColorsAreEqual(result1.High, new RgbaColor(0, 0, 0, 20)) ||
         ColorsAreClose(result1.High, new RgbaColor(0, 0, 0, 20), 1)).Should().BeTrue();

        // Act - Different alpha values
        var result2 = EncodeAndDecodeColors(
            new RgbaColor(247, 248, 246, 250),
            new RgbaColor(2, 3, 1, 172),
            255, mode);

        // Assert
        ColorsAreEqual(result2.Low, new RgbaColor(247, 247, 247, 250)).Should().BeTrue();
        ColorsAreEqual(result2.High, new RgbaColor(2, 2, 2, 172)).Should().BeTrue();
    }

    #endregion

    #region RGB Direct Tests

    [Fact]
    public void EncodeDecodeColors_WithRgbDirectAndRandomColors_ShouldPreserveColors()
    {
        // Arrange
        var mode = EndpointEncodingMode.DirectRbg;
        var random = new Random(unchecked((int)0xdeadbeef));

        // Act & Assert - Random colors
        for (int i = 0; i < 100; ++i)
        {
            var low = new RgbaColor(random.Next(0, 256), random.Next(0, 256), random.Next(0, 256), 255);
            var high = new RgbaColor(random.Next(0, 256), random.Next(0, 256), random.Next(0, 256), 255);
            var result = EncodeAndDecodeColors(low, high, 255, mode);

            ColorsAreEqual(result.Low, low).Should().BeTrue();
            ColorsAreEqual(result.High, high).Should().BeTrue();
        }
    }

    [Fact]
    public void EncodeDecodeColors_WithRgbDirectAndSpecificColors_ShouldMatchExpected()
    {
        // Arrange
        var mode = EndpointEncodingMode.DirectRbg;

        // Act & Assert - Test case 1
        var result1 = EncodeAndDecodeColors(
            new RgbaColor(64, 127, 192, 255),
            new RgbaColor(0, 0, 0, 255),
            63, mode);

        ColorsAreEqual(result1.Low, new RgbaColor(65, 125, 190, 255)).Should().BeTrue();
        ColorsAreEqual(result1.High, new RgbaColor(0, 0, 0, 255)).Should().BeTrue();

        // Act & Assert - Test case 2 (reversed)
        var result2 = EncodeAndDecodeColors(
            new RgbaColor(0, 0, 0, 255),
            new RgbaColor(64, 127, 192, 255),
            63, mode);

        ColorsAreEqual(result2.Low, new RgbaColor(0, 0, 0, 255)).Should().BeTrue();
        ColorsAreEqual(result2.High, new RgbaColor(65, 125, 190, 255)).Should().BeTrue();
    }

    #endregion

    #region RGB Base Scale Tests

    [Fact]
    public void EncodeDecodeColors_WithRgbBaseScaleAndIdenticalColors_ShouldBeCloseToOriginal()
    {
        // Arrange
        var mode = EndpointEncodingMode.BaseScaleRgb;
        var random = new Random(unchecked((int)0xdeadbeef));

        // Act & Assert - Identical colors should encode with scale ~255, within tolerance of 1
        for (int i = 0; i < 100; ++i)
        {
            var color = new RgbaColor(random.Next(0, 256), random.Next(0, 256), random.Next(0, 256), 255);
            var result = EncodeAndDecodeColors(color, color, 255, mode);

            ColorsAreClose(result.Low, color, 1).Should().BeTrue();
            ColorsAreClose(result.High, color, 1).Should().BeTrue();
        }
    }

    [Fact]
    public void EncodeDecodeColors_WithRgbBaseScaleAndDifferentColors_ShouldMatchExpected()
    {
        // Arrange
        var mode = EndpointEncodingMode.BaseScaleRgb;
        var low = new RgbaColor(20, 4, 40, 255);
        var high = new RgbaColor(80, 16, 160, 255);

        // Act & Assert - High quantization (255) should be exact
        var result1 = EncodeAndDecodeColors(low, high, 255, mode);
        ColorsAreClose(result1.Low, low, 0).Should().BeTrue();
        ColorsAreClose(result1.High, high, 0).Should().BeTrue();

        // Act & Assert - Lower quantization (127) should be close
        var result2 = EncodeAndDecodeColors(low, high, 127, mode);
        ColorsAreClose(result2.Low, low, 1).Should().BeTrue();
        ColorsAreClose(result2.High, high, 1).Should().BeTrue();
    }

    #endregion

    #region RGB Base Offset Tests

    [Fact]
    public void DecodeColorsForMode_WithRgbBaseOffset_ShouldDecodeCorrectly()
    {
        // Helper to test decoding
        void TestDecoding(RgbaColor expectedLow, RgbaColor expectedHigh)
        {
            var values = new List<int>();
            for (int i = 0; i < 3; ++i)
            {
                bool isLarge = expectedLow[i] >= 128;
                values.Add((expectedLow[i] * 2) & 0xFF);
                int diff = (expectedHigh[i] - expectedLow[i]) * 2;
                if (isLarge) diff |= 0x80;
                values.Add(diff);
            }

            var (decLow, decHigh) = EndpointCodec.DecodeColorsForMode(values, 255, ColorEndpointMode.LdrRgbBaseOffset);

            ColorsAreEqual(decLow, expectedLow).Should().BeTrue();
            ColorsAreEqual(decHigh, expectedHigh).Should().BeTrue();
        }

        // Act & Assert - Specific test cases
        TestDecoding(new RgbaColor(80, 16, 112, 255), new RgbaColor(87, 18, 132, 255));
        TestDecoding(new RgbaColor(80, 74, 82, 255), new RgbaColor(90, 92, 110, 255));
        TestDecoding(new RgbaColor(0, 0, 0, 255), new RgbaColor(2, 2, 2, 255));

        // Act & Assert - Random identical endpoints (even channels only)
        var random = new Random(unchecked((int)0xdeadbeef));
        for (int i = 0; i < 100; ++i)
        {
            int r = random.Next(0, 256);
            int g = random.Next(0, 256);
            int b = random.Next(0, 256);

            // Ensure even channels (reference test skips odd)
            if (((r | g | b) & 1) != 0) continue;

            var color = new RgbaColor(r, g, b, 255);
            TestDecoding(color, color);
        }
    }

    #endregion
}
