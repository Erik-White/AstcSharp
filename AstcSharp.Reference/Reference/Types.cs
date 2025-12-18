// Port of astc-codec/src/decoder/types.h
namespace AstcSharp.Reference
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

    public static class Types
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
    }

    // We intentionally do not create C# type aliases here. Use RgbaColor and
    // ValueTuple<RgbaColor, RgbaColor> where needed.
}
