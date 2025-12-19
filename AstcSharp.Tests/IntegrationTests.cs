using System;
using System.IO;
using Xunit;
using AstcSharp;

namespace AstcSharp.Tests
{
    public class IntegrationTests
    {
        [Fact]
        public void DecodeAllTestdataFiles()
        {
            string testdataDir = Path.Combine("AstcSharp.Reference", "astc-codec", "src", "decoder", "testdata");
            Assert.True(Directory.Exists(testdataDir), $"Testdata directory not found: {testdataDir}");

            var files = Directory.GetFiles(testdataDir, "*.astc");
            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                var astc = AstcFile.LoadFromMemory(bytes, out var err);
                Assert.Null(err);
                Assert.NotNull(astc);
                var width = astc.GetWidth();
                var height = astc.GetHeight();
                var fpOpt = astc.GetFootprint();
                Assert.True(fpOpt.HasValue, $"Unknown footprint for {file}");

                var fp = fpOpt.Value;
                var outbuf = new byte[width * height * 4];
                var ok = Codec.DecompressToImage(astc, outbuf, outbuf.Length, width * 4);
                Assert.True(ok, $"Decoding failed for {Path.GetFileName(file)}");
            }
        }
    }
}
