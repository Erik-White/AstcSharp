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
}
