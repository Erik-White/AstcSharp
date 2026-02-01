using AstcSharp.Core;

namespace AstcSharp.Tests;

public class BitOperationsTests
{
    [Fact]
    public void GetBits_UInt128_ExtractsLowBits()
    {
        UInt128 value = new UInt128(0x1234567890ABCDEF, 0xFEDCBA0987654321);

        // Test extracting lowest 8 bits
        var result = BitOperations.GetBits(value, 0, 8);
        Assert.Equal(0x21UL, result.Low());
    }

    [Fact]
    public void GetBits_ULong_ExtractsLowBits()
    {
        ulong value = 0xFEDCBA0987654321;

        // Test extracting lowest 8 bits
        var result = BitOperations.GetBits(value, 0, 8);
        Assert.Equal(0x21UL, result);
    }

    [Fact]
    public void GetBits_UInt128_ExtractsZeroLengthReturnsZero()
    {
        UInt128 value = new UInt128(0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF);
        var result = BitOperations.GetBits(value, 0, 0);
        Assert.Equal(UInt128.Zero, result);
    }

    [Fact]
    public void GetBits_ULong_ExtractsZeroLengthReturnsZero()
    {
        ulong value = 0xFFFFFFFFFFFFFFFF;
        var result = BitOperations.GetBits(value, 0, 0);
        Assert.Equal(0UL, result);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 20)]
    [InlineData(128, 255)]
    [InlineData(255, 128)]
    [InlineData(64, 64)]
    public void TransferPrecision_ProducesConsistentResults(int inputA, int inputB)
    {
        // Just verify it completes without exception and produces deterministic results
        var (a, b) = BitOperations.TransferPrecision(inputA, inputB);

        // Run again with same inputs to verify determinism
        var (a2, b2) = BitOperations.TransferPrecision(inputA, inputB);

        Assert.Equal(a, a2);
        Assert.Equal(b, b2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 10)]
    [InlineData(10, 255)]
    [InlineData(31, 128)]
    [InlineData(-32, 200)]
    [InlineData(-1, 100)]
    public void TransferPrecisionInverse_ProducesConsistentResults(int inputA, int inputB)
    {
        // Just verify it completes without exception and produces deterministic results
        var (a, b) = BitOperations.TransferPrecisionInverse(inputA, inputB);

        // Run again with same inputs to verify determinism
        var (a2, b2) = BitOperations.TransferPrecisionInverse(inputA, inputB);

        Assert.Equal(a, a2);
        Assert.Equal(b, b2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 20)]
    [InlineData(31, 255)]
    [InlineData(-32, 128)]
    [InlineData(-1, 200)]
    public void TransferPrecision_RoundTrip_ReturnsOriginalValues(int originalA, int originalB)
    {
        // Apply inverse first to get encoded values
        var (encodedA, encodedB) = BitOperations.TransferPrecisionInverse(originalA, originalB);

        // Then apply regular to decode
        var (a, b) = BitOperations.TransferPrecision(encodedA, encodedB);

        Assert.Equal(originalA, a);
        Assert.Equal(originalB, b);
    }

    [Theory]
    [InlineData(-33, 128)] // a too small
    [InlineData(32, 128)]  // a too large
    [InlineData(0, -1)]    // b too small
    [InlineData(0, 256)]   // b too large
    public void TransferPrecisionInverse_ThrowsOnInvalidInput(int a, int b)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            BitOperations.TransferPrecisionInverse(a, b);
        });
    }

    [Fact]
    public void TransferPrecision_DoesNotThrowOnAnyValidByteInput()
    {
        // TransferPrecision should work with any int values
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                var exception = Record.Exception(() => BitOperations.TransferPrecision(a, b));
                Assert.Null(exception);
            }
        }
    }
}
