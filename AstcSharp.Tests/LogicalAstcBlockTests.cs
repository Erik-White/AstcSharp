using System;
using Xunit;
using AstcSharp.Reference;

namespace AstcSharp.Tests
{
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
    }
}
