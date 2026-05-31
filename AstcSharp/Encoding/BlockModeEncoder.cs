using AstcSharp.BlockDecoding;

namespace AstcSharp.Encoding;

/// <summary>
/// Encodes the 11-bit weight configuration of an ASTC block mode (grid dimensions, weight range,
/// dual-plane flag — spec §C.2.7–§C.2.10) into the low bits the decoder reads. This is the inverse
/// of <see cref="BlockModeDecoder.TryDecodeWeightConfig"/>.
/// </summary>
/// <remarks>
/// The forward block-mode layout (spec §C.2.8 Table 24) has several overlapping bit-field
/// arrangements that are awkward to invert by hand. Instead of re-deriving the packing, the
/// reverse table is built once by enumerating all 2048 possible 11-bit mode values, decoding each
/// through the decoder's own <see cref="BlockModeDecoder.TryDecodeWeightConfig"/>, and recording
/// the mode bits for every (gridWidth, gridHeight, weightRange, dualPlane) configuration it
/// produces. This guarantees the encoder and decoder agree by construction.
/// </remarks>
internal static class BlockModeEncoder
{
    private const int ModeBitCount = 11;
    private const int ModeValueCount = 1 << ModeBitCount;

    private static readonly Dictionary<WeightConfig, ushort> ConfigToModeBits = BuildReverseTable();

    /// <summary>
    /// Returns the 11-bit block mode encoding the given weight configuration, or throws if the
    /// configuration is not representable by any legal block mode.
    /// </summary>
    public static ushort Encode(int gridWidth, int gridHeight, int weightRange, bool isDualPlane)
        => TryEncode(gridWidth, gridHeight, weightRange, isDualPlane, out ushort modeBits)
            ? modeBits
            : throw new ArgumentException(
                $"No legal ASTC block mode for grid {gridWidth}x{gridHeight}, weight range {weightRange}, dual-plane {isDualPlane}.");

    /// <summary>
    /// Returns the 11-bit block mode for the configuration, or false if it is not representable.
    /// </summary>
    public static bool TryEncode(int gridWidth, int gridHeight, int weightRange, bool isDualPlane, out ushort modeBits)
        => ConfigToModeBits.TryGetValue(new WeightConfig(gridWidth, gridHeight, weightRange, isDualPlane), out modeBits);

    private static Dictionary<WeightConfig, ushort> BuildReverseTable()
    {
        var table = new Dictionary<WeightConfig, ushort>();
        for (ushort modeBits = 0; modeBits < ModeValueCount; modeBits++)
        {
            if (!BlockModeDecoder.TryDecodeWeightConfig(modeBits, out int gridWidth, out int gridHeight, out int weightRange, out bool isDualPlane))
            {
                continue;
            }

            // Multiple mode values can decode to the same configuration (unused high bits);
            // keep the lowest so encoding is deterministic.
            var key = new WeightConfig(gridWidth, gridHeight, weightRange, isDualPlane);
            table.TryAdd(key, modeBits);
        }

        return table;
    }

    private readonly record struct WeightConfig(int GridWidth, int GridHeight, int WeightRange, bool IsDualPlane);
}
