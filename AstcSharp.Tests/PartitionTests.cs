using Xunit;
using AstcSharp;
using System.Collections.Generic;

namespace AstcSharp.Tests
{
    public class PartitionTests
    {
        [Fact]
        public void TestSimplePartitionMetric()
        {
            var a = new Partition(Footprint.Get6x6(), 2);
            var b = new Partition(Footprint.Get6x6(), 2);
            a.assignment = new List<int>(new int[] {
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,1
            });
            b.assignment = new List<int>(new int[] {
                1,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0
            });
            int dist = Partition.PartitionMetric(a, b);
            Assert.Equal(2, dist);
        }

        [Fact]
        public void TestDiffPartsPartitionMetric()
        {
            var a = new Partition(Footprint.Get4x4(), 2);
            var b = new Partition(Footprint.Get4x4(), 3);
            a.assignment = new List<int>(new int[] {
                2,2,2,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,1
            });
            b.assignment = new List<int>(new int[] {
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            });
            int dist = Partition.PartitionMetric(a, b);
            Assert.Equal(3, dist);
        }

        [Fact]
        public void TestDiffMappingPartitionMetric()
        {
            var a = new Partition(Footprint.Get4x4(), 2);
            var b = new Partition(Footprint.Get4x4(), 3);
            a.assignment = new List<int>(new int[] {
                0,1,2,2,
                2,2,2,2,
                2,2,2,2,
                2,2,2,2
            });
            b.assignment = new List<int>(new int[] {
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            });
            int dist = Partition.PartitionMetric(a, b);
            Assert.Equal(1, dist);
        }

        [Fact]
        public void TestSpecificPartition()
        {
            var astc = Partition.GetASTCPartition(Footprint.Get10x6(), 3, 557);
            int[] expected = new int[] {
                0,0,0,0,1,1,1,2,2,2,
                0,0,0,0,1,1,1,2,2,2,
                0,0,0,0,1,1,1,2,2,2,
                0,0,0,0,1,1,1,2,2,2,
                0,0,0,0,1,1,1,2,2,2,
                0,0,0,0,1,1,1,2,2,2
            };
            Assert.Equal(expected, astc.assignment);
        }

        [Fact]
        public void TestEstimatedPartitionSubsets()
        {
            var partition = new Partition(Footprint.Get6x6(), 2);
            partition.assignment = new List<int> {
                0,0,1,1,1,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,1,1,1,1,1,
                0,0,0,0,0,0,
                1,1,1,1,1,1
            };
            var astc = Partition.FindClosestASTCPartition(partition);
            Assert.Equal(partition.num_parts, astc.num_parts);
        }

        [Fact]
        public void UniquePartitionResults()
        {
            var partition = new Partition(Footprint.Get6x6(), 2);
            partition.assignment = new List<int> {
                0,1,1,1,1,1,
                0,1,1,1,1,1,
                0,1,1,1,1,1,
                0,1,1,1,1,1,
                0,1,1,1,1,1,
                0,1,1,1,1,1
            };
            var parts = new List<Partition> { Partition.GetASTCPartition(Footprint.Get6x6(), 2, 0), Partition.GetASTCPartition(Footprint.Get6x6(), 2, 1) };
            Assert.NotEqual(parts[0].assignment, parts[1].assignment);
        }
    }
}
