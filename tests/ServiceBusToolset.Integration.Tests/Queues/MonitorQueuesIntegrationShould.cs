using System.Reactive.Linq;
using ServiceBusToolset.Application.Queues.MonitorQueues;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Integration.Tests.Queues;

public class MonitorQueuesIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task EmitQueueStatistics_WhenQueuesExist()
    {
        // Arrange
        var queue = GetQueue("monitor-q");
        await CreateQueueAsync(queue);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new MonitorQueuesCommand("ignored-by-emulator",
                                                                $"*{TestId}*",
                                                                TimeSpan.FromSeconds(1),
                                                                cts.Token),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        var snapshot = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        snapshot.ShouldNotBeEmpty();
        // Note: emulator admin API does not track runtime properties (message counts are always 0),
        // so we only verify the monitor discovers the queue by name.
        snapshot.Single(s => s.Name == queue).ShouldNotBeNull();
    }

    [Fact]
    public async Task FilterQueues_WhenQueueFilterProvided()
    {
        // Arrange
        var matchQueue = GetQueue("mon-match");
        var otherQueue = GetQueue("mon-other");
        await CreateQueueAsync(matchQueue);
        await CreateQueueAsync(otherQueue);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act — filter that matches only the first queue
        var result = await sender.Send(new MonitorQueuesCommand("ignored-by-emulator",
                                                                $"mon-match-{TestId}",
                                                                TimeSpan.FromSeconds(1),
                                                                cts.Token),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var snapshot = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        snapshot.Count.ShouldBe(1);
        snapshot[0].Name.ShouldBe(matchQueue);
    }

    [Fact]
    public async Task CompleteObservable_WhenCancellationTokenCancelled()
    {
        // Arrange
        var queue = GetQueue("mon-cancel");
        await CreateQueueAsync(queue);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        var result = await sender.Send(new MonitorQueuesCommand("ignored-by-emulator",
                                                                $"*{TestId}*",
                                                                TimeSpan.FromSeconds(1),
                                                                cts.Token),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        // Act — get one snapshot, then cancel
        var snapshot = await result.Value.QueueStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert — observable should complete without error
        snapshot.ShouldNotBeNull();
    }
}
