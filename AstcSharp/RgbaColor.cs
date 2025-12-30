namespace AstcSharp;

public struct RgbaColor
{
    public static int BytesPerPixel => 4;
    public int R { get; }
    public int G { get; }
    public int B { get; }
    public int A { get; }

    public RgbaColor(int r, int g, int b, int a)
    {
        // Sould RGBA be bytes instead of ints?
        R = Math.Clamp(r, byte.MinValue, byte.MaxValue);
        G = Math.Clamp(g, byte.MinValue, byte.MaxValue);
        B = Math.Clamp(b, byte.MinValue, byte.MaxValue);
        A = Math.Clamp(a, byte.MinValue, byte.MaxValue);
    }

    public int this[int i]
    {
        get => i switch
        {
            0 => R,
            1 => G,
            2 => B,
            3 => A,
            _ => throw new IndexOutOfRangeException()
        };
    }

    public static RgbaColor Empty => new(0, 0, 0, 0);
}
