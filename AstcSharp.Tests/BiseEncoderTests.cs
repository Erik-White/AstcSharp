using AstcSharp.BiseEncoding;

namespace AstcSharp.Tests;

/// <summary>
/// Round-trip tests for <see cref="BoundedIntegerSequenceEncoder"/> against
/// <see cref="BoundedIntegerSequenceDecoder"/>: every spec-valid range, encoding then decoding a
/// sequence must reproduce it exactly (ASTC spec §C.2.12). Sequence lengths are capped to the
/// 128-bit block budget the <see cref="BitStream"/> holds.
/// </summary>
public class BiseEncoderTests
{
    public static TheoryData<int> ValidRanges
    {
        get
        {
            var data = new TheoryData<int>();
            foreach (int range in BoundedIntegerSequenceCodec.MaxRanges)
            {
                data.Add(range);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidRanges))]
    public void EncodeThenDecode_VaryingLengths_RoundTrips(int range)
    {
        int maxCount = MaxValuesIn128Bits(range);

        // Exercise every length from 1 up to the budget so full and partial trailing
        // trit/quint blocks (counts not a multiple of 5 or 3) are all covered.
        for (int count = 1; count <= maxCount; count++)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = i % (range + 1);
            }

            AssertRoundTrips(range, values);
        }
    }

    [Theory]
    [MemberData(nameof(ValidRanges))]
    public void EncodeThenDecode_Extremes_RoundTrip(int range)
    {
        int count = MaxValuesIn128Bits(range);
        int[] allZero = new int[count];
        int[] allMax = new int[count];
        Array.Fill(allMax, range);

        AssertRoundTrips(range, allZero);
        AssertRoundTrips(range, allMax);
    }

    private static int MaxValuesIn128Bits(int range)
    {
        int count = 0;
        while (BoundedIntegerSequenceCodec.GetBitCountForRange(count + 1, range) <= 128)
        {
            count++;
        }

        return count;
    }

    private static void AssertRoundTrips(int range, int[] values)
    {
        (BiseEncodingMode mode, int bitCount) = BoundedIntegerSequenceCodec.GetPackingModeBitCount(range);

        var stream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(range, values, ref stream);

        int totalBits = BoundedIntegerSequenceCodec.GetBitCount(mode, values.Length, bitCount);
        Assert.Equal((uint)totalBits, stream.Bits);

        int[] decoded = new int[values.Length];
        BoundedIntegerSequenceDecoder.Decode(mode, bitCount, values.Length, ref stream, decoded);

        Assert.Equal(values, decoded);
    }
}
