using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using AwesomeAssertions;

namespace AstcSharp.Tests;

public class PartitionTests
{

    [Fact]
    public void PartitionMetric_WithSimplePartitions_ShouldCalculateCorrectDistance()
    {
        var partitionA = new Partition(Footprint.Get6x6(), 2)
        {
            assignment =
            [
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,1
            ]
        };

        var partitionB = new Partition(Footprint.Get6x6(), 2)
        {
            assignment =
            [
                1,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0
            ]
        };

        int distance = Partition.PartitionMetric(partitionA, partitionB);

        distance.Should().Be(2);
    }

    [Fact]
    public void PartitionMetric_WithDifferentPartCounts_ShouldCalculateCorrectDistance()
    {
        var partitionA = new Partition(Footprint.Get4x4(), 2)
        {
            assignment =
            [
                2,2,2,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,1
            ]
        };

        var partitionB = new Partition(Footprint.Get4x4(), 3)
        {
            assignment =
            [
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            ]
        };

        int distance = Partition.PartitionMetric(partitionA, partitionB);

        distance.Should().Be(3);
    }

    [Fact]
    public void PartitionMetric_WithDifferentMapping_ShouldCalculateCorrectDistance()
    {
        var partitionA = new Partition(Footprint.Get4x4(), 2)
        {
            assignment =
            [
                0,1,2,2,
                2,2,2,2,
                2,2,2,2,
                2,2,2,2
            ]
        };

        var partitionB = new Partition(Footprint.Get4x4(), 3)
        {
            assignment =
            [
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            ]
        };

        int distance = Partition.PartitionMetric(partitionA, partitionB);

        distance.Should().Be(1);
    }

    [Fact]
    public void GetASTCPartition_WithSpecificParameters_ShouldReturnExpectedAssignment()
    {
        int[] expected =
        [
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2
        ];

        var partition = Partition.GetASTCPartition(Footprint.Get10x6(), 3, 557);

        partition.assignment.Should().Equal(expected);
    }

    [Fact]
    public void GetASTCPartition_WithDifferentIds_ShouldProduceUniqueAssignments()
    {
        var partition0 = Partition.GetASTCPartition(Footprint.Get6x6(), 2, 0);
        var partition1 = Partition.GetASTCPartition(Footprint.Get6x6(), 2, 1);

        partition0.assignment.Should().NotEqual(partition1.assignment);
    }



    [Fact]
    public void FindClosestASTCPartition_ShouldPreservePartitionCount()
    {
        var partition = new Partition(Footprint.Get6x6(), 2)
        {
            assignment =
            [
                0,0,1,1,1,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,1,1,1,1,1,
                0,0,0,0,0,0,
                1,1,1,1,1,1
            ]
        };

        var closestAstcPartition = Partition.FindClosestASTCPartition(partition);

        closestAstcPartition.numParts.Should().Be(partition.numParts);
    }

    [Fact]
    public void FindClosestASTCPartition_WithModifiedPartition_ShouldReturnValidASTCPartition()
    {
        var astcPartition = Partition.GetASTCPartition(Footprint.Get12x12(), 3, 0x3CB);
        var modifiedPartition = new Partition(astcPartition.footprint, astcPartition.numParts)
        {
            assignment = [.. astcPartition.assignment]
        };
        modifiedPartition.assignment[0]++;

        // Find closest ASTC partition
        var closestPartition = Partition.FindClosestASTCPartition(modifiedPartition);

        // The closest partition should be a valid ASTC partition with the same footprint and number of parts
        closestPartition.footprint.Should().Be(astcPartition.footprint);
        closestPartition.numParts.Should().Be(astcPartition.numParts);
        closestPartition.partitionId.Should().HaveValue("returned partition should have a valid ID");

        // Verify we can retrieve the same partition again using its ID
        var verifyPartition = Partition.GetASTCPartition(
            closestPartition.footprint,
            closestPartition.numParts,
            closestPartition.partitionId!.Value);
        verifyPartition.Should().Be(closestPartition);
    }

    [Theory]
    [InlineData(FootprintType.Footprint4x4)]
    [InlineData(FootprintType.Footprint5x4)]
    [InlineData(FootprintType.Footprint5x5)]
    [InlineData(FootprintType.Footprint6x5)]
    [InlineData(FootprintType.Footprint6x6)]
    [InlineData(FootprintType.Footprint8x5)]
    [InlineData(FootprintType.Footprint8x6)]
    [InlineData(FootprintType.Footprint8x8)]
    [InlineData(FootprintType.Footprint10x5)]
    [InlineData(FootprintType.Footprint10x6)]
    [InlineData(FootprintType.Footprint10x8)]
    [InlineData(FootprintType.Footprint10x10)]
    [InlineData(FootprintType.Footprint12x10)]
    [InlineData(FootprintType.Footprint12x12)]
    public void FindClosestASTCPartition_WithRandomPartitions_ShouldReturnFewerOrEqualSubsets(FootprintType footprintType)
    {
        var footprint = Footprint.FromFootprintType(footprintType);
        var random = new Random(unchecked((int)0xdeadbeef));

        const int numTests = 15; // Tests per footprint type
        for (int i = 0; i < numTests; i++)
        {
            // Create random partition
            int numParts = 2 + random.Next(3); // 2, 3, or 4 parts
            var assignment = new int[footprint.PixelCount];
            for (int j = 0; j < footprint.PixelCount; j++)
            {
                assignment[j] = random.Next(numParts);
            }
            var partition = new Partition(footprint, numParts)
            {
                assignment = assignment
            };

            var astcPartition = Partition.FindClosestASTCPartition(partition);

            // Matched partition should have fewer or equal subsets
            astcPartition.numParts
                .Should()
                .BeLessThanOrEqualTo(
                    partition.numParts,
                    $"Footprint {footprintType}, Test #{i}: Selected partition with ID {astcPartition.partitionId?.ToString() ?? "null"}");
        }
    }
}
