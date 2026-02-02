using AstcSharp.ColorEncoding;
using AstcSharp.Core;
using FluentAssertions;

namespace AstcSharp.Tests;

public class PartitionTests
{
    #region PartitionMetric Tests

    [Fact]
    public void PartitionMetric_WithSimplePartitions_ShouldCalculateCorrectDistance()
    {
        // Arrange
        var partitionA = new Partition(Footprint.Get6x6(), 2)
        {
            assignment = new List<int>
            {
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,1
            }
        };

        var partitionB = new Partition(Footprint.Get6x6(), 2)
        {
            assignment = new List<int>
            {
                1,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0
            }
        };

        // Act
        int distance = Partition.PartitionMetric(partitionA, partitionB);

        // Assert
        distance.Should().Be(2);
    }

    [Fact]
    public void PartitionMetric_WithDifferentPartCounts_ShouldCalculateCorrectDistance()
    {
        // Arrange
        var partitionA = new Partition(Footprint.Get4x4(), 2)
        {
            assignment = new List<int>
            {
                2,2,2,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,1
            }
        };

        var partitionB = new Partition(Footprint.Get4x4(), 3)
        {
            assignment = new List<int>
            {
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            }
        };

        // Act
        int distance = Partition.PartitionMetric(partitionA, partitionB);

        // Assert
        distance.Should().Be(3);
    }

    [Fact]
    public void PartitionMetric_WithDifferentMapping_ShouldCalculateCorrectDistance()
    {
        // Arrange
        var partitionA = new Partition(Footprint.Get4x4(), 2)
        {
            assignment = new List<int>
            {
                0,1,2,2,
                2,2,2,2,
                2,2,2,2,
                2,2,2,2
            }
        };

        var partitionB = new Partition(Footprint.Get4x4(), 3)
        {
            assignment = new List<int>
            {
                1,0,0,0,
                0,0,0,0,
                0,0,0,0,
                0,0,0,0
            }
        };

        // Act
        int distance = Partition.PartitionMetric(partitionA, partitionB);

        // Assert
        distance.Should().Be(1);
    }

    #endregion

    #region GetASTCPartition Tests

    [Fact]
    public void GetASTCPartition_WithSpecificParameters_ShouldReturnExpectedAssignment()
    {
        // Arrange
        int[] expected = new int[]
        {
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2,
            0,0,0,0,1,1,1,2,2,2
        };

        // Act
        var partition = Partition.GetASTCPartition(Footprint.Get10x6(), 3, 557);

        // Assert
        partition.assignment.Should().Equal(expected);
    }

    [Fact]
    public void GetASTCPartition_WithDifferentIds_ShouldProduceUniqueAssignments()
    {
        // Arrange & Act
        var partition0 = Partition.GetASTCPartition(Footprint.Get6x6(), 2, 0);
        var partition1 = Partition.GetASTCPartition(Footprint.Get6x6(), 2, 1);

        // Assert
        partition0.assignment.Should().NotEqual(partition1.assignment);
    }

    #endregion

    #region FindClosestASTCPartition Tests

    [Fact]
    public void FindClosestASTCPartition_ShouldPreservePartitionCount()
    {
        // Arrange
        var partition = new Partition(Footprint.Get6x6(), 2)
        {
            assignment = new List<int>
            {
                0,0,1,1,1,0,
                0,0,0,0,0,0,
                0,0,0,0,0,0,
                0,1,1,1,1,1,
                0,0,0,0,0,0,
                1,1,1,1,1,1
            }
        };

        // Act
        var closestAstcPartition = Partition.FindClosestASTCPartition(partition);

        // Assert
        closestAstcPartition.numParts.Should().Be(partition.numParts);
    }

    #endregion
}
