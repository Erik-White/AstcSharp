// Port of astc-codec/src/decoder/weight_infill.{h,cc}
using System;
using System.Collections.Generic;
using AstcSharp.Core;

namespace AstcSharp.BiseEncoding
{
    internal static class WeightInfill
    {
        // The following functions are based on Section C.2.18 of the ASTC specification
        private static int GetScaleFactorD(int block_dim)
        {
            return (int)((1024f + (float)(block_dim >> 1)) / (float)(block_dim - 1));
        }

        private static (int, int) GetGridSpaceCoordinates(Footprint footprint, int s, int t, int weight_dim_x, int weight_dim_y)
        {
            int ds = GetScaleFactorD(footprint.Width);
            int dt = GetScaleFactorD(footprint.Height);

            int cs = ds * s;
            int ct = dt * t;

            int gs = (cs * (weight_dim_x - 1) + 32) >> 6;
            int gt = (ct * (weight_dim_y - 1) + 32) >> 6;

            return (gs, gt);
        }

        // Returns the weight-grid indices that are to be used for bilinearly
        // interpolating the weight to its final value. If the returned value
        // is equal to weight_dim_x * weight_dim_y, it may be ignored.
        private static int[] BilerpGridPointsForWeight((int, int) gridSpaceCoords, int weight_dim_x)
        {
            int js = gridSpaceCoords.Item1 >> 4;
            int jt = gridSpaceCoords.Item2 >> 4;

            int[] result = new int[4];
            result[0] = js + weight_dim_x * jt;
            result[1] = js + weight_dim_x * jt + 1;
            result[2] = js + weight_dim_x * (jt + 1);
            result[3] = js + weight_dim_x * (jt + 1) + 1;

            return result;
        }

        private static int[] BilerpGridPointFactorsForWeight((int, int) gridSpaceCoords)
        {
            int fs = gridSpaceCoords.Item1 & 0xF;
            int ft = gridSpaceCoords.Item2 & 0xF;

            int[] result = new int[4];
            result[3] = (fs * ft + 8) >> 4;
            result[2] = ft - result[3];
            result[1] = fs - result[3];
            result[0] = 16 - fs - ft + result[3];

            return result;
        }

        // Returns the number of bits used to represent the weight grid at the target
        // dimensions and weight range.
        public static int CountBitsForWeights(int weight_dim_x, int weight_dim_y, int target_weight_range)
        {
            int num_weights = weight_dim_x * weight_dim_y;
            return BoundedIntegerSequenceCodec.GetBitCountForRange(num_weights, target_weight_range);
        }

        // Performs weight infill of a grid of weights of size |dim_x * dim_y|. The
        // weights are fit using the algorithm laid out in Section C.2.18 of the ASTC
        // specification. Weights are expected to be passed unquantized and the returned
        // grid will be unquantized as well (i.e. each weight within the range [0, 64]).
        public static List<int> InfillWeights(IReadOnlyList<int> weights, Footprint footprint, int dim_x, int dim_y)
        {
            var result = new List<int>(footprint.PixelCount);
            for (int t = 0; t < footprint.Height; ++t)
            {
                for (int s = 0; s < footprint.Width; ++s)
                {
                    var gridSpaceCoords = GetGridSpaceCoordinates(footprint, s, t, dim_x, dim_y);
                    var gridPts = BilerpGridPointsForWeight(gridSpaceCoords, dim_x);
                    var gridFactors = BilerpGridPointFactorsForWeight(gridSpaceCoords);

                    int weight = 0;
                    for (int i = 0; i < 4; ++i)
                    {
                        if (gridPts[i] < dim_x * dim_y)
                        {
                            weight += weights[gridPts[i]] * gridFactors[i];
                        }
                    }
                    result.Add((weight + 8) >> 4);
                }
            }

            return result;
        }
    }
}
