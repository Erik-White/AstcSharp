using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AstcSharp.Tests
{
    internal class ImageBuffer
    {
        private byte[]? data_;
        private int stride_;
        private int bytesPerPixel_;
        private int width_;
        private int height_;

        public void Allocate(int width, int height, int bytesPerPixel)
        {
            width_ = width;
            height_ = height;
            bytesPerPixel_ = bytesPerPixel;
            int rowBytes = width * bytesPerPixel;
            stride_ = ((rowBytes + (ImageBuffer.Align - 1)) / ImageBuffer.Align) * ImageBuffer.Align;
            data_ = new byte[stride_ * height];
        }

        public byte[] Data()
        {
            return data_ ?? Array.Empty<byte>();
        }

        public int Stride() => stride_;
        public int BytesPerPixel() => bytesPerPixel_;
        public int DataSize() => Data().Length;

        public int Width() => width_;
        public int Height() => height_;

        public const int Align = 4;
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

        public static ImageBuffer LoadGoldenImageWithAlpha(string basename)
        {
            var filename = Path.Combine("TestData", "Expected", basename + ".bmp");
            var result = new ImageBuffer();
            LoadGoldenBmp(filename, result);
            Assert.Equal(4, result.BytesPerPixel());
            return result;
        }

        public static ImageBuffer LoadGoldenImage(string basename)
        {
            var filename = Path.Combine("TestData", "Expected", basename + ".bmp");
            var result = new ImageBuffer();
            LoadGoldenBmp(filename, result);
            Assert.Equal(3, result.BytesPerPixel());
            return result;
        }

        private static void LoadGoldenBmp(string path, ImageBuffer result)
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

            result.Allocate(width, height, bitsPerPixel == 24 ? 3 : 4);
            Assert.True(imageSize <= result.DataSize());

            var resultData = result.Data();
            var stride = result.Stride();

            for (int row = 0; row < height; ++row)
            {
                Array.Copy(data, (int)dataPos + row * stride, resultData, row * stride, width * (bitsPerPixel / 8));
            }

            if (bitsPerPixel == 32)
            {
                for (int row = 0; row < height; ++row)
                {
                    int rowOffset = row * stride;
                    for (int i = 3; i < stride; i += 4)
                    {
                        var b = resultData[rowOffset + i - 3];
                        resultData[rowOffset + i - 3] = resultData[rowOffset + i - 1];
                        resultData[rowOffset + i - 1] = b;
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
                        var tmp = resultData[rowOffset + i - 2];
                        resultData[rowOffset + i - 2] = resultData[rowOffset + i];
                        resultData[rowOffset + i] = tmp;
                    }
                }
            }
        }
    }

    internal static class ImageUtils
    {
        public static void CompareSumOfSquaredDifferences(ImageBuffer golden, ImageBuffer image, double threshold)
        {
            Assert.Equal(golden.DataSize(), image.DataSize());
            Assert.Equal(golden.Stride(), image.Stride());
            Assert.Equal(golden.BytesPerPixel(), image.BytesPerPixel());

            var image_data = image.Data();
            var golden_data = golden.Data();

            double sum = 0.0;
            for (int i = 0; i < image_data.Length; ++i)
            {
                double diff = (double)image_data[i] - golden_data[i];
                sum += diff * diff;
            }

            Assert.True(sum <= threshold * image_data.Length, $"Per pixel {(sum / image_data.Length)}, expected <= {threshold}");
            if (sum > threshold * image_data.Length)
            {
                Assert.Equal(golden_data, image_data);
            }
        }
    }
}
