namespace AstcSharp;

public struct RgbColor
{
    public static int BytesPerPixel => 3;
    public int R { get; }
    public int G { get; }
    public int B { get; }

    public RgbColor(int r, int g, int b)
    {
        R = Math.Clamp(r, byte.MinValue, byte.MaxValue);
        G = Math.Clamp(g, byte.MinValue, byte.MaxValue);
        B = Math.Clamp(b, byte.MinValue, byte.MaxValue);
    }

    public readonly int this[int i] => i switch
    {
        0 => R,
        1 => G,
        2 => B,
        _ => throw new IndexOutOfRangeException()
    };

    public static RgbColor Empty => new(0, 0, 0);
}
