using Xunit;
using AstcSharp.Reference;

namespace AstcSharp.Tests
{
    public class FootprintTests
    {
        [Fact]
        public void ParseAstcFootprintString()
        {
            var valid = new (string, Footprint)[] {
                ("4x4", Footprint.Get4x4()),
                ("5x4", Footprint.Get5x4()),
                ("5x5", Footprint.Get5x5()),
                ("6x5", Footprint.Get6x5()),
                ("6x6", Footprint.Get6x6()),
                ("8x5", Footprint.Get8x5()),
                ("8x6", Footprint.Get8x6()),
                ("8x8", Footprint.Get8x8()),
                ("10x5", Footprint.Get10x5()),
                ("10x6", Footprint.Get10x6()),
                ("10x8", Footprint.Get10x8()),
                ("10x10", Footprint.Get10x10()),
                ("12x10", Footprint.Get12x10()),
                ("12x12", Footprint.Get12x12())
            };

            foreach (var (s, fp) in valid)
            {
                var parsed = FootprintParser.Parse(s);
                Assert.True(parsed.HasValue);
                Assert.Equal(fp, parsed.Value);
            }

            // Some invalid cases
            Assert.False(FootprintParser.Parse("").HasValue);
            Assert.False(FootprintParser.Parse("3").HasValue);
            Assert.False(FootprintParser.Parse("x").HasValue);
            Assert.False(FootprintParser.Parse("9999999999x10").HasValue);
            Assert.False(FootprintParser.Parse("10x4").HasValue);
        }

        [Fact]
        public void Bitrates()
        {
            Assert.InRange(Footprint.Get4x4().Bitrate(), 8f - 0.01f, 8f + 0.01f);
            Assert.InRange(Footprint.Get5x4().Bitrate(), 6.4f - 0.01f, 6.4f + 0.01f);
            Assert.InRange(Footprint.Get5x5().Bitrate(), 5.12f - 0.01f, 5.12f + 0.01f);
            Assert.InRange(Footprint.Get6x5().Bitrate(), 4.27f - 0.01f, 4.27f + 0.01f);
            Assert.InRange(Footprint.Get6x6().Bitrate(), 3.56f - 0.01f, 3.56f + 0.01f);
            Assert.InRange(Footprint.Get8x5().Bitrate(), 3.20f - 0.01f, 3.20f + 0.01f);
            Assert.InRange(Footprint.Get8x6().Bitrate(), 2.67f - 0.01f, 2.67f + 0.01f);
            Assert.InRange(Footprint.Get8x8().Bitrate(), 2.00f - 0.01f, 2.00f + 0.01f);
            Assert.InRange(Footprint.Get10x5().Bitrate(), 2.56f - 0.01f, 2.56f + 0.01f);
            Assert.InRange(Footprint.Get10x6().Bitrate(), 2.13f - 0.01f, 2.13f + 0.01f);
            Assert.InRange(Footprint.Get10x8().Bitrate(), 1.60f - 0.01f, 1.60f + 0.01f);
            Assert.InRange(Footprint.Get10x10().Bitrate(), 1.28f - 0.01f, 1.28f + 0.01f);
            Assert.InRange(Footprint.Get12x10().Bitrate(), 1.07f - 0.01f, 1.07f + 0.01f);
            Assert.InRange(Footprint.Get12x12().Bitrate(), 0.89f - 0.01f, 0.89f + 0.01f);
        }

        [Fact]
        public void StorageRequirements()
        {
            var footprint = Footprint.Get10x8();
            Assert.Equal(10, footprint.Width());
            Assert.Equal(8, footprint.Height());

            Assert.Equal(1024, footprint.StorageRequirements(80, 64));
            Assert.Equal(1024, footprint.StorageRequirements(79, 63));
        }
    }
}
