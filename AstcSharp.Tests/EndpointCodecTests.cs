using System;
using System.Collections.Generic;
using Xunit;
using AstcSharp;

namespace AstcSharp.Tests
{
    public class EndpointCodecTests
    {
        private static (RgbaColor low, RgbaColor high) TestColors(RgbaColor low, RgbaColor high, int quant, EndpointEncodingMode mode)
        {
            var vals = new List<int>();
            var needsSwap = EndpointCodec.EncodeColorsForMode(low, high, quant, mode, out var astcMode, vals);
                var (decLow, decHigh) = EndpointCodec.DecodeColorsForMode(vals, quant, astcMode);
                if (needsSwap)
                {
                    return (decHigh, decLow);
                }
                return (decLow, decHigh);
        }

        private static bool AreEqual(RgbaColor a, RgbaColor b)
        {
            return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3];
        }

        private static bool AreClose(RgbaColor a, RgbaColor b, int tol)
        {
            return Math.Abs(a[0]-b[0]) <= tol && Math.Abs(a[1]-b[1]) <= tol && Math.Abs(a[2]-b[2]) <= tol && Math.Abs(a[3]-b[3]) <= tol;
        }

        [Fact]
        public void QuantRanges()
        {
            var modes = new[] {
                EndpointEncodingMode.kDirectLuma,
                EndpointEncodingMode.kDirectLumaAlpha,
                EndpointEncodingMode.kBaseScaleRGB,
                EndpointEncodingMode.kBaseScaleRGBA,
                EndpointEncodingMode.kDirectRGB,
                EndpointEncodingMode.kDirectRGBA
            };

            var low = new RgbaColor(0,0,0,0);
            var high = new RgbaColor(255,255,255,255);

            foreach (var mode in modes)
            {
                for (int i = 5; i < 256; ++i)
                {
                    var vals = new List<int>();
                    var needsSwap = EndpointCodec.EncodeColorsForMode(low, high, i, mode, out var astcMode, vals);
                    // The resulting vals length should match the encoding hint's value count
                    Assert.Equal(EndpointCodec.EncodingModeValuesCount(mode), vals.Count);

                    foreach (var v in vals)
                    {
                        Assert.InRange(v, 0, i);
                    }

                    // just ensure the return is a bool (we don't assert a specific value)
                    Assert.IsType<bool>(needsSwap);
                }
            }
        }

        [Fact]
        public void ExtremeDirectEncodings()
        {
            var modes = new[] {
                EndpointEncodingMode.kDirectLuma,
                EndpointEncodingMode.kDirectLumaAlpha,
                EndpointEncodingMode.kBaseScaleRGB,
                EndpointEncodingMode.kBaseScaleRGBA,
                EndpointEncodingMode.kDirectRGB,
                EndpointEncodingMode.kDirectRGBA
            };

            var white = new RgbaColor(255,255,255,255);
            var black = new RgbaColor(0,0,0,255);

            foreach (var mode in modes)
            {
                for (int i = 5; i < 256; ++i)
                {
                    var res = TestColors(white, black, i, mode);
                    Assert.True(AreEqual(res.low, white));
                    Assert.True(AreEqual(res.high, black));
                }
            }
        }

        [Fact]
        public void UsesBlueContract_SimpleCases()
        {
            var vals = new List<int>{ 132, 127, 116, 112, 183, 180, 31, 22 };
            Assert.True(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbDirect, vals));
            Assert.True(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbaDirect, vals));

            // For offset modes, flip certain bits to test negative cases as in reference
            var vals2 = new List<int>(vals);
            vals2[1] &= 0xBF;
            vals2[3] &= 0xBF;
            vals2[5] &= 0xBF;
            vals2[7] &= 0xBF;
            Assert.False(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbBaseOffset, vals2));
            Assert.False(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbaBaseOffset, vals2));

            vals2 = new List<int>(vals);
            vals2[1] |= 0x40;
            vals2[3] |= 0x40;
            vals2[5] |= 0x40;
            vals2[7] |= 0x40;
            Assert.True(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbBaseOffset, vals2));
            Assert.True(EndpointCodec.UsesBlueContract(255, ColorEndpointMode.kLdrRgbaBaseOffset, vals2));
        }

        [Fact]
        public void LumaDirect_SpecificChecks()
        {
            var mode = EndpointEncodingMode.kDirectLuma;

            // Specific cases from reference tests
            var res1 = TestColors(new RgbaColor(247,248,246,255), new RgbaColor(2,3,1,255), 255, mode);
            var expected1 = (new RgbaColor(247,247,247,255), new RgbaColor(2,2,2,255));
            Assert.True(AreEqual(res1.low, expected1.Item1));
            Assert.True(AreEqual(res1.high, expected1.Item2));

            var res2 = TestColors(new RgbaColor(80,80,50,255), new RgbaColor(99,255,6,255), 255, mode);
            var expected2 = (new RgbaColor(70,70,70,255), new RgbaColor(120,120,120,255));
            Assert.True(AreEqual(res2.low, expected2.Item1));
            Assert.True(AreEqual(res2.high, expected2.Item2));

            var res3 = TestColors(new RgbaColor(247,248,246,255), new RgbaColor(2,3,1,255), 15, mode);
            var expected3 = (new RgbaColor(255,255,255,255), new RgbaColor(0,0,0,255));
            Assert.True(AreEqual(res3.low, expected3.Item1));
            Assert.True(AreEqual(res3.high, expected3.Item2));

            var res4 = TestColors(new RgbaColor(64,127,192,255), new RgbaColor(0,0,0,255), 63, mode);
            var expected4 = (new RgbaColor(130,130,130,255), new RgbaColor(0,0,0,255));
            Assert.True(AreEqual(res4.low, expected4.Item1));
            Assert.True(AreEqual(res4.high, expected4.Item2));
        }

        [Fact]
        public void LumaAlphaDirect_SpecificChecks()
        {
            var mode = EndpointEncodingMode.kDirectLumaAlpha;

            // grey with varying alpha should round luma correctly and preserve alpha
            var res = TestColors(new RgbaColor(64,127,192,127), new RgbaColor(0,0,0,20), 63, mode);
            Assert.True(AreEqual(res.low, new RgbaColor(130,130,130,125)) || AreClose(res.low, new RgbaColor(130,130,130,125), 1));
            Assert.True(AreEqual(res.high, new RgbaColor(0,0,0,20)) || AreClose(res.high, new RgbaColor(0,0,0,20), 1));

            // alpha independent: using different alpha values
            var res2 = TestColors(new RgbaColor(247,248,246,250), new RgbaColor(2,3,1,172), 255, mode);
            Assert.True(AreEqual(res2.low, new RgbaColor(247,247,247,250)));
            Assert.True(AreEqual(res2.high, new RgbaColor(2,2,2,172)));
        }

        [Fact]
        public void RGBDirect_RandomAndSpecific()
        {
            var mode = EndpointEncodingMode.kDirectRGB;
            var rand = new Random(unchecked((int)0xdeadbeef));
            for (int i = 0; i < 100; ++i)
            {
                var low = new RgbaColor(rand.Next(0,256), rand.Next(0,256), rand.Next(0,256), 255);
                var high = new RgbaColor(rand.Next(0,256), rand.Next(0,256), rand.Next(0,256), 255);
                var res = TestColors(low, high, 255, mode);
                Assert.True(AreEqual(res.low, low));
                Assert.True(AreEqual(res.high, high));
            }

            // Specific reference cases
            var r1 = TestColors(new RgbaColor(64,127,192,255), new RgbaColor(0,0,0,255), 63, mode);
            Assert.True(AreEqual(r1.low, new RgbaColor(65,125,190,255)));
            Assert.True(AreEqual(r1.high, new RgbaColor(0,0,0,255)));

            var r2 = TestColors(new RgbaColor(0,0,0,255), new RgbaColor(64,127,192,255), 63, mode);
            Assert.True(AreEqual(r2.low, new RgbaColor(0,0,0,255)));
            Assert.True(AreEqual(r2.high, new RgbaColor(65,125,190,255)));
        }

        [Fact]
        public void RGBDirectMakesBlueContract()
        {
            var pairs = new (RgbaColor, RgbaColor)[] {
                (new RgbaColor(22,18,30,59), new RgbaColor(162,148,155,59)),
                (new RgbaColor(22,30,27,36), new RgbaColor(228,221,207,36)),
                (new RgbaColor(54,60,55,255), new RgbaColor(23,30,27,255))
            };

            const int kEndpointRange = 31;
            foreach (var p in pairs)
            {
                var vals = new List<int>();
                var needsSwap = EndpointCodec.EncodeColorsForMode(p.Item1, p.Item2, kEndpointRange, EndpointEncodingMode.kDirectRGB, out var astcMode, vals);
                // ensure blue contract used
                Assert.True(EndpointCodec.UsesBlueContract(kEndpointRange, astcMode, vals));
            }
        }

        [Fact]
        public void RGBBaseScale_Tests()
        {
            var mode = EndpointEncodingMode.kBaseScaleRGB;
            var rand = new Random(unchecked((int)0xdeadbeef));

            // identical colors should be encoded with approx scale 255 -> within 1
            for (int i = 0; i < 100; ++i)
            {
                var c = new RgbaColor(rand.Next(0,256), rand.Next(0,256), rand.Next(0,256), 255);
                var res = TestColors(c, c, 255, mode);
                Assert.True(AreClose(res.low, c, 1));
                Assert.True(AreClose(res.high, c, 1));
            }

            // explicit scale case
            var low = new RgbaColor(20,4,40,255);
            var high = new RgbaColor(80,16,160,255);
            var r = TestColors(low, high, 255, mode);
            Assert.True(AreClose(r.low, low, 0));
            Assert.True(AreClose(r.high, high, 0));

            // lower quantization produces small deviations
            var r2 = TestColors(low, high, 127, mode);
            Assert.True(AreClose(r2.low, low, 1));
            Assert.True(AreClose(r2.high, high, 1));
        }

        [Fact]
        public void RGBBaseOffset_DecodeChecks()
        {
            // Helper to construct vals as in reference test
            void TestColorsDecode(RgbaColor low, RgbaColor high)
            {
                var vals = new List<int>();
                for (int i = 0; i < 3; ++i)
                {
                    bool is_large = low[i] >= 128;
                    vals.Add((low[i] * 2) & 0xFF);
                    int diff = (high[i] - low[i]) * 2;
                    if (is_large) diff |= 0x80;
                    vals.Add(diff);
                }

                var (decLow, decHigh) = EndpointCodec.DecodeColorsForMode(vals, 255, ColorEndpointMode.kLdrRgbBaseOffset);
                Assert.True(AreEqual(decLow, low));
                Assert.True(AreEqual(decHigh, high));
            }

            TestColorsDecode(new RgbaColor(80,16,112,255), new RgbaColor(87,18,132,255));
            TestColorsDecode(new RgbaColor(80,74,82,255), new RgbaColor(90,92,110,255));
            TestColorsDecode(new RgbaColor(0,0,0,255), new RgbaColor(2,2,2,255));

            // random identical endpoints (even channels) should decode faithfully
            var rand = new Random(unchecked((int)0xdeadbeef));
            for (int i = 0; i < 100; ++i)
            {
                int r = rand.Next(0,256);
                int g = rand.Next(0,256);
                int b = rand.Next(0,256);
                // ensure even channels as reference skips odd
                if (((r|g|b) & 1) != 0) continue;
                var c = new RgbaColor(r,g,b,255);
                TestColorsDecode(c, c);
            }
        }
    }
}
