using AstcSharp.BlockDecoding;
using AstcSharp.Core;
using AstcSharp.Encoding;

namespace AstcSharp.Tests;

/// <summary>
/// Verifies <see cref="BlockModeEncoder"/> is the exact inverse of the block-mode weight-config
/// decode: every (grid, weightRange, dualPlane) the decoder can produce must encode back to mode
/// bits the decoder reads as the same configuration (ASTC spec §C.2.7–§C.2.10).
/// </summary>
public class BlockModeEncoderTests
{
    [Fact]
    public void Encode_EveryDecodableConfig_RoundTripsThroughDecoder()
    {
        int configsChecked = 0;

        // Enumerate all 11-bit modes, decode each to its weight config, then encode that config
        // and confirm it decodes back identically. Covers every legal weight configuration.
        for (ushort modeBits = 0; modeBits < (1 << 11); modeBits++)
        {
            if (!BlockModeDecoder.TryDecodeWeightConfig(modeBits, out int gridWidth, out int gridHeight, out int weightRange, out bool isDualPlane))
            {
                continue;
            }

            // A full block decode also rejects configs with more than 64 weights (spec §C.2.11),
            // the encoder mirrors that, so those configs are legitimately not representable.
            int weightCount = gridWidth * gridHeight * (isDualPlane ? 2 : 1);
            if (weightCount > 64)
            {
                continue;
            }

            configsChecked++;

            ushort encoded = BlockModeEncoder.Encode(gridWidth, gridHeight, weightRange, isDualPlane);

            bool ok = BlockModeDecoder.TryDecodeWeightConfig(encoded, out int gw2, out int gh2, out int wr2, out bool dp2);
            Assert.True(ok, $"Encoded mode 0x{encoded:X} did not decode for config {gridWidth}x{gridHeight} r{weightRange} dp{isDualPlane}");
            Assert.Equal((gridWidth, gridHeight, weightRange, isDualPlane), (gw2, gh2, wr2, dp2));
        }

        Assert.True(configsChecked > 0, "Expected at least one decodable block-mode configuration");
    }

    [Theory]
    [InlineData(4, 4, 5, false)]   // 16 weights
    [InlineData(8, 8, 7, false)]   // 64 weights (single-plane maximum)
    [InlineData(4, 4, 3, true)]    // 32 weights (dual-plane doubles the count)
    [InlineData(6, 6, 1, false)]   // 36 weights
    public void Encode_KnownConfig_DecodesBack(int gridWidth, int gridHeight, int weightRange, bool isDualPlane)
    {
        ushort modeBits = BlockModeEncoder.Encode(gridWidth, gridHeight, weightRange, isDualPlane);

        Assert.True(BlockModeDecoder.TryDecodeWeightConfig(modeBits, out int gw, out int gh, out int wr, out bool dp));
        Assert.Equal((gridWidth, gridHeight, weightRange, isDualPlane), (gw, gh, wr, dp));
    }

    [Fact]
    public void TryEncode_UnrepresentableConfig_ReturnsFalse()
    {
        // 13x13 exceeds the maximum 12x12 weight grid; no legal block mode encodes it.
        Assert.False(BlockModeEncoder.TryEncode(13, 13, 5, false, out _));
    }
}
