using AstcSharp.Core;
using AstcSharp.IO;
using AwesomeAssertions;

namespace AstcSharp.Tests;

public class HdrIntegrationTests
{
    [Fact]
    public void DecompressToFloat16_WithValidBlock_ShouldProduceCorrectOutputSize()
    {
        // Create a simple 4x4 block (16 bytes)
        var astcData = new byte[16];

        var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        // Decompress using HDR API
        var hdrResult = AstcDecoder.DecompressToFloat16(astcData, 4, 4, footprint);

        // Verify output size: 4x4 pixels, 4 Half values (RGBA) per pixel
        hdrResult.Length.Should().Be(4 * 4 * 4); // 64 Half values total

        // Verify all values are valid Half numbers (not NaN or infinity)
        foreach (var value in hdrResult)
        {
            Half.IsNaN(value).Should().BeFalse();
            Half.IsInfinity(value).Should().BeFalse();
            // Values should be in reasonable range for normalized colors
            ((float)value).Should().BeGreaterThanOrEqualTo(0.0f);
            ((float)value).Should().BeLessThanOrEqualTo(1.1f); // Allow slight overshoot for HDR
        }
    }

    [Fact]
    public void DecompressToFloat16_WithDifferentFootprints_ShouldWork()
    {
        // Test that HDR API works with various footprint types
        var footprints = new[]
        {
            FootprintType.Footprint4x4,
            FootprintType.Footprint5x5,
            FootprintType.Footprint6x6,
            FootprintType.Footprint8x8
        };

        foreach (var footprint in footprints)
        {
            // Create a simple test: 1 block (footprint size) of zeros
            var fp = Footprint.FromFootprintType(footprint);
            var astcData = new byte[16]; // One ASTC block (all zeros = void extent block)

            var result = AstcDecoder.ASTCDecompressToFloat16(astcData, fp.Width, fp.Height, footprint);

            // Should produce footprint.Width * footprint.Height pixels, each with 4 Half values
            result.Length.Should().Be(fp.Width * fp.Height * 4);
        }
    }

    [Fact]
    public void ASTCDecompressToFloat16_WithInvalidData_ShouldReturnEmpty()
    {
        var emptyData = Array.Empty<byte>();

        var result = AstcDecoder.ASTCDecompressToFloat16(emptyData, 64, 64, FootprintType.Footprint4x4);

        result.Length.Should().Be(0);
    }

    [Fact]
    public void DecompressToFloat16_WithZeroDimensions_ShouldReturnEmpty()
    {
        var astcData = new byte[16];
        var footprint = Footprint.FromFootprintType(FootprintType.Footprint4x4);

        var result = AstcDecoder.DecompressToFloat16(astcData, 0, 0, footprint);

        result.Length.Should().Be(0);
    }

    [Fact]
    public void HdrColor_Conversions_ShouldMaintainPrecision()
    {
        // Test round-trip conversions
        var hdrColor = new HdrColor(0, 32767, 65535, 16383);

        // Convert to Half array
        var halfArray = hdrColor.ToHalfArray();
        halfArray.Length.Should().Be(4);

        // Verify normalized values
        ((float)halfArray[0]).Should().BeApproximately(0.0f, 0.001f);
        ((float)halfArray[1]).Should().BeApproximately(0.5f, 0.001f);
        ((float)halfArray[2]).Should().BeApproximately(1.0f, 0.001f);
        ((float)halfArray[3]).Should().BeApproximately(0.25f, 0.001f);

        // Round-trip back
        var reconstructed = HdrColor.FromHalfArray(halfArray);
        reconstructed.R.Should().Be((ushort)0);
        Math.Abs(reconstructed.G - 32767).Should().BeLessThanOrEqualTo(10);
        reconstructed.B.Should().Be((ushort)65535);
        Math.Abs(reconstructed.A - 16383).Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public void HdrColor_LdrRoundTrip_ShouldPreserveValues()
    {
        var ldrColor = new RgbaColor(50, 100, 150, 200);

        var hdrColor = HdrColor.FromLdr(ldrColor);
        var backToLdr = hdrColor.ToLdr();

        backToLdr.R.Should().Be(ldrColor.R);
        backToLdr.G.Should().Be(ldrColor.G);
        backToLdr.B.Should().Be(ldrColor.B);
        backToLdr.A.Should().Be(ldrColor.A);
    }
}
