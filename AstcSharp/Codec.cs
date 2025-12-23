using System;

namespace AstcSharp
{
    public static class Codec
    {
        private const int kBytesPerPixelUNORM8 = 4;

        public static bool ASTCDecompressToRGBA(ReadOnlySpan<byte> astc_data, int width, int height, FootprintType footprintType, Span<byte> out_buffer, int out_buffer_stride)
        {
            var maybe = Footprint.FromFootprintType(footprintType);
            if (maybe == null) return false;
            return DecompressToImage(astc_data, width, height, maybe.Value, out_buffer, out_buffer_stride);
        }

        public static bool DecompressToImage(AstcFile file, Span<byte> out_buffer, int out_buffer_size, int out_buffer_stride)
        {
            if (file == null) return false;
            var fp = file.GetFootprint();
            if (!fp.HasValue) return false;
            return DecompressToImage(file.Blocks, file.GetWidth(), file.GetHeight(), fp.Value, out_buffer, out_buffer_stride);
        }

        public static bool DecompressToImage(ReadOnlySpan<byte> astc_data, int width, int height, Footprint footprint, Span<byte> out_buffer, int out_buffer_stride)
        {
            // TODO: Tidy up unused
            int block_width = footprint.Width();
            int block_height = footprint.Height();
            var stride = block_width * kBytesPerPixelUNORM8;
            Span<byte> block_buffer = new byte[stride * block_height];

            if (block_width == 0 || block_height == 0) return false;
            if (width == 0 || height == 0) return false;

            int blocks_wide = (width + block_width - 1) / block_width;
            if (blocks_wide == 0) return false;

            int expected_block_count = ((width + block_width - 1) / block_width) * ((height + block_height - 1) / block_height);
            if (astc_data.Length % PhysicalAstcBlock.kSizeInBytes != 0 || astc_data.Length / PhysicalAstcBlock.kSizeInBytes != expected_block_count) return false;

            if (kBytesPerPixelUNORM8 * width > out_buffer_stride || out_buffer_stride * height < out_buffer.Length) return false;

            int blocksWide = (width + footprint.Width() - 1) / footprint.Width();
            int blocksHigh = (height + footprint.Height() - 1) / footprint.Height();
            
            byte[] decodedBlock = new byte[footprint.Width() * footprint.Height() * kBytesPerPixelUNORM8];
            int blockIndex = 0;

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    int blockDataOffset = blockIndex * PhysicalAstcBlock.kSizeInBytes;
                    if (blockDataOffset + PhysicalAstcBlock.kSizeInBytes <= astc_data.Length)
                    {
                        if (!DecompressBlock(
                            astc_data.Slice(blockDataOffset, PhysicalAstcBlock.kSizeInBytes),
                            footprint,
                            decodedBlock))
                        {
                            return false;
                        }

                        for (int py = 0; py < footprint.Height() && (by * footprint.Height() + py) < height; py++)
                        {
                            for (int px = 0; px < footprint.Width() && (bx * footprint.Width() + px) < width; px++)
                            {
                                int srcIndex = (py * footprint.Width() + px) * 4;
                                int dstX = bx * footprint.Width() + px;
                                int dstY = by * footprint.Height() + py;
                                int dstIndex = (dstY * width + dstX) * 4;

                                out_buffer[dstIndex] = decodedBlock[srcIndex];
                                out_buffer[dstIndex + 1] = decodedBlock[srcIndex + 1];
                                out_buffer[dstIndex + 2] = decodedBlock[srcIndex + 2];
                                out_buffer[dstIndex + 3] = decodedBlock[srcIndex + 3];
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
            int block_width = footprint.Width();
            int block_height = footprint.Height();

            // copy 16 bytes into UInt128Ex
            Span<byte> blkSpan = block_data.ToArray();
            var pb = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blkSpan.Slice(0,8).ToArray(),0), BitConverter.ToUInt64(blkSpan.Slice(8,8).ToArray(),0)));

            var lb = LogicalAstcBlock.UnpackLogicalBlock(footprint, pb);
            if (lb == null)
                return false;
            var logical_block = lb!;

            for (int row = 0; row < block_height; ++row)
            {
                for (int column = 0; column < block_width; ++column)
                {
                    var pixelOffset = (block_width * row * kBytesPerPixelUNORM8) + (column * kBytesPerPixelUNORM8);
                    var decoded = logical_block.ColorAt(column, row);

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
