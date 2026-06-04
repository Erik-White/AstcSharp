namespace AstcSharp.Encoding;

/// <summary>
/// Bit positions of the fields in a 128-bit ASTC block (spec §C.2.7–§C.2.12), shared by the encoder
/// search (which computes the colour bit budget from them) and <see cref="BlockAssembler"/> (which
/// writes the fields at them). Block mode occupies bits [0:10], then the 2-bit partition-count
/// field. Single-partition blocks store the colour endpoint mode at bit 13 and colour data at bit
/// 17. Multi-partition blocks store a 10-bit partition seed at bit 13, a 2-bit shared-CEM marker
/// (0) at bit 23, the shared CEM at bit 25, and colour data at bit 29.
/// </summary>
internal static class BlockLayout
{
    // Total bits in an ASTC block (spec §C.2.7).
    public const int BlockBits = 128;

    public const int BlockModeStartBit = 0;
    public const int BlockModeBits = 11;
    public const int PartitionCountStartBit = 11;
    public const int PartitionCountBits = 2;
    public const int CemStartBit = 13;
    public const int CemBits = 4;
    public const int ColorStartBit = 17;
    public const int SinglePartitionField = 0; // partition-count field value for 1 partition (count - 1)
    public const int PartitionSeedStartBit = 13;
    public const int PartitionSeedBits = 10;
    public const int SharedCemMarkerStartBit = 23;
    public const int SharedCemMarkerBits = 2;
    public const int SharedCemStartBit = 25;
    public const int MultiColorStartBit = 29;

    // Dual-plane blocks (spec §C.2.20) carry a 2-bit colour-component selector in the high bits just
    // below the weight data.
    public const int DualPlaneSelectorBits = 2;
}
