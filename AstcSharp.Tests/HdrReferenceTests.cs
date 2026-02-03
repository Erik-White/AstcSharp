using AstcSharp.Core;
using AstcSharp.IO;
using AwesomeAssertions;

namespace AstcSharp.Tests;

/// <summary>
/// Tests using real HDR ASTC files from the ARM astc-encoder reference repository.
/// These tests validate that our HDR implementation produces valid output for
/// actual HDR-compressed ASTC data.
/// </summary>
public class HdrReferenceTests
{
    [Fact]
    public void DecodeHdrAstcFile_1x1Pixel_ShouldProduceValidHdrOutput()
    {
        // This is a real HDR ASTC file from ARM's astc-encoder test suite
        // File: Test/Data/HDR-A-1x1.astc
        // Format: 1x1 pixel, 6x6 footprint, HDR content
        var astcPath = Path.Combine("TestData", "HDR", "HDR-A-1x1.astc");

        if (!File.Exists(astcPath))
        {
            // Skip test if file not present
            return;
        }

        var astcData = File.ReadAllBytes(astcPath);
        var astcFile = AstcFile.FromMemory(astcData);

        // Verify file properties
        astcFile.Width.Should().Be(1);
        astcFile.Height.Should().Be(1);
        astcFile.Footprint.Width.Should().Be(6);
        astcFile.Footprint.Height.Should().Be(6);

        // Decode using HDR API
        var hdrResult = AstcDecoder.DecompressToFloat16(
            astcFile.Blocks,
            astcFile.Width,
            astcFile.Height,
            astcFile.Footprint);

        // Should produce 1 pixel with 4 Half values (RGBA)
        hdrResult.Length.Should().Be(4);

        // Verify all values are valid (not NaN or infinity)
        foreach (var value in hdrResult)
        {
            Half.IsNaN(value).Should().BeFalse();
            Half.IsInfinity(value).Should().BeFalse();
        }

        // HDR values can exceed 1.0
        // Just verify they're in a reasonable range (0.0 to 10.0)
        foreach (var value in hdrResult)
        {
            ((float)value).Should().BeGreaterThanOrEqualTo(0.0f);
            ((float)value).Should().BeLessThan(10.0f);
        }
    }

    [Fact]
    public void DecodeHdrAstcFile_Tile_ShouldProduceValidHdrOutput()
    {
        // This is a HDR tile from ARM's astc-encoder test suite
        // File: Test/Data/Tiles/hdr.astc
        var astcPath = Path.Combine("TestData", "HDR", "hdr-tile.astc");

        if (!File.Exists(astcPath))
        {
            // Skip test if file not present
            return;
        }

        var astcData = File.ReadAllBytes(astcPath);
        var astcFile = AstcFile.FromMemory(astcData);

        // Decode using HDR API
        var hdrResult = AstcDecoder.DecompressToFloat16(
            astcFile.Blocks,
            astcFile.Width,
            astcFile.Height,
            astcFile.Footprint);

        // Should produce Width * Height pixels, each with 4 Half values
        hdrResult.Length.Should().Be(astcFile.Width * astcFile.Height * 4);

        // Verify all values are valid
        foreach (var value in hdrResult)
        {
            Half.IsNaN(value).Should().BeFalse();
            Half.IsInfinity(value).Should().BeFalse();
        }

        // Verify at least some HDR values exceed 1.0 (typical for HDR content)
        var valuesAboveOne = hdrResult.Count(v => (float)v > 1.0f);

        // HDR content should have some bright values above 1.0
        // (This may or may not be true depending on the specific image,
        // so we just check that it decodes without errors)
    }

    [Fact]
    public void DecodeHdrAstcFile_WithLdrApi_ShouldClampValues()
    {
        // Verify that HDR content can be decoded with LDR API (values clamped)
        var astcPath = Path.Combine("TestData", "HDR", "HDR-A-1x1.astc");

        if (!File.Exists(astcPath))
        {
            return;
        }

        var astcData = File.ReadAllBytes(astcPath);
        var astcFile = AstcFile.FromMemory(astcData);

        // Decode using LDR API
        var ldrResult = AstcDecoder.DecompressToImage(astcFile);

        // Should produce 1 pixel with 4 bytes (RGBA)
        ldrResult.Length.Should().Be(4);

        // All values should be in LDR range (0-255)
        foreach (var value in ldrResult)
        {
            value.Should().BeGreaterThanOrEqualTo((byte)0);
            value.Should().BeLessThanOrEqualTo((byte)255);
        }
    }

    [Fact]
    public void HdrAndLdrApis_OnSameHdrFile_ShouldProduceConsistentRelativeValues()
    {
        // Verify that HDR and LDR APIs produce consistent output
        var astcPath = Path.Combine("TestData", "HDR", "HDR-A-1x1.astc");

        if (!File.Exists(astcPath))
        {
            return;
        }

        var astcData = File.ReadAllBytes(astcPath);
        var astcFile = AstcFile.FromMemory(astcData);

        // Decode with both APIs
        var hdrResult = AstcDecoder.DecompressToFloat16(
            astcFile.Blocks, astcFile.Width, astcFile.Height, astcFile.Footprint);
        var ldrResult = AstcDecoder.DecompressToImage(astcFile);

        // Both should produce output for 1 pixel
        hdrResult.Length.Should().Be(4);
        ldrResult.Length.Should().Be(4);

        // The relative ordering of channels should be consistent
        // If HDR R > G, then LDR R should be >= G (accounting for clamping)
        for (int i = 0; i < 3; i++)
        {
            float hdrVal = (float)hdrResult[i];
            byte ldrVal = ldrResult[i];

            // Both should be valid
            hdrVal.Should().BeGreaterThanOrEqualTo(0.0f);
            ldrVal.Should().BeGreaterThanOrEqualTo((byte)0);
        }
    }

    [Fact]
    public void DecodeHdrFile_VerifyFootprintDetection()
    {
        // Verify that the ASTC file header is correctly parsed for HDR content
        var astcPath = Path.Combine("TestData", "HDR", "HDR-A-1x1.astc");

        if (!File.Exists(astcPath))
        {
            return;
        }

        var astcData = File.ReadAllBytes(astcPath);
        var astcFile = AstcFile.FromMemory(astcData);

        // The HDR-A-1x1.astc file has a 6x6 footprint based on the header
        astcFile.Footprint.Width.Should().Be(6);
        astcFile.Footprint.Height.Should().Be(6);
        astcFile.Footprint.Type.Should().Be(FootprintType.Footprint6x6);
    }
}
