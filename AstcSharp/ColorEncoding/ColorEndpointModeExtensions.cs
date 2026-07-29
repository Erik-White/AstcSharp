namespace AstcSharp.ColorEncoding;

internal static class ColorEndpointModeExtensions
{
    // The 16 colour endpoint modes (spec §C.2.11) are grouped into 4 classes of 4 modes each.
    private const int ModesPerClass = 4;

    public static int GetEndpointModeClass(this ColorEndpointMode mode)
        => (int)mode / ModesPerClass;

    public static int GetColorValuesCount(this ColorEndpointMode mode)
        => (mode.GetEndpointModeClass() + 1) * 2;

    /// <summary>
    /// Determines whether the specified endpoint mode uses HDR (High Dynamic Range) encoding.
    /// </summary>
    /// <returns>
    /// True if the mode is one of the 6 HDR modes
    /// </returns>
    public static bool IsHdr(this ColorEndpointMode mode)
        => mode switch
        {
            ColorEndpointMode.HdrLumaLargeRange => true,      // Mode 2
            ColorEndpointMode.HdrLumaSmallRange => true,      // Mode 3
            ColorEndpointMode.HdrRgbBaseScale => true,        // Mode 7
            ColorEndpointMode.HdrRgbDirect => true,           // Mode 11
            ColorEndpointMode.HdrRgbDirectLdrAlpha => true,   // Mode 14
            ColorEndpointMode.HdrRgbDirectHdrAlpha => true,   // Mode 15
            _ => false
        };
}
