using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Queues.MonitorQueues.Models;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Helpers;

public class StatisticsListComparerShould
{
    private readonly StatisticsListComparer<QueueStatistics> _comparer = new();

    [Fact]
    public void ReturnTrue_WhenBothListsAreNull()
    {
        _comparer.Equals(null, null).ShouldBeTrue();
    }

    [Fact]
    public void ReturnFalse_WhenFirstListIsNull()
    {
        var list = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        0)
        };
        _comparer.Equals(null, list).ShouldBeFalse();
    }

    [Fact]
    public void ReturnFalse_WhenSecondListIsNull()
    {
        var list = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        0)
        };
        _comparer.Equals(list, null).ShouldBeFalse();
    }

    [Fact]
    public void ReturnFalse_WhenListsHaveDifferentCounts()
    {
        var list1 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        0)
        };
        var list2 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        0),
            CreateStats("queue2",
                        20,
                        10,
                        0)
        };

        _comparer.Equals(list1, list2).ShouldBeFalse();
    }

    [Fact]
    public void ReturnTrue_WhenListsAreIdentical()
    {
        var list1 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        2),
            CreateStats("queue2",
                        20,
                        10,
                        1)
        };
        var list2 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        2),
            CreateStats("queue2",
                        20,
                        10,
                        1)
        };

        _comparer.Equals(list1, list2).ShouldBeTrue();
    }

    [Fact]
    public void ReturnTrue_WhenBothListsAreEmpty()
    {
        var list1 = new List<QueueStatistics>();
        var list2 = new List<QueueStatistics>();
        _comparer.Equals(list1, list2).ShouldBeTrue();
    }

    [Fact]
    public void ReturnSameHashCode_WhenListsAreIdentical()
    {
        var list1 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        2)
        };
        var list2 = new List<QueueStatistics>
        {
            CreateStats("queue1",
                        10,
                        5,
                        2)
        };

        _comparer.GetHashCode(list1).ShouldBe(_comparer.GetHashCode(list2));
    }

    private static QueueStatistics CreateStats(string name, long active, long dlq, long scheduled)
        => new(name,
               active,
               dlq,
               scheduled,
               DateTimeOffset.UtcNow);
}
