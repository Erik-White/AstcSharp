using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using static AstcSharp.Encoding.BlockLayout;

namespace AstcSharp.Encoding;

/// <summary>
/// Assembles the three LDR block layouts the encoder produces — single-partition, multi-partition
/// (shared CEM), and single-partition dual-plane — from a resolved configuration (block mode, colour
/// values, and quantised weights). Each writes the low-bit header fields, the colour BISE stream,
/// and the weight BISE stream into an <see cref="AstcBlockBuilder"/>; the field positions come from
/// <see cref="BlockLayout"/>.
/// </summary>
internal static class BlockAssembler
{
    /// <summary>
    /// Assembles a single-partition block: block mode, the colour endpoint mode, the colour values
    /// from bit 17, and the weight data at the top.
    /// </summary>
    public static UInt128 AssembleSinglePartition(
        ushort blockMode, ColorEndpointMode mode, int colorRange, ReadOnlySpan<int> colorValues, int weightRange, ReadOnlySpan<int> quantWeights)
    {
        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, BlockModeStartBit, BlockModeBits);
        builder.PlaceLowField(SinglePartitionField, PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)mode, CemStartBit, CemBits);

        PlaceColorData(ref builder, colorRange, colorValues, ColorStartBit);
        PlaceWeightData(ref builder, weightRange, quantWeights);
        return builder.Build();
    }

    /// <summary>
    /// Assembles a multi-partition block (spec §C.2.10): partition count, 10-bit seed, a shared-CEM
    /// marker of 0, the shared colour endpoint mode, the concatenated per-partition colour values
    /// from bit 29, and the weight data at the top.
    /// </summary>
    public static UInt128 AssembleMultiPartition(
        ushort blockMode,
        int partitionCount,
        int seed,
        ColorEndpointMode mode,
        int colorRange,
        ReadOnlySpan<int> colorValues,
        int weightRange,
        ReadOnlySpan<int> quantWeights)
    {
        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, BlockModeStartBit, BlockModeBits);
        builder.PlaceLowField((ulong)(partitionCount - 1), PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)seed, PartitionSeedStartBit, PartitionSeedBits);
        builder.PlaceLowField(0, SharedCemMarkerStartBit, SharedCemMarkerBits);
        builder.PlaceLowField((ulong)mode, SharedCemStartBit, CemBits);

        PlaceColorData(ref builder, colorRange, colorValues, MultiColorStartBit);
        PlaceWeightData(ref builder, weightRange, quantWeights);
        return builder.Build();
    }

    /// <summary>
    /// Assembles a single-partition dual-plane block (spec §C.2.20): block mode, the colour endpoint
    /// mode and values, the two weight planes interleaved (plane 0 at even grid indices, plane 1 at
    /// odd) into the reversed weight region, and the 2-bit colour-component selector in the high bits
    /// just below the weights.
    /// </summary>
    public static UInt128 AssembleDualPlane(
        ushort blockMode,
        ColorEndpointMode mode,
        int colorRange,
        ReadOnlySpan<int> colorValues,
        int weightRange,
        int dualPlaneChannel,
        ReadOnlySpan<int> quantWeights0,
        ReadOnlySpan<int> quantWeights1)
    {
        var builder = new AstcBlockBuilder();
        builder.PlaceLowField(blockMode, BlockModeStartBit, BlockModeBits);
        builder.PlaceLowField(SinglePartitionField, PartitionCountStartBit, PartitionCountBits);
        builder.PlaceLowField((ulong)mode, CemStartBit, CemBits);

        PlaceColorData(ref builder, colorRange, colorValues, ColorStartBit);

        // Interleave the two planes' grid weights (spec §C.2.20): even raw indices drive plane 0,
        // odd plane 1 — the order the decoder de-interleaves.
        int gridCount = quantWeights0.Length;
        Span<int> interleaved = stackalloc int[gridCount * 2];
        for (int i = 0; i < gridCount; i++)
        {
            interleaved[i * 2] = quantWeights0[i];
            interleaved[(i * 2) + 1] = quantWeights1[i];
        }

        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(weightRange, interleaved, ref weightStream);
        int weightBitCount = (int)weightStream.Bits;

        // The 2-bit colour-component selector sits just below the weight data (spec §C.2.20): at bit
        // 128 - weightBitCount - 2. Single-partition only — the decoder's position also subtracts the
        // per-partition extra-CEM bits, which are zero here; multi-partition dual-plane would need them.
        builder.PlaceLowField((ulong)dualPlaneChannel, BlockBits - weightBitCount - DualPlaneSelectorBits, DualPlaneSelectorBits);
        builder.PlaceWeightData(weightStream);
        return builder.Build();
    }

    private static void PlaceColorData(ref AstcBlockBuilder builder, int colorRange, ReadOnlySpan<int> colorValues, int startBit)
    {
        var colorStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(colorRange, colorValues, ref colorStream);
        builder.PlaceColorData(colorStream, startBit);
    }

    private static void PlaceWeightData(ref AstcBlockBuilder builder, int weightRange, ReadOnlySpan<int> quantWeights)
    {
        var weightStream = new BitStream();
        BoundedIntegerSequenceEncoder.Encode(weightRange, quantWeights, ref weightStream);
        builder.PlaceWeightData(weightStream);
    }
}
