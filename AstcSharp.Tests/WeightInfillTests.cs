using Xunit;
using AstcSharp.Reference;
using System.Collections.Generic;

namespace AstcSharp.Tests
{
    public class WeightInfillTests
    {
        [Fact]
        public void TestGetBitCount()
        {
            Assert.Equal(32, WeightInfill.CountBitsForWeights(4, 4, 3));
            Assert.Equal(48, WeightInfill.CountBitsForWeights(4, 4, 7));
            Assert.Equal(24, WeightInfill.CountBitsForWeights(2, 4, 7));
            Assert.Equal(8, WeightInfill.CountBitsForWeights(2, 4, 1));

            Assert.Equal(32, WeightInfill.CountBitsForWeights(4, 5, 2));
            Assert.Equal(26, WeightInfill.CountBitsForWeights(4, 4, 2));
            Assert.Equal(52, WeightInfill.CountBitsForWeights(4, 5, 5));
            Assert.Equal(42, WeightInfill.CountBitsForWeights(4, 4, 5));

            Assert.Equal(21, WeightInfill.CountBitsForWeights(3, 3, 4));
            Assert.Equal(38, WeightInfill.CountBitsForWeights(4, 4, 4));
            Assert.Equal(49, WeightInfill.CountBitsForWeights(3, 7, 4));
            Assert.Equal(52, WeightInfill.CountBitsForWeights(4, 3, 19));
            Assert.Equal(70, WeightInfill.CountBitsForWeights(4, 4, 19));
        }

        [Fact]
        public void TestInfillBilerp()
        {
            var weights = new List<int> { 1,3,5,3,5,7,5,7,9 };
            var result = WeightInfill.InfillWeights(weights, Footprint.Get5x5(), 3, 3);

            var expected = new List<int> { 1,2,3,4,5, 2,3,4,5,6, 3,4,5,6,7, 4,5,6,7,8, 5,6,7,8,9 };
            Assert.Equal(expected.Count, result.Count);
            for (int i=0;i<expected.Count;++i) Assert.Equal(expected[i], result[i]);
        }
    }
}
