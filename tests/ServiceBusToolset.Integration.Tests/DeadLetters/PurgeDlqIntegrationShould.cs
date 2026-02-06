using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class PurgeDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task RemoveAllMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var queue = GetQueue("purge-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 5; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"message-{i}") { Subject = "Order.Failed" },
                                         "MaxDeliveryCountExceeded");
        }

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new PurgeDlqMessagesCommand("ignored-by-emulator",
                                                                   target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(5);
        result.Value.SkippedCount.ShouldBe(0);

        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessageAsync(cancellationToken:TestContext.Current.CancellationToken);
        remaining.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveOnlyMatchingMessages_WhenCategoryAndTimeFiltersProvided()
    {
        // Arrange
        var queue = GetQueue("purge-filtered");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // 2 messages with category ("OrderFailed", "MaxRetries")
        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"order-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        // 2 messages with category ("PaymentError", "Expired")
        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"payment-{i}") { Subject = "PaymentError" },
                                         "Expired");
        }

        var categoryFilter = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new PurgeDlqMessagesCommand("ignored-by-emulator",
                                                                   target,
                                                                   DateTimeOffset.UtcNow.AddHours(1),
                                                                   categoryFilter),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(2);
        result.Value.SkippedCount.ShouldBe(2);

        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessagesAsync(10, cancellationToken:TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(2);
        remaining.ShouldAllBe(m => m.Subject == "PaymentError");
    }

    [Fact]
    public async Task ReturnZeroPurged_WhenDlqIsEmpty()
    {
        // Arrange
        var queue = GetQueue("purge-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new PurgeDlqMessagesCommand("ignored-by-emulator",
                                                                   target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }
}
