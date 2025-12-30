namespace AstcSharp;

public struct RgbColor
{
    public static int BytesPerPixel => 3;
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public RgbColor(int r, int g, int b) : this(
        (byte)Math.Clamp(r, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(g, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(b, byte.MinValue, byte.MaxValue))
    {
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
