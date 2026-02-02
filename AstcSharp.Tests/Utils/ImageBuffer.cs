namespace AstcSharp.Tests.Utils;

internal class ImageBuffer
{
    public const int Align = 4;

    public byte[] Data { get; }
    public int Stride { get; }
    public int BytesPerPixel { get; }
    public int Width { get; }
    public int Height { get; }
    public int DataSize => Data.Length;

    public ImageBuffer(byte[] data, int width, int height, int bytesPerPixel)
    {
        Data = data;
        BytesPerPixel = bytesPerPixel;
        Width = width;
        Height = height;
        int rowBytes = width * bytesPerPixel;
        Stride = (rowBytes + (Align - 1)) / Align * Align;
    }

    public static ImageBuffer Allocate(int width, int height, int bytesPerPixel)
    {
        int rowBytes = width * bytesPerPixel;
        var stride = (rowBytes + (Align - 1)) / Align * Align;
        var data = new byte[stride * height];

        return new ImageBuffer(data, width, height, bytesPerPixel);
    }
}
