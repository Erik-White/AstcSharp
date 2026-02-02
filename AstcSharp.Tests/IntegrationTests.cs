using AstcSharp.IO;
using FluentAssertions;

namespace AstcSharp.Tests;

public class IntegrationTests
{
    [Fact]
    public void DecompressToImage_WithAllTestdataFiles_ShouldDecodeSuccessfully()
    {
        // Arrange
        string testdataDir = Path.Combine("TestData", "Input");
        Directory.Exists(testdataDir).Should().BeTrue($"Testdata directory should exist: {testdataDir}");

        var files = Directory.GetFiles(testdataDir, "*.astc");
        files.Should().NotBeEmpty("testdata directory should contain ASTC files");

        // Act & Assert
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var astc = AstcFile.FromMemory(bytes);

            var result = AstcDecoder.DecompressToImage(astc);

            result.Length.Should().BeGreaterThan(0, $"decoding should succeed for {Path.GetFileName(file)}");
        }
    }
}
