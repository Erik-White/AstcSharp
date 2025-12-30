namespace AstcSharp;

public record RgbaColor : RgbColor
{
    public static new int BytesPerPixel => 4;
    public byte A { get; }

    public RgbaColor(byte r, byte g, byte b, byte a)
        : base(r, g, b)
    {
        A = a;
    }

    public RgbaColor(int r, int g, int b, int a = byte.MaxValue) : this(
        (byte)Math.Clamp(r, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(g, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(b, byte.MinValue, byte.MaxValue),
        (byte)Math.Clamp(a, byte.MinValue, byte.MaxValue))
    {
    }

    public override int this[int i]
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

    public static new RgbaColor Empty => new(0, 0, 0, 0);

    public static new int SquaredError(RgbColor a, RgbColor b)
        => SquaredError(a, b, BytesPerPixel);
}
