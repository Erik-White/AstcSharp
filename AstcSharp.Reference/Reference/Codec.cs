// Port of src/decoder/codec.{h,cc}
using System;

namespace AstcSharp.Reference
{
    public static class Codec
    {
        private const int kBytesPerPixelUNORM8 = 4;

        public static bool DecompressToImage(ReadOnlySpan<byte> astc_data, int width, int height, Footprint footprint, Span<byte> out_buffer, int out_buffer_stride)
        {
            int block_width = footprint.Width();
            int block_height = footprint.Height();
            if (block_width == 0 || block_height == 0) return false;
            if (width == 0 || height == 0) return false;

            int blocks_wide = (width + block_width - 1) / block_width;
            if (blocks_wide == 0) return false;

            int expected_block_count = ((width + block_width - 1) / block_width) * ((height + block_height - 1) / block_height);
            if (astc_data.Length % PhysicalAstcBlock.kSizeInBytes != 0 || astc_data.Length / PhysicalAstcBlock.kSizeInBytes != expected_block_count) return false;

            if (kBytesPerPixelUNORM8 * width > out_buffer_stride || out_buffer_stride * height < out_buffer.Length) return false;

            for (int i = 0; i < astc_data.Length; i += PhysicalAstcBlock.kSizeInBytes)
            {
                int block_index = i / PhysicalAstcBlock.kSizeInBytes;
                int block_x = block_index % blocks_wide;
                int block_y = block_index / blocks_wide;

                // copy 16 bytes into UInt128Ex
                Span<byte> blkSpan = astc_data.Slice(i, PhysicalAstcBlock.kSizeInBytes).ToArray();
                var pb = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blkSpan.Slice(0,8).ToArray(),0), BitConverter.ToUInt64(blkSpan.Slice(8,8).ToArray(),0)));

                var lb = LogicalAstcBlock.UnpackLogicalBlock(footprint, pb);
                if (lb == null) return false;
                var logical_block = lb!;

                for (int y = 0; y < block_height; ++y)
                {
                    int py = block_height * block_y + y;
                    int out_row_offset = py * out_buffer_stride;

                    for (int x = 0; x < block_width; ++x)
                    {
                        int px = block_width * block_x + x;
                        if (px >= width || py >= height) continue;

                        int pixelOffset = out_row_offset + px * kBytesPerPixelUNORM8;
                        var decoded = logical_block.ColorAt(x, y);
                        out_buffer[pixelOffset + 0] = (byte)decoded.R;
                        out_buffer[pixelOffset + 1] = (byte)decoded.G;
                        out_buffer[pixelOffset + 2] = (byte)decoded.B;
                        out_buffer[pixelOffset + 3] = (byte)decoded.A;
                    }
                }
            }

            return true;
        }

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
    }
}
