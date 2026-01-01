namespace AstcSharp;

public static class RgbaColorExtensions
{
    /// <summary>
    /// Applies the 'blue_contract' function defined in Section C.2.14 of the ASTC specification.
    /// </summary>
    public static RgbaColor WithBlueContract(int red, int green, int blue, int alpha)
        => new(
            r: (red + blue) >> 1,
            g: (green + blue) >> 1,
            b: blue,
            a: alpha);

    /// <summary>
    /// Applies the 'blue_contract' function defined in Section C.2.14 of the ASTC specification.
    /// </summary>
    public static RgbaColor WithBlueContract(this RgbaColor color)
        => WithBlueContract(color.R, color.G, color.B, color.A);

    /// <summary>
    /// The inverse of the 'blue_contract' function defined in Section C.2.14 of the ASTC specification.
    /// </summary>
    /// <param name="color"></param>
    /// <returns></returns>
    public static RgbaColor WithInvertedBlueContract(this RgbaColor color)
        => new(
            r: 2 * color.R - color.B,
            g: 2 * color.G - color.B,
            b: color.B,
            a: color.A);
}