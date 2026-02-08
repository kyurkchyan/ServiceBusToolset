using System.Reactive.Linq;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NSubstitute;
using ServiceBusToolset.Application.Queues.MonitorQueues;
using ServiceBusToolset.Application.Queues.MonitorQueues.Models;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.Queues.MonitorQueues;

public class MonitorQueuesCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly MonitorQueuesCommandHandler _handler;

    public MonitorQueuesCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new MonitorQueuesCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnObservable_WhenHandlingCommand()
    {
        // Arrange
        SetupQueues();

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               null,
                                               TimeSpan.FromSeconds(1),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _ = result.Value.QueueStatistics.ShouldNotBeNull();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task EmitStatistics_WhenQueuesExist()
    {
        // Arrange
        SetupQueues(CreateQueueRuntimeProperties("queue-1", 10, 5, 2),
                    CreateQueueRuntimeProperties("queue-2", 20, 3));

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               null,
                                               TimeSpan.FromMilliseconds(100),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(2);
        firstEmission.ShouldContain(q => q.Name == "queue-1");
        firstEmission.ShouldContain(q => q.Name == "queue-2");
    }

    [Fact]
    public async Task ApplyWildcardFilter_WhenFilterProvided()
    {
        // Arrange
        SetupQueues(CreateQueueRuntimeProperties("orders-queue", 10, 5),
                    CreateQueueRuntimeProperties("payments-queue", 20, 3),
                    CreateQueueRuntimeProperties("orders-dlq", 5));

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               "orders*",
                                               TimeSpan.FromMilliseconds(100),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(2);
        firstEmission.ShouldAllBe(q => q.Name.StartsWith("orders"));
    }

    [Fact]
    public async Task SortQueuesByName()
    {
        // Arrange
        SetupQueues(CreateQueueRuntimeProperties("zebra-queue", 1),
                    CreateQueueRuntimeProperties("alpha-queue", 2),
                    CreateQueueRuntimeProperties("beta-queue", 3));

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               null,
                                               TimeSpan.FromMilliseconds(100),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission[0].Name.ShouldBe("alpha-queue");
        firstEmission[1].Name.ShouldBe("beta-queue");
        firstEmission[2].Name.ShouldBe("zebra-queue");
    }

    [Fact]
    public async Task ReturnEmptyList_WhenNoQueuesExist()
    {
        // Arrange
        SetupQueues();

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               null,
                                               TimeSpan.FromMilliseconds(100),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.ShouldBeEmpty();
    }

    [Fact]
    public async Task IncludeMessageCounts_InStatistics()
    {
        // Arrange
        SetupQueues(CreateQueueRuntimeProperties("test-queue", 100, 50, 25));

        using var cts = new CancellationTokenSource();

        var command = new MonitorQueuesCommand("test.servicebus.windows.net",
                                               null,
                                               TimeSpan.FromMilliseconds(100),
                                               cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        var queueStats = firstEmission.Single();
        queueStats.ActiveMessageCount.ShouldBe(100);
        queueStats.DeadLetterMessageCount.ShouldBe(50);
        queueStats.ScheduledMessageCount.ShouldBe(25);
    }

    private void SetupQueues(params QueueRuntimeProperties[] queues)
    {
        var asyncPageable = CreateAsyncPageable(queues);
        _mockFactory.AdminClient.GetQueuesRuntimePropertiesAsync(Arg.Any<CancellationToken>())
                    .ReturnsForAnyArgs(asyncPageable);
    }

    private static QueueRuntimeProperties CreateQueueRuntimeProperties(
        string name,
        long activeMessageCount = 0,
        long deadLetterCount = 0,
        long scheduledCount = 0) =>
        ServiceBusModelFactory.QueueRuntimeProperties(name,
                                                      activeMessageCount,
                                                      deadLetterMessageCount: deadLetterCount,
                                                      scheduledMessageCount: scheduledCount);

    private static AsyncPageable<QueueRuntimeProperties> CreateAsyncPageable(QueueRuntimeProperties[] items)
    {
        var page = Page<QueueRuntimeProperties>.FromValues(items, null, Substitute.For<Response>());
        return AsyncPageable<QueueRuntimeProperties>.FromPages([page]);
    }
}
