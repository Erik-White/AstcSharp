using System;
using System.IO;
using Xunit;
using AstcSharp;
using AstcSharp.Tools;

namespace AstcSharp.Tests
{
    public class AstcInspectorTests
    {
        [Fact]
        public void InspectAtlasSmall6x6()
        {
            var path = Path.Combine("AstcSharp.Reference", "astc-codec", "src", "decoder", "testdata", "atlas_small_6x6.astc");
            var data = File.ReadAllBytes(path);
            var summary = AstcInspector.Inspect(data);
            Assert.Contains("W=256", summary);
            Assert.Contains("H=256", summary);
            Assert.Contains("Blocks=", summary);
        }
    }
}
