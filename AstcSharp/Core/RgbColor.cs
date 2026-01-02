namespace AstcSharp.Core;

public record RgbColor
{
    public static int BytesPerPixel => 3;
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

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
    
    public RgbColor(int r, int g, int b) : this(
        (byte)Math.Clamp(r, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(g, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(b, byte.MinValue, byte.MaxValue))
    {
    }

    public RgbColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public virtual int this[int i] => i switch
    {
        0 => R,
        1 => G,
        2 => B,
        _ => throw new IndexOutOfRangeException()
    };

    public static int SquaredError(RgbColor a, RgbColor b)
        => SquaredError(a, b, BytesPerPixel);

    /// <summary>
    /// Computes the squared error between this color and another color
    /// </summary>
    protected static int SquaredError(RgbColor a, RgbColor b, int bytesPerPixel)
    {
        int result = 0;
        for (int i = 0; i < bytesPerPixel; i++)
        {
            int diff = a[i] - b[i];
            result += diff * diff;
        }

        return result;
    }

    public static RgbColor Empty => new(0, 0, 0);
}