using Microsoft.VisualStudio.TestPlatform.Utilities;

namespace AstcSharp.Tests;

internal class ImageBuffer
{
    // TODO: Use auto-properties
    private readonly byte[] data_;
    private readonly int stride_;
    private readonly int bytesPerPixel_;
    private readonly int width_;
    private readonly int height_;

    public const int Align = 4;

    public ImageBuffer(byte[] data, int width, int height, int bytesPerPixel)
    {
        data_ = data;
        bytesPerPixel_ = bytesPerPixel;
        width_ = width;
        height_ = height;
        int rowBytes = width * bytesPerPixel;
        stride_ = (rowBytes + (Align - 1)) / Align * Align;;
    }

    public static ImageBuffer Allocate(int width, int height, int bytesPerPixel)
    {
        int rowBytes = width * bytesPerPixel;
        var stride = (rowBytes + (Align - 1)) / Align * Align;
        var data = new byte[stride * height];

        return new ImageBuffer(data, width, height, bytesPerPixel);
    }

    public byte[] Data() => data_;

    public int Stride() => stride_;
    public int BytesPerPixel() => bytesPerPixel_;
    public int DataSize() => Data().Length;
    public int Width() => width_;
    public int Height() => height_;
}

internal static class FileBasedHelpers
{
    public static byte[] LoadASTCFile(string basename)
    {
        var filename = Path.Combine("TestData", "Input", basename + ".astc");
        Assert.True(File.Exists(filename), $"Testdata missing: {filename}");
        var data = File.ReadAllBytes(filename);
        Assert.True(data.Length >= 16, "ASTC file too small");
        return data.Skip(16).ToArray();
    }

    public static ImageBuffer LoadExpectedImage(string path)
    {
        const int kBmpHeaderSize = 54;
        var data = File.ReadAllBytes(path);
        Assert.True(data.Length >= kBmpHeaderSize);
        Assert.Equal((byte)'B', data[0]);
        Assert.Equal((byte)'M', data[1]);

        uint dataPos = BitConverter.ToUInt32(data, 0x0A);
        uint imageSize = BitConverter.ToUInt32(data, 0x22);
        ushort bitsPerPixel = BitConverter.ToUInt16(data, 0x1C);
        int width = BitConverter.ToInt32(data, 0x12);
        int height = BitConverter.ToInt32(data, 0x16);

        if (height < 0) height = -height;
        if (imageSize == 0) imageSize = (uint)(width * height * (bitsPerPixel / 8));
        if (dataPos < kBmpHeaderSize) dataPos = kBmpHeaderSize;

        Assert.True(bitsPerPixel == 24 || bitsPerPixel == 32, "BMP bits per pixel mismatch, expected 24 or 32");

        var result = ImageBuffer.Allocate(width, height, bitsPerPixel == 24 ? 3 : 4);
        Assert.True(imageSize <= result.DataSize());

        var stride = result.Stride();

        for (int row = 0; row < height; ++row)
        {
            Array.Copy(data, (int)dataPos + row * stride, result.Data(), row * stride, width * (bitsPerPixel / 8));
        }

        if (bitsPerPixel == 32)
        {
            for (int row = 0; row < height; ++row)
            {
                int rowOffset = row * stride;
                for (int i = 3; i < stride; i += 4)
                {
                    var b = result.Data()[rowOffset + i - 3];
                    result.Data()[rowOffset + i - 3] = result.Data()[rowOffset + i - 1];
                    result.Data()[rowOffset + i - 1] = b;
                }
            }
        }
        else
        {
            for (int row = 0; row < height; ++row)
            {
                int rowOffset = row * stride;
                for (int i = 2; i < stride; i += 3)
                {
                    var tmp = result.Data()[rowOffset + i - 2];
                    result.Data()[rowOffset + i - 2] = result.Data()[rowOffset + i];
                    result.Data()[rowOffset + i] = tmp;
                }
            }
        }

        return result;
    }
}

internal static class ImageUtils
{
    public static void CompareSumOfSquaredDifferences(ImageBuffer expected, ImageBuffer actual, double threshold)
    {
        Assert.Equal(expected.DataSize(), actual.DataSize());
        Assert.Equal(expected.Stride(), actual.Stride());
        Assert.Equal(expected.BytesPerPixel(), actual.BytesPerPixel());

        var expectedData = expected.Data();
        var actualData = actual.Data();

        double sum = 0.0;
        for (int i = 0; i < actualData.Length; ++i)
        {
            double diff = (double)actualData[i] - expectedData[i];
            sum += diff * diff;
        }

        Assert.True(sum <= threshold * actualData.Length, $"Per pixel {(sum / actualData.Length)}, expected <= {threshold}");
        if (sum > threshold * actualData.Length)
        {
            Assert.Equal(expectedData, actualData);
        }
    }
}
