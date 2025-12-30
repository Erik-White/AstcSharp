using System.Buffers;

namespace AstcSharp;

public static class Codec
{
    private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private const int BytesPerPixelUnorm8 = 4;

    public static Span<byte> ASTCDecompressToRGBA(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        if (footPrint is null)
            return [];
        
        return DecompressToImage(astcData, width, height, footPrint.Value);
    }

    public static Span<byte> DecompressToImage(AstcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var footprint = file.GetFootprint();
        if (!footprint.HasValue)
            return [];
        
        return DecompressToImage(file.Blocks, file.GetWidth(), file.GetHeight(), footprint.Value);
    }

    // TODO: Return a normal array instead of Span<byte>?
    public static Span<byte> DecompressToImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        int blockWidth = footprint.Width();
        int blockHeight = footprint.Height();

        if (blockWidth == 0 || blockHeight == 0 || width == 0 || height == 0)
            return [];

        int blocksWide = (width + blockWidth - 1) / blockWidth;
        if (blocksWide == 0)
            return [];

        int expectedBlockCount = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight);
        if (astcData.Length % PhysicalAstcBlock.kSizeInBytes != 0 || astcData.Length / PhysicalAstcBlock.kSizeInBytes != expectedBlockCount)
            return [];

        var decodedBlock = Array.Empty<byte>();
        var imageBuffer = new byte[width * height * BytesPerPixelUnorm8];

        try
        {
            // Create a buffer once, and reuse for all the blocks in the image
            decodedBlock = _arrayPool.Rent(footprint.Width() * footprint.Height() * BytesPerPixelUnorm8);
            var decodedPixels = decodedBlock.AsSpan();
            int blocksHigh = (height + footprint.Height() - 1) / footprint.Height();
            int blockIndex = 0;
            
            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex++ * PhysicalAstcBlock.kSizeInBytes;
                    if (blockDataOffset + PhysicalAstcBlock.kSizeInBytes > astcData.Length)
                        continue;

                    DecompressBlock(
                        astcData.Slice(blockDataOffset, PhysicalAstcBlock.kSizeInBytes),
                        footprint,
                        ref decodedPixels);

                    if (decodedPixels.Length == 0)
                        throw new InvalidOperationException("Failed to decompress ASTC block.");

                    for (int pixelY = 0; pixelY < footprint.Height() && (blockY * footprint.Height() + pixelY) < height; pixelY++)
                    {
                        for (int pixelX = 0; pixelX < footprint.Width() && (blockX * footprint.Width() + pixelX) < width; pixelX++)
                        {
                            int srcIndex = (pixelY * footprint.Width() + pixelX) * 4;
                            int dstX = blockX * footprint.Width() + pixelX;
                            int dstY = blockY * footprint.Height() + pixelY;
                            int dstIndex = (dstY * width + dstX) * 4;

                            imageBuffer[dstIndex] = decodedPixels[srcIndex];
                            imageBuffer[dstIndex + 1] = decodedPixels[srcIndex + 1];
                            imageBuffer[dstIndex + 2] = decodedPixels[srcIndex + 2];
                            imageBuffer[dstIndex + 3] = decodedPixels[srcIndex + 3];
                        }
                    }
                }
            }
        }
        finally
        {
            _arrayPool.Return(decodedBlock);
        }

        return imageBuffer;
    }

    /// <summary>
    /// Decompress a single ASTC block to RGBA8 pixel data
    /// </summary>
    /// <param name="blockData">The data to decode</param>
    /// <param name="footprint">The type of ASTC block footprint e.g. 4x4, 5x5, etc.</param>
    /// <returns>The decoded block of pixels as RGBA values</returns>
    public static Span<byte> DecompressBlock(ReadOnlySpan<byte> blockData, Footprint footprint)
    {
        var decodedPixels = Array.Empty<byte>();
        try
        {
            decodedPixels = _arrayPool.Rent(footprint.Width() * footprint.Height() * BytesPerPixelUnorm8);
            var decodedPixelBuffer = decodedPixels.AsSpan();

            DecompressBlock(blockData, footprint, ref decodedPixelBuffer);
        }
        
        finally
        {
            _arrayPool.Return(decodedPixels);
        }

        return decodedPixels;
    }

    /// <inheritdoc cref="DecompressBlock(ReadOnlySpan{byte}, Footprint)"/>
    /// <param name="buffer">The buffer to write the decoded pixels into</param>
    public static void DecompressBlock(ReadOnlySpan<byte> blockData, Footprint footprint, ref Span<byte> buffer)
    {
        // Copy the 16 bytes that make up the ASTC block
        var physicalBlock = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blockData.Slice(0,8).ToArray(),0), BitConverter.ToUInt64(blockData.Slice(8,8).ToArray(),0)));

        var logicalBlock = LogicalAstcBlock.UnpackLogicalBlock(footprint, physicalBlock);
        if (logicalBlock is null)
            return;

        for (int row = 0; row < footprint.Height(); ++row)
        {
            for (int column = 0; column < footprint.Width(); ++column)
            {
                var pixelOffset = (footprint.Width() * row * BytesPerPixelUnorm8) + (column * BytesPerPixelUnorm8);
                var decoded = logicalBlock.ColorAt(column, row);

                buffer[pixelOffset + 0] = decoded.R;
                buffer[pixelOffset + 1] = decoded.G;
                buffer[pixelOffset + 2] = decoded.B;
                buffer[pixelOffset + 3] = decoded.A;
            }
        }

        return;
    }
}
