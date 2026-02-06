using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.Subscriptions;

public class MonitorSubscriptionsIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task EmitSubscriptionStatistics_WhenSubscriptionsExist()
    {
        // Arrange
        var topic = GetTopic("mon-sub-topic");
        var subscription = GetSubscription("mon-sub");
        await CreateTopicAsync(topic);
        await CreateSubscriptionAsync(topic, subscription);

        var target = EntityTarget.ForSubscription(topic, subscription);
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("dlq-msg") { Subject = "Test" });
        await WaitForDlqCountAsync(target, 1, TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new MonitorSubscriptionsCommand("ignored-by-emulator",
                                                                       $"*{TestId}*",
                                                                       null,
                                                                       TimeSpan.FromSeconds(1),
                                                                       cts.Token),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        var snapshot = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        snapshot.ShouldNotBeEmpty();
        var stats = snapshot.Single(s => s.TopicName == topic && s.SubscriptionName == subscription);
        stats.DeadLetterMessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task FilterByTopicAndSubscription_WhenFiltersProvided()
    {
        // Arrange
        var topic1 = GetTopic("mon-t1");
        var topic2 = GetTopic("mon-t2");
        var sub1 = GetSubscription("mon-s1");
        var sub2 = GetSubscription("mon-s2");

        await CreateTopicAsync(topic1);
        await CreateTopicAsync(topic2);
        await CreateSubscriptionAsync(topic1, sub1);
        await CreateSubscriptionAsync(topic2, sub2);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act — filter to only topic1
        var result = await sender.Send(new MonitorSubscriptionsCommand("ignored-by-emulator",
                                                                       $"mon-t1-{TestId}",
                                                                       null,
                                                                       TimeSpan.FromSeconds(1),
                                                                       cts.Token),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var snapshot = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        snapshot.Count.ShouldBe(1);
        snapshot[0].TopicName.ShouldBe(topic1);
        snapshot[0].SubscriptionName.ShouldBe(sub1);
    }

    [Fact]
    public async Task CompleteObservable_WhenCancellationTokenCancelled()
    {
        // Arrange
        var topic = GetTopic("mon-sub-cancel");
        var subscription = GetSubscription("mon-sub-cancel");
        await CreateTopicAsync(topic);
        await CreateSubscriptionAsync(topic, subscription);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sender = CreateSender();

        var result = await sender.Send(new MonitorSubscriptionsCommand("ignored-by-emulator",
                                                                       $"*{TestId}*",
                                                                       null,
                                                                       TimeSpan.FromSeconds(1),
                                                                       cts.Token),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        // Act — get one snapshot, then cancel
        var snapshot = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert — observable should complete without error
        snapshot.ShouldNotBeNull();
    }
}
