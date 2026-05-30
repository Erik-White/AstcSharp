namespace AstcSharp.Encoding;

/// <summary>
/// Encodes constant-colour "void-extent" ASTC blocks (spec §C.2.23) — the simplest legal block,
/// carrying a single RGBA colour and no weight grid. Inverts <c>LogicalBlock.DecodeVoidExtentEndpoint</c>,
/// LDR channels are stored as UNORM16 (the decoder takes the high byte), HDR channels as raw FP16 bit patterns.
/// </summary>
internal static class VoidExtentEncoder
{
    // Void-extent marker: bits[0:8] == 0x1FC (spec §C.2.23).
    private const int Marker = 0x1FC;
    private const int MarkerBits = 9;

    // Bit 9 selects the dynamic range: 0 = LDR (UNORM16), 1 = HDR (FP16).
    private const int HdrFlagBit = 9;

    // Bits[10:11] are reserved and must be 0x3 for a well-formed void-extent block.
    private const int ReservedValue = 0x3;
    private const int ReservedStartBit = 10;
    private const int ReservedBits = 2;

    // Four 13-bit texel-coordinate fields (bits 12..63). All-ones is the "no constraint" sentinel.
    private const int CoordsStartBit = 12;
    private const int CoordsBits = 52;
    private const ulong CoordsAllOnes = (1UL << CoordsBits) - 1UL;

    private const int ChannelBits = 16;
    private const int RedStartBit = 64;
    private const int GreenStartBit = 80;
    private const int BlueStartBit = 96;
    private const int AlphaStartBit = 112;

    /// <summary>
    /// Builds an LDR void-extent block for the constant colour (<paramref name="r"/>,
    /// <paramref name="g"/>, <paramref name="b"/>, <paramref name="a"/>). Each 8-bit channel is
    /// bit-replicated to UNORM16 (<c>(c &lt;&lt; 8) | c</c>) so the decoder's high-byte read
    /// recovers the original byte exactly.
    /// </summary>
    public static UInt128 EncodeLdr(byte r, byte g, byte b, byte a)
    {
        var builder = new AstcBlockBuilder();
        WriteHeader(ref builder, isHdr: false);
        builder.PlaceLowField(Replicate(r), RedStartBit, ChannelBits);
        builder.PlaceLowField(Replicate(g), GreenStartBit, ChannelBits);
        builder.PlaceLowField(Replicate(b), BlueStartBit, ChannelBits);
        builder.PlaceLowField(Replicate(a), AlphaStartBit, ChannelBits);
        return builder.Build();
    }

    /// <summary>
    /// Builds an HDR void-extent block from raw FP16 channel bit patterns. The decoder reads these
    /// directly as FP16 (no LNS conversion — spec §C.2.23).
    /// </summary>
    public static UInt128 EncodeHdr(ushort r, ushort g, ushort b, ushort a)
    {
        var builder = new AstcBlockBuilder();
        WriteHeader(ref builder, isHdr: true);
        builder.PlaceLowField(r, RedStartBit, ChannelBits);
        builder.PlaceLowField(g, GreenStartBit, ChannelBits);
        builder.PlaceLowField(b, BlueStartBit, ChannelBits);
        builder.PlaceLowField(a, AlphaStartBit, ChannelBits);
        return builder.Build();
    }

    private static void WriteHeader(ref AstcBlockBuilder builder, bool isHdr)
    {
        builder.PlaceLowField(Marker, startBit: 0, MarkerBits);
        if (isHdr)
        {
            builder.PlaceLowField(1, HdrFlagBit, count: 1);
        }

        builder.PlaceLowField(ReservedValue, ReservedStartBit, ReservedBits);
        builder.PlaceLowField(CoordsAllOnes, CoordsStartBit, CoordsBits);
    }

    private static ulong Replicate(byte channel) => (ulong)(((uint)channel << 8) | channel);
}
