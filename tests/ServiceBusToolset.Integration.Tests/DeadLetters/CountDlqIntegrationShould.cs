using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class CountDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task ReturnTotalCount_WhenNoFilterProvided()
    {
        // Arrange
        var queue = GetQueue("count-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 5; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "Order.Failed" },
                                         "MaxRetries");
        }

        await WaitForDlqCountAsync(target, 5, TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act — use a far-future BeforeTime so the AMQP peeking path counts all messages.
        // The emulator's admin API does not track runtime properties (DeadLetterMessageCount),
        // so the "no filter" fast path (which relies on GetQueueRuntimePropertiesAsync) cannot be tested here.
        var beforeTime = DateTimeOffset.UtcNow.AddHours(1);
        var result = await sender.Send(new CountDlqMessagesCommand("ignored-by-emulator",
                                                                   target,
                                                                   beforeTime),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(5);
        result.Value.FilteredCount.ShouldBe(5);
    }

    [Fact]
    public async Task ReturnFilteredCount_WhenBeforeTimeProvided()
    {
        // Arrange
        var queue = GetQueue("count-filtered");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 5; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "Order.Failed" },
                                         "MaxRetries");
        }

        var sender = CreateSender();
        var beforeTime = DateTimeOffset.UtcNow.AddHours(-1);

        // Act — BeforeTime in the past: all messages were enqueued after this time, so none match
        var result = await sender.Send(new CountDlqMessagesCommand("ignored-by-emulator",
                                                                   target,
                                                                   beforeTime),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(5);
        result.Value.FilteredCount.ShouldBe(0);
        result.Value.BeforeTime.ShouldBe(beforeTime);
    }

    [Fact]
    public async Task ReturnTotalCount_WhenTargetIsSubscription()
    {
        // Arrange
        var topic = GetTopic("count-topic");
        var subscription = GetSubscription("count-sub");
        await CreateTopicAsync(topic);
        await CreateSubscriptionAsync(topic, subscription);

        var target = EntityTarget.ForSubscription(topic, subscription);
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "Event.Error" },
                                         "ProcessingFailed");
        }

        await WaitForDlqCountAsync(target, 3, TestContext.Current.CancellationToken);
        var sender = CreateSender();

        // Act — use a far-future BeforeTime to exercise the AMQP peeking path
        // (emulator admin API does not track runtime properties)
        var beforeTime = DateTimeOffset.UtcNow.AddHours(1);
        var result = await sender.Send(new CountDlqMessagesCommand("ignored-by-emulator",
                                                                   target,
                                                                   beforeTime),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(3);
        result.Value.FilteredCount.ShouldBe(3);
    }
}
