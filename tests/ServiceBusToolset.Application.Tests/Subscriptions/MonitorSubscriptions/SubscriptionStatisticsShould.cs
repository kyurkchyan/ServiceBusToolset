using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions.Models;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.Subscriptions.MonitorSubscriptions;

public class SubscriptionStatisticsShould
{
    [Fact]
    public void HasSameCountsAs_ReturnTrue_WhenAllCountsMatch()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeTrue();
    }

    [Fact]
    public void HasSameCountsAs_ReturnFalse_WhenTopicNameDiffers()
    {
        // Arrange
        var stats1 = CreateStats("topic1",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic2",
                                 "sub",
                                 10,
                                 5,
                                 2);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeFalse();
    }

    [Fact]
    public void HasSameCountsAs_ReturnFalse_WhenSubscriptionNameDiffers()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub1",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub2",
                                 10,
                                 5,
                                 2);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeFalse();
    }

    [Fact]
    public void HasSameCountsAs_ReturnFalse_WhenActiveMessageCountDiffers()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 20,
                                 5,
                                 2);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeFalse();
    }

    [Fact]
    public void HasSameCountsAs_ReturnFalse_WhenDeadLetterCountDiffers()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 10,
                                 10,
                                 2);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeFalse();
    }

    [Fact]
    public void HasSameCountsAs_ReturnFalse_WhenScheduledCountDiffers()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 5);

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeFalse();
    }

    [Fact]
    public void HasSameCountsAs_IgnoreUpdatedAtDifference()
    {
        // Arrange
        var stats1 = new SubscriptionStatistics("topic",
                                                "sub",
                                                10,
                                                5,
                                                2,
                                                DateTimeOffset.UtcNow);
        var stats2 = new SubscriptionStatistics("topic",
                                                "sub",
                                                10,
                                                5,
                                                2,
                                                DateTimeOffset.UtcNow.AddHours(1));

        // Act & Assert
        stats1.HasSameCountsAs(stats2).ShouldBeTrue();
    }

    [Fact]
    public void AddToHashCode_ProduceSameHash_WhenCountsAreSame()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);

        var hash1 = new HashCode();
        var hash2 = new HashCode();

        // Act
        stats1.AddToHashCode(ref hash1);
        stats2.AddToHashCode(ref hash2);

        // Assert
        hash1.ToHashCode().ShouldBe(hash2.ToHashCode());
    }

    [Fact]
    public void AddToHashCode_ProduceDifferentHash_WhenCountsDiffer()
    {
        // Arrange
        var stats1 = CreateStats("topic",
                                 "sub",
                                 10,
                                 5,
                                 2);
        var stats2 = CreateStats("topic",
                                 "sub",
                                 20,
                                 5,
                                 2);

        var hash1 = new HashCode();
        var hash2 = new HashCode();

        // Act
        stats1.AddToHashCode(ref hash1);
        stats2.AddToHashCode(ref hash2);

        // Assert
        hash1.ToHashCode().ShouldNotBe(hash2.ToHashCode());
    }

    private static SubscriptionStatistics CreateStats(
        string topicName,
        string subscriptionName,
        long active,
        long dlq,
        long scheduled)
        => new(topicName,
               subscriptionName,
               active,
               dlq,
               scheduled,
               DateTimeOffset.UtcNow);
}
