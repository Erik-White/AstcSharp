// Port of astc-codec/src/decoder/weight_infill.{h,cc}
using System;
using AstcSharp.Core;

namespace AstcSharp.BiseEncoding
{
    internal static class WeightInfill
    {
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
        public static int[] InfillWeights(int[] weights, Footprint footprint, int dim_x, int dim_y)
        {
            var result = new int[footprint.PixelCount];
            var di = DecimationTable.Get(footprint, dim_x, dim_y);
            DecimationTable.InfillWeights(weights, di, result);
            return result;
        }
    }
}
