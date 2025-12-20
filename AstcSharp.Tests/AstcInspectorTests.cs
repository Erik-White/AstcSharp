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
            var data = File.ReadAllBytes(Path.Combine("TestData", "Input", "atlas_small_6x6.astc"));
            var summary = AstcInspector.Inspect(data);
            Assert.Contains("W=256", summary);
            Assert.Contains("H=256", summary);
            Assert.Contains("Blocks=", summary);
        }
    }
}
