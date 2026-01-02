using Xunit;
using System.Linq;
using System;
using System.Collections.Generic;
using AstcSharp.BiseEncoding;

namespace AstcSharp.Tests
{
    public class QuantizationTests
    {
        [Fact]
        public void QuantizeMaxRange()
        {
            for (int i = Quantization.kEndpointRangeMinValue; i < 256; ++i)
            {
                Assert.True(Quantization.QuantizeCEValueToRange(255, i) <= i);
            }

            for (int i = 1; i < Quantization.kWeightRangeMaxValue; ++i)
            {
                Assert.True(Quantization.QuantizeWeightToRange(64, i) <= i);
            }
        }

        [Fact]
        public void ReversibilityBasic()
        {
            var ranges = IntegerSequenceCodec.ISERange().ToArray();
            foreach (var range in ranges)
            {
                if (range <= Quantization.kWeightRangeMaxValue)
                {
                    for (int j = 0; j <= range; ++j)
                    {
                        var q = Quantization.UnquantizeWeightFromRange(j, range);
                        Assert.Equal(j, Quantization.QuantizeWeightToRange(q, range));
                    }
                }

                if (range >= Quantization.kEndpointRangeMinValue)
                {
                    for (int j = 0; j <= range; ++j)
                    {
                        var q = Quantization.UnquantizeCEValueFromRange(j, range);
                        Assert.Equal(j, Quantization.QuantizeCEValueToRange(q, range));
                    }
                }
            }
        }

        [Fact]
        public void QuantizationRange()
        {
            var ranges = IntegerSequenceCodec.ISERange().ToArray();
            foreach (var range in ranges)
            {
                if (range >= Quantization.kEndpointRangeMinValue)
                {
                    Assert.True(Quantization.QuantizeCEValueToRange(0, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(4, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(15, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(22, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(66, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(91, range) <= range);
                    Assert.True(Quantization.QuantizeCEValueToRange(126, range) <= range);
                }

                if (range <= Quantization.kWeightRangeMaxValue)
                {
                    Assert.True(Quantization.QuantizeWeightToRange(0, range) <= range);
                    Assert.True(Quantization.QuantizeWeightToRange(4, range) <= range);
                    Assert.True(Quantization.QuantizeWeightToRange(15, range) <= range);
                    Assert.True(Quantization.QuantizeWeightToRange(22, range) <= range);
                }
            }
        }

        [Fact]
        public void UnquantizationRange()
        {
            Assert.True(Quantization.UnquantizeCEValueFromRange(2, 7) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(7, 7) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(39, 63) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(66, 79) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(91, 191) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(126, 255) < 256);
            Assert.True(Quantization.UnquantizeCEValueFromRange(255, 255) < 256);

            Assert.True(Quantization.UnquantizeWeightFromRange(0, 1) <= 64);
            Assert.True(Quantization.UnquantizeWeightFromRange(2, 7) <= 64);
            Assert.True(Quantization.UnquantizeWeightFromRange(7, 7) <= 64);
            Assert.True(Quantization.UnquantizeWeightFromRange(29, 31) <= 64);
        }

        [Fact]
        public void UpperBoundRanges()
        {
            var ranges = IntegerSequenceCodec.ISERange().ToArray();
            int idx = 0;
            for (int desired_range = 1; desired_range < 256; ++desired_range)
            {
                while (idx + 1 < ranges.Length && ranges[idx + 1] <= desired_range) ++idx;
                int expected_range = ranges[idx];
                if (desired_range >= Quantization.kEndpointRangeMinValue)
                {
                    Assert.Equal(Quantization.QuantizeCEValueToRange(0, desired_range), Quantization.QuantizeCEValueToRange(0, expected_range));
                    Assert.Equal(Quantization.QuantizeCEValueToRange(208, desired_range), Quantization.QuantizeCEValueToRange(208, expected_range));
                    Assert.Equal(Quantization.QuantizeCEValueToRange(173, desired_range), Quantization.QuantizeCEValueToRange(173, expected_range));
                    Assert.Equal(Quantization.QuantizeCEValueToRange(13, desired_range), Quantization.QuantizeCEValueToRange(13, expected_range));
                    Assert.Equal(Quantization.QuantizeCEValueToRange(255, desired_range), Quantization.QuantizeCEValueToRange(255, expected_range));
                }

                if (desired_range <= Quantization.kWeightRangeMaxValue)
                {
                    Assert.Equal(Quantization.QuantizeWeightToRange(0, desired_range), Quantization.QuantizeWeightToRange(0, expected_range));
                    Assert.Equal(Quantization.QuantizeWeightToRange(63, desired_range), Quantization.QuantizeWeightToRange(63, expected_range));
                    Assert.Equal(Quantization.QuantizeWeightToRange(12, desired_range), Quantization.QuantizeWeightToRange(12, expected_range));
                    Assert.Equal(Quantization.QuantizeWeightToRange(23, desired_range), Quantization.QuantizeWeightToRange(23, expected_range));
                }
            }

            Assert.Equal(ranges.Length - 1, idx);
        }

        [Fact]
        public void Identity()
        {
            for (int i = 0; i < 256; ++i) Assert.Equal(i, Quantization.QuantizeCEValueToRange(i, 255));
        }

        [Fact]
        public void MonotonicBitPacking()
        {
            for (int num_bits = 3; num_bits < 8; ++num_bits)
            {
                int range = (1 << num_bits) - 1;
                int last_quant_val = -1;
                for (int i = 0; i < 256; ++i)
                {
                    int quant_val = Quantization.QuantizeCEValueToRange(i, range);
                    Assert.True(last_quant_val <= quant_val);
                    last_quant_val = quant_val;
                }
                Assert.Equal(range, last_quant_val);

                if (range <= Quantization.kWeightRangeMaxValue)
                {
                    last_quant_val = -1;
                    for (int i = 0; i <= 64; ++i)
                    {
                        int quant_val = Quantization.QuantizeWeightToRange(i, range);
                        Assert.True(last_quant_val <= quant_val);
                        last_quant_val = quant_val;
                    }
                    Assert.Equal(range, last_quant_val);
                }
            }
        }

        [Fact]
        public void SmallBitPacking()
        {
            for (int num_bits = 1; num_bits <= 8; ++num_bits)
            {
                int range = (1 << num_bits) - 1;

                if (range >= Quantization.kEndpointRangeMinValue)
                {
                    const int cev_bits = 8;
                    int half_max_quant_bits = Math.Max(0, cev_bits - num_bits - 1);
                    int largest_cev_to_zero = (1 << half_max_quant_bits) - 1;
                    Assert.Equal(0, Quantization.QuantizeCEValueToRange(largest_cev_to_zero, range));
                }

                if (range <= Quantization.kWeightRangeMaxValue)
                {
                    const int weight_bits = 6;
                    int half_max_quant_bits = Math.Max(0, weight_bits - num_bits - 1);
                    int largest_weight_to_zero = (1 << half_max_quant_bits) - 1;
                    Assert.Equal(0, Quantization.QuantizeWeightToRange(largest_weight_to_zero, range));
                }
            }
        }

        [Fact]
        public void SpecificQuintTritPackings()
        {
            var vals = new List<int> { 4, 6, 4, 6, 7, 5, 7, 5 };
            var quantized = vals.Select(v => Quantization.UnquantizeWeightFromRange(v, 9)).ToList();
            var quintExpected = new List<int> { 14, 21, 14, 21, 43, 50, 43, 50 };
            Assert.Equal(quintExpected, quantized);

            quantized = vals.Select(v => Quantization.UnquantizeWeightFromRange(v, 11)).ToList();
            var tritExpected = new List<int> { 5, 23, 5, 23, 41, 59, 41, 59 };
            Assert.Equal(tritExpected, quantized);
        }

        [Fact]
        public void InvalidMinRangeThrows()
        {
            for (int i = 0; i < Quantization.kEndpointRangeMinValue; ++i)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(0, i));
                Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(0, i));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(0, 0));
        }

        [Fact]
        public void OutOfRangeInputsThrow()
        {
            // CE value checks
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(-1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(256, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(10000, 17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(-1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(8, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(-1000, 17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(0, -7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(0, -17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeCEValueToRange(0, 257));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeCEValueFromRange(0, 256));

            // Weight checks
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(-1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(256, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(10000, 17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(-1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(8, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(-1000, 17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(0, -7));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(0, -17));

            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.QuantizeWeightToRange(0, 32));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.UnquantizeWeightFromRange(0, 64));
        }
    }
}
