using AstcSharp.BiseEncoding;
using AstcSharp.ColorEncoding;
using AstcSharp.Core;

namespace AstcSharp.TexelBlock;

/// <summary>
/// A standard (non-void-extent) physical ASTC texel block
/// </summary>
internal sealed class StandardPhysicalBlock : PhysicalBlock
{
    public StandardPhysicalBlock(UInt128 bits) : base(bits)
    {
    }

    public override bool IsVoidExtent => false;

    public override bool IsDualPlane
        => !IsIllegalEncoding && DecodeDualPlaneBit(BlockBits);

    internal override (int Width, int Height)? GetWeightGridDimensions()
    {
        var (weightGridProperties, _) = DecodeWeightProperties(BlockBits);

        return weightGridProperties is not null && !IsIllegalEncoding
            ? (weightGridProperties.Value.Width, weightGridProperties.Value.Height)
            : null;
    }

    internal override int? GetWeightRange()
    {
        var (weightGridProperties, _) = DecodeWeightProperties(BlockBits);

        return weightGridProperties is not null && !IsIllegalEncoding
            ? weightGridProperties.Value.Range
            : null;
    }

    internal override int[]? GetVoidExtentCoordinates() => null;

    internal override string? IdentifyInvalidEncodingIssues()
    {
        // Standard blocks must have weights specified. DecodeWeightProps will return
        // the weight specifications if they exist and are legal according to C.2.24,
        // and will otherwise be empty.
        var (props, error) = DecodeWeightProperties(BlockBits);
        if (props == null)
        {
            return error;
        }

        int numColorVals = DecodeNumColorValues(BlockBits);
        if (numColorVals > 18) return "Too many color values";

        int numPartitions = DecodePartitionsCount(BlockBits);
        int dualPlaneStartPos = DecodeDualPlaneBitStartPosition(BlockBits);
        int colorStartBit = (numPartitions == 1) ? 17 : 29;

        int requiredColorBits = ((13 * numColorVals) + 4) / 5;
        int availableColorBits = dualPlaneStartPos - colorStartBit;
        if (availableColorBits < requiredColorBits) return "Not enough color bits";

        if (numPartitions == 4 && DecodeDualPlaneBit(BlockBits))
            return "Both four partitions and dual plane specified";

        return null;
    }

    internal override int? GetWeightBitCount()
        => !IsIllegalEncoding
            ? DecodeNumWeightBits(BlockBits)
            : null;

    internal override int? GetWeightStartBit()
        => !IsIllegalEncoding
            ? 128 - DecodeNumWeightBits(BlockBits)
            : null;

    internal override int? GetPartitionsCount()
        => !IsIllegalEncoding
            ? DecodePartitionsCount(BlockBits)
            : null;

    internal override ColorEndpointMode? GetEndpointMode(int partition)
        => partition >= 0 && DecodePartitionsCount(BlockBits) > partition
            ? DecodeEndpointMode(BlockBits, partition)
            : null;

    internal override int? GetColorStartBit()
    {
        var numPartitions = GetPartitionsCount();
        if (!numPartitions.HasValue) return null;

        return (numPartitions.Value == 1) ? 17 : 29;
    }

    internal override int? GetColorValuesCount()
    {
        return !IsIllegalEncoding
            ? DecodeNumColorValues(BlockBits)
            : null;
    }

    internal override int? GetColorBitCount()
    {
        if (IsIllegalEncoding) return null;

        var (colorBits, _) = GetColorValuesInfo();

        return colorBits;
    }

    internal override int? GetColorValuesRange()
    {
        if (IsIllegalEncoding) return null;

        var (_, colorRange) = GetColorValuesInfo();

        return colorRange;
    }
}
