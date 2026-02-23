using System.Buffers;
using System.Buffers.Binary;
using AstcSharp.Core;
using AstcSharp.IO;
using AstcSharp.TexelBlock;

namespace AstcSharp;

/// <summary>
/// Provides methods to decode ASTC-compressed texture data into uncompressed pixel formats.
/// </summary>
public static class AstcDecoder
{
    private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private const int BytesPerPixelUnorm8 = 4;

    internal static Span<byte> DecompressImage(AstcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        
        return DecompressImage(file.Blocks, file.Width, file.Height, file.Footprint);
    }

    internal static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        
        return DecompressImage(astcData, width, height, footPrint);
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to uncompressed RGBA8 format (4 bytes per pixel).
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>Array of bytes in RGBA8 format (width * height * 4 bytes total)</returns>
    /// <exception cref="InvalidOperationException">If decompression fails for any block</exception>
    public static Span<byte> DecompressImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
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

    /// <summary>
    /// Decompresses a single ASTC block to RGBA8 pixel data
    /// </summary>
    /// <param name="blockData">The data to decode</param>
    /// <param name="footprint">The type of ASTC block footprint e.g. 4x4, 5x5, etc.</param>
    /// <param name="buffer">The buffer to write the decoded pixels into</param>
    /// <returns>The decoded block of pixels as RGBA values</returns>
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
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint (e.g., 4x4, 5x5)</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint)
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
        var imageBuffer = new float[width * height * channelsPerPixel];
        var decodedBlock = Array.Empty<float>();

        try
        {
            // Create a buffer once, and reuse for all the blocks in the image
            decodedBlock = ArrayPool<float>.Shared.Rent(footprint.Width * footprint.Height * channelsPerPixel);
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

                    DecompressHdrBlock(
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
            ArrayPool<float>.Shared.Return(decodedBlock);
        }

        return imageBuffer;
    }

    /// <summary>
    /// Decompresses ASTC-compressed data to RGBA values.
    /// </summary>
    /// <param name="astcData">The ASTC-compressed texture data</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="footprint">The ASTC block footprint type</param>
    /// <returns>
    /// Values in RGBA order. For HDR content, values may exceed 1.0.
    /// </returns>
    public static Span<float> DecompressHdrImage(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprint)
    {
        var footPrint = Footprint.FromFootprintType(footprint);
        return DecompressHdrImage(astcData, width, height, footPrint);
    }

    /// <summary>
    /// Decompresses a single ASTC block to float RGBA values.
    /// </summary>
    /// <param name="blockData">The 16-byte ASTC block to decode</param>
    /// <param name="footprint">The ASTC block footprint</param>
    /// <param name="buffer">The buffer to write decoded values into (must be at least footprint.Width * footprint.Height * 4 elements)</param>
    public static void DecompressHdrBlock(ReadOnlySpan<byte> blockData, Footprint footprint, Span<float> buffer)
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
                logicalBlock.WriteHdrPixel(column, row, buffer.Slice(pixelOffset, channelsPerPixel));
            }
        }
    }
}
