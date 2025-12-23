// Port of astc-codec/src/decoder/types.h
namespace AstcSharp
{
    // Mirror of the reference ColorEndpointMode. Order and values must match the
    // specification/numeric ordering used by the reference implementation.
    public enum ColorEndpointMode
    {
        kLDRLumaDirect = 0,
        kLDRLumaBaseOffset,
        kHDRLumaLargeRange,
        kHDRLumaSmallRange,
        kLDRLumaAlphaDirect,
        kLDRLumaAlphaBaseOffset,
        kLDRRGBBaseScale,
        kHDRRGBBaseScale,
        kLDRRGBDirect,
        kLDRRGBBaseOffset,
        kLDRRGBBaseScaleTwoA,
        kHDRRGBDirect,
        kLDRRGBADirect,
        kLDRRGBABaseOffset,
        kHDRRGBDirectLDRAlpha,
        kHDRRGBDirectHDRAlpha,

        // Number of endpoint modes defined by the ASTC specification.
        kNumColorEndpointModes
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
            R = r; G = g; B = b; A = a;
        }

        public int[] ToArray() => new[] { R, G, B, A };

        public int this[int i]
        {
            get => i switch { 0 => R, 1 => G, 2 => B, 3 => A, _ => throw new System.IndexOutOfRangeException() };
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

        public int[] ToArray4() => new[] { R, G, B, A };

        public static RgbaColor FromArray(int[] arr)
        {
            if (arr == null || arr.Length < 4) throw new System.ArgumentException("Array must have length >= 4");
            return new RgbaColor(arr[0], arr[1], arr[2], arr[3]);
        }

        public static RgbaColor Empty => new RgbaColor(0, 0, 0, 0);
    }

    // We intentionally do not create C# type aliases here. Use RgbaColor and
    // ValueTuple<RgbaColor, RgbaColor> where needed.
}
