namespace AstcSharp;

public struct RgbaColor
{
    public static int BytesPerPixel => 4;
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }

    /// <summary>
    /// The average of the R, G, and B channels
    /// </summary>
    public byte Average
    {
        get
        {
            var sum = R + G + B;
            return (byte)((sum * 256 + 384) / 768);
        }
    }

    public RgbaColor(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public RgbaColor(int r, int g, int b, int a = byte.MaxValue) : this(
        (byte)Math.Clamp(r, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(g, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(b, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(a, byte.MinValue, byte.MaxValue))
    {
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
