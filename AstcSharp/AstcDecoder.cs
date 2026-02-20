using System.Buffers;
using System.Buffers.Binary;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp;

public static class AstcDecoder
{
    private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private const int BytesPerPixelUnorm8 = 4;

    public static Span<byte> ASTCDecompressToRGBA(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        
        return DecompressToImage(astcData, width, height, footPrint);
    }

    public static Span<byte> DecompressToImage(AstcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        
        return DecompressToImage(file.Blocks, file.Width, file.Height, file.Footprint);
    }

    // TODO: Return a normal array instead of Span<byte>?
    public static Span<byte> DecompressToImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;

        if (blockWidth == 0 || blockHeight == 0 || width == 0 || height == 0)
            return [];

        int blocksWide = (width + blockWidth - 1) / blockWidth;
        if (blocksWide == 0)
            return [];

        int expectedBlockCount = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight);
        if (astcData.Length % PhysicalBlock.SizeInBytes != 0 || astcData.Length / PhysicalBlock.SizeInBytes != expectedBlockCount)
            return [];

        var decodedBlock = Array.Empty<byte>();
        var imageBuffer = new byte[width * height * BytesPerPixelUnorm8];

        try
        {
            // Create a buffer once, and reuse for all the blocks in the image
            decodedBlock = _arrayPool.Rent(footprint.Width * footprint.Height * BytesPerPixelUnorm8);
            var decodedPixels = decodedBlock.AsSpan();
            int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
            int blockIndex = 0;
            
            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex++ * PhysicalBlock.SizeInBytes;
                    if (blockDataOffset + PhysicalBlock.SizeInBytes > astcData.Length)
                        continue;

                    DecompressBlock(
                        astcData.Slice(blockDataOffset, PhysicalBlock.SizeInBytes),
                        footprint,
                        decodedPixels);

                    if (decodedPixels.Length == 0)
                        throw new InvalidOperationException("Failed to decompress ASTC block.");

                    for (int pixelY = 0; pixelY < footprint.Height && (blockY * footprint.Height + pixelY) < height; pixelY++)
                    {
                        for (int pixelX = 0; pixelX < footprint.Width && (blockX * footprint.Width + pixelX) < width; pixelX++)
                        {
                            int srcIndex = (pixelY * footprint.Width + pixelX) * 4;
                            int dstX = blockX * footprint.Width + pixelX;
                            int dstY = blockY * footprint.Height + pixelY;
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
            decodedPixels = _arrayPool.Rent(footprint.Width * footprint.Height * BytesPerPixelUnorm8);
            var decodedPixelBuffer = decodedPixels.AsSpan();

            DecompressBlock(blockData, footprint, decodedPixelBuffer);
        }
        
        finally
        {
            _arrayPool.Return(decodedPixels);
        }

        return decodedPixels;
    }

    /// <inheritdoc cref="DecompressBlock(ReadOnlySpan{byte}, Footprint)"/>
    /// <param name="buffer">The buffer to write the decoded pixels into</param>
    public static void DecompressBlock(ReadOnlySpan<byte> blockData, Footprint footprint, Span<byte> buffer)
    {
        // Read the 16 bytes that make up the ASTC block as a 128-bit value
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(blockData);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(blockData.Slice(8));
        var blockBits = new UInt128(high, low);
        var physicalBlock = PhysicalBlock.Create(blockBits);

        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, physicalBlock);
        if (logicalBlock is null)
            return;

        for (int row = 0; row < footprint.Height; row++)
        {
            for (int column = 0; column < footprint.Width; ++column)
            {
                var pixelOffset = (footprint.Width * row * BytesPerPixelUnorm8) + (column * BytesPerPixelUnorm8);
                var decoded = logicalBlock.ColorAt(column, row);

                buffer[pixelOffset + 0] = decoded.R;
                buffer[pixelOffset + 1] = decoded.G;
                buffer[pixelOffset + 2] = decoded.B;
                buffer[pixelOffset + 3] = decoded.A;
            }
        }

        return;
    }

    /// <summary>
    /// Decompresses ASTC data to HDR Float16 (Half) format with full 16-bit channel precision.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Array of Half values in RGBA order, normalized to 0.0-1.0+ range.
    /// For HDR content, values may exceed 1.0. Size: width * height * 4 Half values.
    /// </returns>
    /// <remarks>
    /// This method is designed for HDR (High Dynamic Range) content where color values
    /// can exceed the standard 0-255 range. The output uses System.Half (FP16) for each channel,
    /// providing higher precision and dynamic range compared to RGBA8.
    /// <para>
    /// Output format:
    /// - Each pixel: 4 Half values (R, G, B, A) = 8 bytes total
    /// - Values normalized to 0.0-1.0 for standard content, >1.0 for HDR highlights
    /// - LDR content is automatically upscaled to HDR range
    /// </para>
    /// </remarks>
    public static Half[] DecompressToFloat16(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
    {
        int blockWidth = footprint.Width;
        int blockHeight = footprint.Height;

        if (blockWidth == 0 || blockHeight == 0 || width == 0 || height == 0)
            return [];

        int blocksWide = (width + blockWidth - 1) / blockWidth;
        if (blocksWide == 0)
            return [];

        int expectedBlockCount = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight);
        if (astcData.Length % PhysicalBlock.SizeInBytes != 0 || astcData.Length / PhysicalBlock.SizeInBytes != expectedBlockCount)
            return [];

        const int channelsPerPixel = 4; // R, G, B, A
        var imageBuffer = new Half[width * height * channelsPerPixel];
        var decodedBlock = Array.Empty<Half>();

        try
        {
            // Create a buffer once, and reuse for all the blocks in the image
            decodedBlock = ArrayPool<Half>.Shared.Rent(footprint.Width * footprint.Height * channelsPerPixel);
            var decodedPixels = decodedBlock.AsSpan();
            int blocksHigh = (height + footprint.Height - 1) / footprint.Height;
            int blockIndex = 0;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex++ * PhysicalBlock.SizeInBytes;
                    if (blockDataOffset + PhysicalBlock.SizeInBytes > astcData.Length)
                        continue;

                    DecompressBlockToFloat16(
                        astcData.Slice(blockDataOffset, PhysicalBlock.SizeInBytes),
                        footprint,
                        decodedPixels);

                    if (decodedPixels.Length == 0)
                        throw new InvalidOperationException("Failed to decompress ASTC block.");

                    for (int pixelY = 0; pixelY < footprint.Height && (blockY * footprint.Height + pixelY) < height; pixelY++)
                    {
                        for (int pixelX = 0; pixelX < footprint.Width && (blockX * footprint.Width + pixelX) < width; pixelX++)
                        {
                            int srcIndex = (pixelY * footprint.Width + pixelX) * channelsPerPixel;
                            int dstX = blockX * footprint.Width + pixelX;
                            int dstY = blockY * footprint.Height + pixelY;
                            int dstIndex = (dstY * width + dstX) * channelsPerPixel;

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
            ArrayPool<Half>.Shared.Return(decodedBlock);
        }

        return imageBuffer;
    }

    /// <summary>
    /// Decompresses ASTC data to HDR Float16 (Half) format with full 16-bit channel precision.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint type</param>
    /// <returns>
    /// Array of Half values in RGBA order, normalized to 0.0-1.0+ range.
    /// For HDR content, values may exceed 1.0. Size: width * height * 4 Half values.
    /// </returns>
    /// <seealso cref="DecompressToFloat16(ReadOnlySpan{byte}, int, int, Footprint)"/>
    public static Half[] ASTCDecompressToFloat16(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        return DecompressToFloat16(astcData, width, height, footPrint);
    }

    /// <summary>
    /// Decompresses a single ASTC block to HDR Float16 pixel data.
    /// </summary>
    /// <param name="blockData">The 16-byte ASTC block to decode</param>
    /// <param name="footprint">The ASTC block footprint</param>
    /// <param name="buffer">The buffer to write decoded Half values into (must be at least footprint.Width * footprint.Height * 4 elements)</param>
    private static void DecompressBlockToFloat16(ReadOnlySpan<byte> blockData, Footprint footprint, Span<Half> buffer)
    {
        // Read the 16 bytes that make up the ASTC block as a 128-bit value
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(blockData);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(blockData.Slice(8));
        var blockBits = new UInt128(high, low);
        var physicalBlock = PhysicalBlock.Create(blockBits);

        var logicalBlock = LogicalBlock.UnpackLogicalBlock(footprint, physicalBlock);
        if (logicalBlock is null)
            return;

        const int channelsPerPixel = 4;
        for (int row = 0; row < footprint.Height; row++)
        {
            for (int column = 0; column < footprint.Width; ++column)
            {
                var pixelOffset = (footprint.Width * row * channelsPerPixel) + (column * channelsPerPixel);
                var hdrColor = logicalBlock.ColorAtHdr(column, row);

                // Convert ushort (0-65535) to Half (0.0-1.0+)
                var hdrHalf = hdrColor.ToHalfArray();
                buffer[pixelOffset + 0] = hdrHalf[0];
                buffer[pixelOffset + 1] = hdrHalf[1];
                buffer[pixelOffset + 2] = hdrHalf[2];
                buffer[pixelOffset + 3] = hdrHalf[3];
            }
        }
    }
}
