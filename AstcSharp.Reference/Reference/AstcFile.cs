using System;
using System.Text;

namespace AstcSharp.Reference
{
    public sealed class AstcFile
    {
        public struct Header
        {
            public int Magic; // should be 0x5CA1AB13 (little-endian)
            public byte BlockDimX;
            public byte BlockDimY;
            public byte BlockDimZ;
            public int Xsize;
            public int Ysize;
            public int Zsize;
        }

        private Header header_;
        private byte[] blocks_;

        private AstcFile(Header h, byte[] blocks)
        {
            header_ = h;
            blocks_ = blocks;
        }

        public static AstcFile? LoadFromMemory(byte[] data, out string? error)
        {
            error = null;
            if (data == null || data.Length < 16)
            {
                error = "data too small";
                return null;
            }

            // ASTC header is 16 bytes: magic (4), blockdim (3), xsize,y,z (each 3 little-endian bytes)
            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != 0x5CA1AB13u)
            {
                error = "invalid magic";
                return null;
            }

            byte blockdimx = data[4];
            byte blockdimy = data[5];
            byte blockdimz = data[6];

            int xsize = data[7] | (data[8] << 8) | (data[9] << 16);
            int ysize = data[10] | (data[11] << 8) | (data[12] << 16);
            int zsize = data[13] | (data[14] << 8) | (data[15] << 16);

            var hdr = new Header { Magic = (int)magic, BlockDimX = blockdimx, BlockDimY = blockdimy, BlockDimZ = blockdimz, Xsize = xsize, Ysize = ysize, Zsize = zsize };

            // Remaining bytes are blocks; C++ reference keeps them as string; here we keep as byte[]
            var blocks_len = data.Length - 16;
            var blocks = new byte[blocks_len];
            Array.Copy(data, 16, blocks, 0, blocks_len);

            return new AstcFile(hdr, blocks);
        }

        public int GetWidth() => header_.Xsize;
        public int GetHeight() => header_.Ysize;
        public int GetDepth() => header_.Zsize;

        public Footprint? GetFootprint()
        {
            // Map block dims to FootprintType
            switch ((header_.BlockDimX, header_.BlockDimY))
            {
                case (4,4): return Footprint.FromFootprintType(FootprintType.k4x4).Value;
                case (5,4): return Footprint.FromFootprintType(FootprintType.k5x4).Value;
                case (5,5): return Footprint.FromFootprintType(FootprintType.k5x5).Value;
                case (6,5): return Footprint.FromFootprintType(FootprintType.k6x5).Value;
                case (6,6): return Footprint.FromFootprintType(FootprintType.k6x6).Value;
                case (8,5): return Footprint.FromFootprintType(FootprintType.k8x5).Value;
                case (8,6): return Footprint.FromFootprintType(FootprintType.k8x6).Value;
                case (8,8): return Footprint.FromFootprintType(FootprintType.k8x8).Value;
                case (10,5): return Footprint.FromFootprintType(FootprintType.k10x5).Value;
                case (10,6): return Footprint.FromFootprintType(FootprintType.k10x6).Value;
                case (10,8): return Footprint.FromFootprintType(FootprintType.k10x8).Value;
                case (10,10): return Footprint.FromFootprintType(FootprintType.k10x10).Value;
                case (12,10): return Footprint.FromFootprintType(FootprintType.k12x10).Value;
                case (12,12): return Footprint.FromFootprintType(FootprintType.k12x12).Value;
                default: return null;
            }
        }

        public ReadOnlySpan<byte> Blocks => blocks_;
    }
}

    namespace AstcSharp.Reference.Tools
    {
        public static class AstcInspector
        {
            // Inspect metadata for an ASTC file's bytes and return a short summary.
            public static string Inspect(byte[] astcBytes)
            {
                var file = AstcFile.LoadFromMemory(astcBytes, out var err);
                if (file == null) return $"Error: {err}";
                var fp = file.GetFootprint();
                string fpStr = fp.HasValue ? fp.Value.Type().ToString() : "unknown";
                return $"W={file.GetWidth()} H={file.GetHeight()} FP={fpStr} Blocks={file.Blocks.Length}";
            }
        }
    }
