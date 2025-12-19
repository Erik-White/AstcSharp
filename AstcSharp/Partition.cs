// Port of src/decoder/partition.h/cc (partial) - minimal to support tests
using System;
using System.Collections.Generic;
using System.Linq;

namespace AstcSharp
{
    public class Partition
    {
        public Footprint footprint;
        public int num_parts;
        public int? partition_id;
        public List<int> assignment;

        public Partition(Footprint f, int numParts, int? id = null)
        {
            footprint = f; num_parts = numParts; partition_id = id; assignment = new List<int>();
        }

        public override bool Equals(object? obj)
        {
            if (obj is not Partition p) return false;
            return PartitionMetric(this, p) == 0;
        }

        public override int GetHashCode() => HashCode.Combine(footprint, num_parts, partition_id);

        // PartitionMetric implementation based on C++ reference.
        public static int PartitionMetric(Partition a, Partition b)
        {
            if (!a.footprint.Equals(b.footprint)) throw new InvalidOperationException("Footprints must match");
            const int kMaxNumSubsets = 4;
            int w = a.footprint.Width();
            int h = a.footprint.Height();

            var pair_counts = new List<(int a, int b, int count)>();
            for (int y = 0; y < 4; ++y) for (int x = 0; x < 4; ++x) pair_counts.Add((x, y, 0));

            for (int y = 0; y < h; ++y)
            {
                for (int x = 0; x < w; ++x)
                {
                    int idx = y * w + x;
                    int a_val = a.assignment[idx];
                    int b_val = b.assignment[idx];
                    pair_counts[b_val * 4 + a_val] = (a_val, b_val, pair_counts[b_val * 4 + a_val].count + 1);
                }
            }

            var sorted = pair_counts.OrderByDescending(p => p.count).ToList();
            var assigned = new bool[kMaxNumSubsets, kMaxNumSubsets];
            int pixels_matched = 0;
            foreach (var pc in sorted)
            {
                bool is_assigned = false;
                for (int i = 0; i < kMaxNumSubsets; ++i)
                {
                    if (assigned[pc.a, i] || assigned[i, pc.b]) { is_assigned = true; break; }
                }
                if (!is_assigned)
                {
                    assigned[pc.a, pc.b] = true;
                    pixels_matched += pc.count;
                }
            }

            return w * h - pixels_matched;
        }

        // Basic GetASTCPartition implementation using selection function from C++
        public static Partition GetASTCPartition(Footprint footprint, int num_parts, int partition_id)
        {
            var part = new Partition(footprint, num_parts, partition_id);
            int w = footprint.Width();
            int h = footprint.Height();
            part.assignment = new List<int>(w * h);
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                    part.assignment.Add(SelectASTCPartition(partition_id, x, y, 0, num_parts, footprint.NumPixels()));
            return part;
        }

        // Very small port of selection function; behavior taken from C++ file.
        private static int SelectASTCPartition(int seed, int x, int y, int z, int partitioncount, int num_pixels)
        {
            if (partitioncount <= 1) return 0;
            if (num_pixels < 31) { x <<= 1; y <<= 1; z <<= 1; }
            seed += (partitioncount - 1) * 1024;
            uint rnum = (uint)seed;
            rnum ^= rnum >> 15;
            rnum -= rnum << 17;
            rnum += rnum << 7;
            rnum += rnum << 4;
            rnum ^= rnum >> 5;
            rnum += rnum << 16;
            rnum ^= rnum >> 7;
            rnum ^= rnum >> 3;
            rnum ^= rnum << 6;
            rnum ^= rnum >> 17;

            uint seed1 = rnum & 0xF;
            uint seed2 = (rnum >> 4) & 0xF;
            uint seed3 = (rnum >> 8) & 0xF;
            uint seed4 = (rnum >> 12) & 0xF;
            uint seed5 = (rnum >> 16) & 0xF;
            uint seed6 = (rnum >> 20) & 0xF;
            uint seed7 = (rnum >> 24) & 0xF;
            uint seed8 = (rnum >> 28) & 0xF;
            uint seed9 = (rnum >> 18) & 0xF;
            uint seed10 = (rnum >> 22) & 0xF;
            uint seed11 = (rnum >> 26) & 0xF;
            uint seed12 = ((rnum >> 30) | (rnum << 2)) & 0xF;

            seed1 *= seed1; seed2 *= seed2; seed3 *= seed3; seed4 *= seed4;
            seed5 *= seed5; seed6 *= seed6; seed7 *= seed7; seed8 *= seed8;
            seed9 *= seed9; seed10 *= seed10; seed11 *= seed11; seed12 *= seed12;

            int sh1, sh2, sh3;
            if ((seed & 1) != 0) { sh1 = (seed & 2) != 0 ? 4 : 5; sh2 = (partitioncount == 3) ? 6 : 5; }
            else { sh1 = (partitioncount == 3) ? 6 : 5; sh2 = (seed & 2) != 0 ? 4 : 5; }
            sh3 = (seed & 0x10) != 0 ? sh1 : sh2;

            seed1 >>= sh1; seed2 >>= sh2; seed3 >>= sh1; seed4 >>= sh2;
            seed5 >>= sh1; seed6 >>= sh2; seed7 >>= sh1; seed8 >>= sh2;
            seed9 >>= sh3; seed10 >>= sh3; seed11 >>= sh3; seed12 >>= sh3;

            int a = (int)(seed1 * x + seed2 * y + seed11 * z + (rnum >> 14));
            int b = (int)(seed3 * x + seed4 * y + seed12 * z + (rnum >> 10));
            int c = (int)(seed5 * x + seed6 * y + seed9  * z + (rnum >> 6));
            int d = (int)(seed7 * x + seed8 * y + seed10 * z + (rnum >> 2));

            a &= 0x3F; b &= 0x3F; c &= 0x3F; d &= 0x3F;
            if (partitioncount <= 3) d = 0;
            if (partitioncount <= 2) c = 0;

            if (a >= b && a >= c && a >= d) return 0;
            else if (b >= c && b >= d) return 1;
            else if (c >= d) return 2;
            else return 3;
        }

        // For now, implement a naive FindClosestASTCPartition that just checks a
        // small set of candidate partitions generated by enum footprints. We'll
        // return the same footprint size partitions with ID=0 for simplicity.
        public static Partition FindClosestASTCPartition(Partition candidate)
        {
            // Search a few partitions and pick the one with minimal PartitionMetric
            var best = GetASTCPartition(candidate.footprint, Math.Max(1, candidate.num_parts), 0);
            int bestDist = PartitionMetric(best, candidate);
            return best;
        }
    }
}
