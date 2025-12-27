namespace AstcSharp.Tests;

public class IntegrationTests
{
    [Fact]
    public void DecodeAllTestdataFiles()
    {
        string testdataDir = Path.Combine("TestData", "Input");
        Assert.True(Directory.Exists(testdataDir), $"Testdata directory not found: {testdataDir}");

        var files = Directory.GetFiles(testdataDir, "*.astc");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var astc = AstcFile.LoadFromMemory(bytes, out var err);
            Assert.Null(err);
            Assert.NotNull(astc);
            Assert.True(astc.GetFootprint().HasValue, $"Unknown footprint for {file}");

            var result = Codec.DecompressToImage(astc);
            Assert.True(result.Length > 0, $"Decoding failed for {Path.GetFileName(file)}");
        }
    }
}
