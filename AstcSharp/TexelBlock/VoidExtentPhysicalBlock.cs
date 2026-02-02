using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

/// <summary>
/// A physical ASTC void extent block representing a constant color region
/// </summary>
internal sealed class VoidExtentPhysicalBlock : PhysicalBlock
{
    public VoidExtentPhysicalBlock(UInt128 bits) : base(bits)
    {
    }

    public override bool IsVoidExtent => true;

    public override bool IsDualPlane => false;

    internal override (int Width, int Height)? GetWeightGridDimensions() => null;

    internal override int? GetWeightRange() => null;

    internal override int[]? GetVoidExtentCoordinates()
    {
        // If void extent coords are all 1's then these are not valid void extent coords
        ulong voidExtentMask = 0xFFFFFFFFFFFFFDFFUL;
        ulong constBlockMode = 0xFFFFFFFFFFFFFDFCUL;

        return !IsIllegalEncoding && (voidExtentMask & BlockBits.Low()) != constBlockMode
            ? DecodeVoidExtentCoordinates(BlockBits)
            : null;
    }

    internal override string? IdentifyInvalidEncodingIssues()
    {
        // Check reserved bits at the full 128-bit level like the C++ reference.
        if (BitOperations.GetBits(BlockBits, 10, 2).Low() != 0x3UL)
        {
            return "Reserved bits set for void extent block";
        }

        var coords = DecodeVoidExtentCoordinates(BlockBits);
        bool coordsAll1s = true;
        foreach (var coord in coords)
            coordsAll1s &= coord == ((1 << 13) - 1);

        if (!coordsAll1s && (coords[0] >= coords[1] || coords[2] >= coords[3]))
        {
            return "Void extent texture coordinates are invalid";
        }

        return null;
    }

    internal override int? GetWeightBitCount() => null;

    internal override int? GetWeightStartBit() => null;

    internal override int? GetPartitionsCount() => null;

    internal override ColorEndpointMode? GetEndpointMode(int partition) => null;

    internal override int? GetColorStartBit() => 64;

    internal override int? GetColorValuesCount() => 4;

    internal override int? GetColorBitCount() => 64;

    internal override int? GetColorValuesRange() => (1 << 16) - 1;
}
