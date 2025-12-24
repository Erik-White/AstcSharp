// Port of astc-codec/src/decoder/types.h
namespace AstcSharp
{
    // Mirror of the reference ColorEndpointMode. Order and values must match the
    // specification/numeric ordering used by the reference implementation.
    public enum ColorEndpointMode
    {
        kLdrLumaDirect = 0,
        kLdrLumaBaseOffset,
        kHdrLumaLargeRange,
        kHdrLumaSmallRange,
        kLdrLumaAlphaDirect,
        kLdrLumaAlphaBaseOffset,
        kLdrRgbBaseScale,
        kHdrRgbBaseScale,
        kLdrRgbDirect,
        kLdrRgbBaseOffset,
        kLdrRgbBaseScaleTwoA,
        kHdrRgbDirect,
        kLdrRgbaDirect,
        kLdrRgbaBaseOffset,
        kHdrRgbDirectLdrAlpha,
        kHdrRgbDirectHdrAlpha,

        // Number of endpoint modes defined by the ASTC specification.
        kColorEndpointModeCount
    }

    internal static class Types
    {
        public static int EndpointModeClass(ColorEndpointMode mode) => ((int)mode) / 4;

        public static int NumColorValuesForEndpointMode(ColorEndpointMode mode)
            => (EndpointModeClass(mode) + 1) * 2;
    }

    public struct RgbColor
    {
        public int R;
        public int G;
        public int B;

        public RgbColor(int r, int g, int b)
        {
            R = r; G = g; B = b;
        }

        public int[] ToArray() => new[] { R, G, B };

        public int this[int i]
        {
            get => i switch { 0 => R, 1 => G, 2 => B, _ => throw new System.IndexOutOfRangeException() };
            set
            {
                switch (i)
                {
                    case 0: R = value; break;
                    case 1: G = value; break;
                    case 2: B = value; break;
                    default: throw new System.IndexOutOfRangeException();
                }
            }
        }
    }

    public struct RgbaColor
    {
        public int R;
        public int G;
        public int B;
        public int A;

        public RgbaColor(int r, int g, int b, int a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }


        public int this[int i]
        {
            readonly get => i switch
            {
                0 => R,
                1 => G,
                2 => B,
                3 => A,
                _ => throw new IndexOutOfRangeException()
            };
            set
            {
                switch (i)
                {
                    case 0: R = value; break;
                    case 1: G = value; break;
                    case 2: B = value; break;
                    case 3: A = value; break;
                    default: throw new System.IndexOutOfRangeException();
                }
            }
        }

        public readonly int[] ToArray() => [R, G, B, A];

        public readonly int[] ToArray4() => [R, G, B, A];

        public static RgbaColor Empty => new(0, 0, 0, 0);

        public static RgbaColor FromArray(int[] array)
        {
            if (array == null || array.Length < 4) throw new System.ArgumentException("Array must have length >= 4");
            return new RgbaColor(array[0], array[1], array[2], array[3]);
        }
    }
}
