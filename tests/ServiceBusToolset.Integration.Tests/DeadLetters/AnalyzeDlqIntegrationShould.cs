using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class AnalyzeDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task GroupMessagesByCategory_WhenQueueHasMultipleCategories()
    {
        // Arrange
        var queue = GetQueue("analyze-cats");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // 3 messages in category ("OrderFailed", "MaxRetries")
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"order-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        // 2 messages in category ("PaymentError", "Expired")
        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"payment-{i}") { Subject = "PaymentError" },
                                         "Expired");
        }

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new AnalyzeDlqCategoriesCommand("ignored-by-emulator",
                                                                       target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalMessageCount.ShouldBe(5);
        result.Value.Categories.Count.ShouldBe(2);

        // Categories are sorted descending by count
        result.Value.Categories[0].Label.ShouldBe("OrderFailed");
        result.Value.Categories[0].DeadLetterReason.ShouldBe("MaxRetries");
        result.Value.Categories[0].Count.ShouldBe(3);

        result.Value.Categories[1].Label.ShouldBe("PaymentError");
        result.Value.Categories[1].DeadLetterReason.ShouldBe("Expired");
        result.Value.Categories[1].Count.ShouldBe(2);
    }

    [Fact]
    public async Task ReturnEmptyCategories_WhenDlqIsEmpty()
    {
        // Arrange
        var queue = GetQueue("analyze-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new AnalyzeDlqCategoriesCommand("ignored-by-emulator",
                                                                       target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalMessageCount.ShouldBe(0);
        result.Value.Categories.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnalyzeCategories_WhenTargetIsSubscription()
    {
        // Arrange
        var topic = GetTopic("analyze-topic");
        var subscription = GetSubscription("analyze-sub");
        await CreateTopicAsync(topic);
        await CreateSubscriptionAsync(topic, subscription);

        var target = EntityTarget.ForSubscription(topic, subscription);

        for (var i = 0; i < 4; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"event-{i}") { Subject = "Event.Error" },
                                         "ProcessingFailed");
        }

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new AnalyzeDlqCategoriesCommand("ignored-by-emulator",
                                                                       target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalMessageCount.ShouldBe(4);
        result.Value.Categories.Count.ShouldBe(1);
        result.Value.Categories[0].Label.ShouldBe("Event.Error");
        result.Value.Categories[0].Count.ShouldBe(4);
    }
}
