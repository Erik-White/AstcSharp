// Port of astc-codec/src/decoder/footprint.h/cc (expanded)
using System;
using System.Collections.Generic;

namespace AstcSharp.Reference
{
    public enum FootprintType
    {
        k4x4,
        k5x4,
        k5x5,
        k6x5,
        k6x6,
        k8x5,
        k8x6,
        k8x8,
        k10x5,
        k10x6,
        k10x8,
        k10x10,
        k12x10,
        k12x12,
        kCount
    }

    public readonly struct Footprint : IEquatable<Footprint>
    {
        private readonly FootprintType _type;
        private readonly int _w;
        private readonly int _h;

        private Footprint(FootprintType t, int w, int h)
        {
            _type = t;
            _w = w;
            _h = h;
        }

        public FootprintType Type() => _type;
        public int Width() => _w;
        public int Height() => _h;
        public int NumPixels() => _w * _h;

        public static int NumValidFootprints() => (int)FootprintType.kCount;

        public bool Equals(Footprint other) => _type == other._type;
        public override bool Equals(object? obj) => obj is Footprint f && Equals(f);
        public override int GetHashCode() => ((int)_type << 16) ^ (_w << 8) ^ _h;

        public static Footprint Get4x4() => new Footprint(FootprintType.k4x4, 4, 4);
        public static Footprint Get5x4() => new Footprint(FootprintType.k5x4, 5, 4);
        public static Footprint Get5x5() => new Footprint(FootprintType.k5x5, 5, 5);
        public static Footprint Get6x5() => new Footprint(FootprintType.k6x5, 6, 5);
        public static Footprint Get6x6() => new Footprint(FootprintType.k6x6, 6, 6);
        public static Footprint Get8x5() => new Footprint(FootprintType.k8x5, 8, 5);
        public static Footprint Get8x6() => new Footprint(FootprintType.k8x6, 8, 6);
        public static Footprint Get8x8() => new Footprint(FootprintType.k8x8, 8, 8);
        public static Footprint Get10x5() => new Footprint(FootprintType.k10x5, 10, 5);
        public static Footprint Get10x6() => new Footprint(FootprintType.k10x6, 10, 6);
        public static Footprint Get10x8() => new Footprint(FootprintType.k10x8, 10, 8);
        public static Footprint Get10x10() => new Footprint(FootprintType.k10x10, 10, 10);
        public static Footprint Get12x10() => new Footprint(FootprintType.k12x10, 12, 10);
        public static Footprint Get12x12() => new Footprint(FootprintType.k12x12, 12, 12);

        // Parse footprint string like "NxM" into Footprint if valid
        public static Footprint? Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split('x');
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], out var w)) return null;
            if (!int.TryParse(parts[1], out var h)) return null;

            return FromDimensions(w, h);
        }

        public static Footprint? FromDimensions(int width, int height)
        {
            foreach (FootprintType t in Enum.GetValues(typeof(FootprintType)))
            {
                if (t == FootprintType.kCount) continue;
                var fp = FromFootprintType(t);
                if (fp.HasValue && fp.Value.Width() == width && fp.Value.Height() == height) return fp.Value;
            }
            return null;
        }

        public static Footprint? FromFootprintType(FootprintType type)
        {
            return type switch
            {
                FootprintType.k4x4 => Get4x4(),
                FootprintType.k5x4 => Get5x4(),
                FootprintType.k5x5 => Get5x5(),
                FootprintType.k6x5 => Get6x5(),
                FootprintType.k6x6 => Get6x6(),
                FootprintType.k8x5 => Get8x5(),
                FootprintType.k8x6 => Get8x6(),
                FootprintType.k8x8 => Get8x8(),
                FootprintType.k10x5 => Get10x5(),
                FootprintType.k10x6 => Get10x6(),
                FootprintType.k10x8 => Get10x8(),
                FootprintType.k10x10 => Get10x10(),
                FootprintType.k12x10 => Get12x10(),
                FootprintType.k12x12 => Get12x12(),
                _ => (Footprint?)null
            };
        }

        public float Bitrate()
        {
            // Bits per pixel = (128 bits) / (w*h) ??? but use values from test expected numbers
            // In reference: bitrate = 128.0f / (w * h) ? Let's compute from known values
            // For 4x4: bitrate 8 -> 8 = 128/16 -> yes. So use 128f / (Width()*Height())
            return 128f / (_w * _h);
        }

        public int StorageRequirements(int width, int height)
        {
            // Number of blocks needed to cover width/height rounded up times block size in bytes
            int bw = (width + _w - 1) / _w;
            int bh = (height + _h - 1) / _h;
            int numBlocks = bw * bh;
            // Each block stores 128 bits = 16 bytes
            return numBlocks * 16;
        }
    }

    // Helper parser wrapper returning nullable Footprint used by tests
    public static class FootprintParser
    {
        public static Footprint? Parse(string s) => Footprint.Parse(s);
    }
}
