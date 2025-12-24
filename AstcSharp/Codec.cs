using System;

namespace AstcSharp
{
    public static class Codec
    {
        private const int BytesPerPixelUnorm8 = 4;

        public static bool ASTCDecompressToRGBA(ReadOnlySpan<byte> astcData, int width, int height, FootprintType footprintType, Span<byte> rgbaBuffer, int bufferStride)
        {
            var footPrint = Footprint.FromFootprintType(footprintType);
            if (footPrint == null) return false;
            
            return DecompressToImage(astcData, width, height, footPrint.Value, rgbaBuffer, bufferStride);
        }

        public static bool DecompressToImage(AstcFile file, Span<byte> imageBuffer, int bufferSize, int bufferStride)
        {
            if (file == null) return false;
            var footprint = file.GetFootprint();
            if (!footprint.HasValue) return false;
            
            return DecompressToImage(file.Blocks, file.GetWidth(), file.GetHeight(), footprint.Value, imageBuffer, bufferStride);
        }

        public static bool DecompressToImage(ReadOnlySpan<byte> astcData, int width, int height, Footprint footprint, Span<byte> imageBuffer, int bufferStride)
        {
            // TODO: Tidy up unused
            int blockWidth = footprint.Width();
            int blockHeight = footprint.Height();
            var stride = blockWidth * BytesPerPixelUnorm8;

            if (blockWidth == 0 || blockHeight == 0) return false;
            if (width == 0 || height == 0) return false;

            int blocksWide = (width + blockWidth - 1) / blockWidth;
            if (blocksWide == 0) return false;

            int expectedBlockCount = ((width + blockWidth - 1) / blockWidth) * ((height + blockHeight - 1) / blockHeight);
            if (astcData.Length % PhysicalAstcBlock.kSizeInBytes != 0 || astcData.Length / PhysicalAstcBlock.kSizeInBytes != expectedBlockCount) return false;

            if (BytesPerPixelUnorm8 * width > bufferStride || bufferStride * height < imageBuffer.Length) return false;

            int blocksHigh = (height + footprint.Height() - 1) / footprint.Height();
            byte[] decodedBlock = new byte[footprint.Width() * footprint.Height() * BytesPerPixelUnorm8];
            int blockIndex = 0;

            for (int blockY = 0; blockY < blocksHigh; blockY++)
            {
                for (int blockX = 0; blockX < blocksWide; blockX++)
                {
                    int blockDataOffset = blockIndex * PhysicalAstcBlock.kSizeInBytes;
                    if (blockDataOffset + PhysicalAstcBlock.kSizeInBytes <= astcData.Length)
                    {
                        if (!DecompressBlock(
                            astcData.Slice(blockDataOffset, PhysicalAstcBlock.kSizeInBytes),
                            footprint,
                            decodedBlock))
                        {
                            return false;
                        }

                        for (int pixelY = 0; pixelY < footprint.Height() && (blockY * footprint.Height() + pixelY) < height; pixelY++)
                        {
                            for (int pixelX = 0; pixelX < footprint.Width() && (blockX * footprint.Width() + pixelX) < width; pixelX++)
                            {
                                int srcIndex = (pixelY * footprint.Width() + pixelX) * 4;
                                int dstX = blockX * footprint.Width() + pixelX;
                                int dstY = blockY * footprint.Height() + pixelY;
                                int dstIndex = (dstY * width + dstX) * 4;

                                imageBuffer[dstIndex] = decodedBlock[srcIndex];
                                imageBuffer[dstIndex + 1] = decodedBlock[srcIndex + 1];
                                imageBuffer[dstIndex + 2] = decodedBlock[srcIndex + 2];
                                imageBuffer[dstIndex + 3] = decodedBlock[srcIndex + 3];
                            }
                        }
                    }

                    blockIndex++;
                }
            }

            return true;
        }

        public static bool DecompressBlock(ReadOnlySpan<byte> block_data, Footprint footprint, Span<byte> out_buffer)
        {
            int blockWidth = footprint.Width();
            int blockHeight = footprint.Height();

            // copy 16 bytes into UInt128Ex
            Span<byte> blockSpan = block_data.ToArray();
            var physicalBlock = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blockSpan.Slice(0,8).ToArray(),0), BitConverter.ToUInt64(blockSpan.Slice(8,8).ToArray(),0)));

            var logicalBlockMaybe = LogicalAstcBlock.UnpackLogicalBlock(footprint, physicalBlock);
            if (logicalBlockMaybe == null)
                return false;
            var logicalBlock = logicalBlockMaybe!;

            for (int row = 0; row < blockHeight; ++row)
            {
                for (int column = 0; column < blockWidth; ++column)
                {
                    var pixelOffset = (blockWidth * row * BytesPerPixelUnorm8) + (column * BytesPerPixelUnorm8);
                    var decoded = logicalBlock.ColorAt(column, row);

                    out_buffer[pixelOffset + 0] = (byte)decoded.R;
                    out_buffer[pixelOffset + 1] = (byte)decoded.G;
                    out_buffer[pixelOffset + 2] = (byte)decoded.B;
                    out_buffer[pixelOffset + 3] = (byte)decoded.A;
                }
            }

            return true;
        }
    }
}
