namespace AstcSharp.Tests;

public class LogicalAstcBlockTests
{
    [Fact]
    public void SetEndpoints_Checkerboard()
    {
        var lb = new LogicalAstcBlock(Footprint.Get8x8());
        for (int j = 0; j < 8; ++j)
        for (int i = 0; i < 8; ++i)
        {
            if (((i ^ j) & 1) == 1) lb.SetWeightAt(i, j, 0);
            else lb.SetWeightAt(i, j, 64);
        }

        var a = new RgbaColor(123,45,67,89);
        var b = new RgbaColor(101,121,31,41);
        lb.SetEndpoints(a, b, 0);

        for (int j = 0; j < 8; ++j)
        for (int i = 0; i < 8; ++i)
        {
            var c = lb.ColorAt(i, j);
            if (((i ^ j) & 1) == 1)
            {
                Assert.Equal(a.R, c.R);
                Assert.Equal(a.G, c.G);
                Assert.Equal(a.B, c.B);
                Assert.Equal(a.A, c.A);
            }
            else
            {
                Assert.Equal(b.R, c.R);
                Assert.Equal(b.G, c.G);
                Assert.Equal(b.B, c.B);
                Assert.Equal(b.A, c.A);
            }
        }
    }

    [Fact]
    public void SetWeightVals_DualPlaneBehavior()
    {
        var lb = new LogicalAstcBlock(Footprint.Get4x4());
        Assert.Equal(Footprint.Get4x4(), lb.GetFootprint());
        Assert.False(lb.IsDualPlane());

        lb.SetWeightAt(2,3,2);
        lb.SetDualPlaneChannel(0);
        Assert.True(lb.IsDualPlane());

        var other = lb; // copy
        Assert.Equal(2, other.WeightAt(2,3));
        Assert.Equal(2, other.DualPlaneWeightAt(0,2,3));

        lb.SetDualPlaneWeightAt(0,2,3,1);
        Assert.Equal(2, lb.WeightAt(2,3));
        Assert.Equal(1, lb.DualPlaneWeightAt(0,2,3));
        for (int i = 1; i < 4; ++i) Assert.Equal(2, lb.DualPlaneWeightAt(i,2,3));

        lb.SetDualPlaneChannel(-1);
        Assert.False(lb.IsDualPlane());
        var other2 = lb;
        Assert.Equal(2, lb.WeightAt(2,3));
        for (int i = 0; i < 4; ++i) Assert.Equal(lb.WeightAt(2,3), other2.DualPlaneWeightAt(i,2,3));
    }

    private static (string image_name, bool has_alpha, Footprint fp, int width, int height)[] GetSyntheticImageTestParams()
        => new[] {
            ("footprint_4x4", false, Footprint.Get4x4(), 32, 32),
            ("footprint_5x4", false, Footprint.Get5x4(), 32, 32),
            ("footprint_5x5", false, Footprint.Get5x5(), 32, 32),
            ("footprint_6x5", false, Footprint.Get6x5(), 32, 32),
            ("footprint_6x6", false, Footprint.Get6x6(), 32, 32),
            ("footprint_8x5", false, Footprint.Get8x5(), 32, 32),
            ("footprint_8x6", false, Footprint.Get8x6(), 32, 32),
            ("footprint_10x5", false, Footprint.Get10x5(), 32, 32),
            ("footprint_10x6", false, Footprint.Get10x6(), 32, 32),
            ("footprint_8x8", false, Footprint.Get8x8(), 32, 32),
            ("footprint_10x8", false, Footprint.Get10x8(), 32, 32),
            ("footprint_10x10", false, Footprint.Get10x10(), 32, 32),
            ("footprint_12x10", false, Footprint.Get12x10(), 32, 32),
            ("footprint_12x12", false, Footprint.Get12x12(), 32, 32),
        };

    public static IEnumerable<object[]> SyntheticParams()
    {
        foreach (var p in GetSyntheticImageTestParams()) yield return new object[] { p.image_name, p.has_alpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(SyntheticParams))]
    public void ImageWithFootprint_Synthetic(string image_name, bool has_alpha, Footprint fp, int width, int height)
    {
        var astc = FileBasedHelpers.LoadASTCFile(image_name);

        var our_decoded_image = ImageBuffer.Allocate(width, height, has_alpha ? 4 : 3);

        int block_width = fp.Width();
        int block_height = fp.Height();

        for (int i = 0; i < astc.Length; i += PhysicalAstcBlock.kSizeInBytes)
        {
            int block_index = i / PhysicalAstcBlock.kSizeInBytes;
            int blocks_wide = (width + block_width - 1) / block_width;
            int block_x = block_index % blocks_wide;
            int block_y = block_index / blocks_wide;

            var blkSpan = astc.AsSpan(i, PhysicalAstcBlock.kSizeInBytes).ToArray();
            var pb = new PhysicalAstcBlock(new UInt128Ex(BitConverter.ToUInt64(blkSpan, 0), BitConverter.ToUInt64(blkSpan, 8)));
            LogicalAstcBlock? lb = LogicalAstcBlock.UnpackLogicalBlock(fp, pb);
            Assert.NotNull(lb);
            var logical_block = lb!;

            for (int y = 0; y < block_height; ++y)
            for (int x = 0; x < block_width; ++x)
            {
                int px = block_width * block_x + x;
                int py = block_height * block_y + y;
                if (px >= width || py >= height) continue;

                var decoded = logical_block.ColorAt(x, y);
                int row = py * our_decoded_image.Stride();
                int off = row + px * our_decoded_image.BytesPerPixel();
                our_decoded_image.Data()[off + 0] = (byte)decoded.R;
                our_decoded_image.Data()[off + 1] = (byte)decoded.G;
                our_decoded_image.Data()[off + 2] = (byte)decoded.B;
                if (has_alpha) our_decoded_image.Data()[off + 3] = (byte)decoded.A;
            }
        }

        var filePath = Path.Combine("TestData", "Expected", image_name + ".bmp");
        var decoded_image = FileBasedHelpers.LoadExpectedImage(filePath);
        ImageUtils.CompareSumOfSquaredDifferences(decoded_image, our_decoded_image, 0.1);
    }

    private static (string image_name, bool has_alpha, Footprint fp, int width, int height)[] GetRealWorldImageTestParams()
        => new[] {
            ("rgb_4x4", false, Footprint.Get4x4(), 224, 288),
            ("rgb_6x6", false, Footprint.Get6x6(), 224, 288),
            ("rgb_8x8", false, Footprint.Get8x8(), 224, 288),
            ("rgb_12x12", false, Footprint.Get12x12(), 224, 288),
            ("rgb_5x4", false, Footprint.Get5x4(), 224, 288),
        };

    public static IEnumerable<object[]> RealWorldParams()
    {
        foreach (var p in GetRealWorldImageTestParams()) yield return new object[] { p.image_name, p.has_alpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(RealWorldParams))]
    public void ImageWithFootprint_RealWorld(string image_name, bool has_alpha, Footprint fp, int width, int height)
    {
        // Reuse synthetic test logic since it performs the same decode+compare
        ImageWithFootprint_Synthetic(image_name, has_alpha, fp, width, height);
    }

    private static (string image_name, bool has_alpha, Footprint fp, int width, int height)[] GetTransparentImageTestParams()
        => new[] {
            ("atlas_small_4x4", true, Footprint.Get4x4(), 256, 256),
            ("atlas_small_5x5", true, Footprint.Get5x5(), 256, 256),
            ("atlas_small_6x6", true, Footprint.Get6x6(), 256, 256),
            ("atlas_small_8x8", true, Footprint.Get8x8(), 256, 256),
        };

    public static IEnumerable<object[]> TransparentParams()
    {
        foreach (var p in GetTransparentImageTestParams()) yield return new object[] { p.image_name, p.has_alpha, p.fp, p.width, p.height };
    }

    [Theory]
    [MemberData(nameof(TransparentParams))]
    public void ImageWithFootprint_Transparent(string image_name, bool has_alpha, Footprint fp, int width, int height)
    {
        ImageWithFootprint_Synthetic(image_name, has_alpha, fp, width, height);
    }
}
