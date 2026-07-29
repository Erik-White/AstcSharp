using AstcSharp.Core;

namespace AstcSharp.Tests.HDR;

/// <summary>
/// Tests for <see cref="Fp16.ToLns"/>, the right inverse of <see cref="Fp16.FromLns"/> the HDR
/// encoder relies on to fit endpoints in the LNS domain. The forward transform is many-to-one, so
/// the property under test is <c>FromLns(ToLns(y)) == y</c> for every representable non-negative
/// finite FP16 pattern — not a two-sided round-trip.
/// </summary>
public class Fp16Tests
{
    // The largest finite FP16 bit pattern (sign 0, exponent 30, mantissa all ones); patterns above
    // this are +Inf/NaN, which the HDR endpoint path never produces.
    private const ushort MaxFinite = 0x7BFF;

    [Fact]
    public void ToLns_IsRightInverseOfFromLns_AcrossAllFiniteNonNegativePatterns()
    {
        for (ushort fp16 = 0; fp16 <= MaxFinite; fp16++)
        {
            int lns = Fp16.ToLns(fp16);
            ushort roundTripped = Fp16.FromLns(lns);

            Assert.Equal(fp16, roundTripped);
        }
    }

    [Fact]
    public void ToLns_ProducesValidLnsField()
    {
        // The LNS value must fit the 16-bit field the encoder stores it in: a 5-bit exponent and an
        // 11-bit mantissa component.
        for (ushort fp16 = 0; fp16 <= MaxFinite; fp16++)
        {
            int lns = Fp16.ToLns(fp16);

            Assert.InRange(lns, 0, 0xFFFF);
        }
    }

    [Theory]
    [InlineData((ushort)0x0000)] // zero
    [InlineData((ushort)0x3C00)] // 1.0
    [InlineData((ushort)0x7800)] // Fp16.One (the endpoint alpha sentinel)
    [InlineData((ushort)0x0001)] // smallest subnormal
    [InlineData(MaxFinite)]      // largest finite
    public void ToLns_KnownPatterns_RoundTripThroughFromLns(ushort fp16)
    {
        Assert.Equal(fp16, Fp16.FromLns(Fp16.ToLns(fp16)));
    }
}
