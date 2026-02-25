using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class ResubmitDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task ResubmitAllMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var sourceQueue = GetQueue("resub-src");
        var targetQueue = GetQueue("resub-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "Order.Failed" },
                                         "MaxRetries");
        }

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new ResubmitDlqMessagesCommand("ignored-by-emulator",
                                                                      target,
                                                                      targetQueue),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(3);
        result.Value.SkippedCount.ShouldBe(0);

        // Verify messages landed in the target queue
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(10,
                                                           TimeSpan.FromSeconds(5),
                                                           TestContext.Current.CancellationToken);
        received.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ResubmitOnlyMatchingMessages_WhenCategoryFilterProvided()
    {
        // Arrange
        var sourceQueue = GetQueue("resub-flt-src");
        var targetQueue = GetQueue("resub-flt-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);

        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"order-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"payment-{i}") { Subject = "PaymentError" },
                                         "Expired");
        }

        var categoryFilter = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new ResubmitDlqMessagesCommand("ignored-by-emulator",
                                                                      target,
                                                                      targetQueue,
                                                                      CategoryFilter: categoryFilter),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(2);
        result.Value.SkippedCount.ShouldBe(2);

        // Verify only matching messages in target queue
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(10,
                                                           TimeSpan.FromSeconds(5),
                                                           TestContext.Current.CancellationToken);
        received.Count.ShouldBe(2);
        received.ShouldAllBe(m => m.Subject == "OrderFailed");
    }

    [Fact]
    public async Task PreserveMessageProperties_WhenResubmitting()
    {
        // Arrange
        var sourceQueue = GetQueue("resub-props-src");
        var targetQueue = GetQueue("resub-props-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        var original = new ServiceBusMessage("test-body")
        {
            Subject = "Order.Failed",
            ContentType = "application/json",
            CorrelationId = "corr-123"
        };
        original.ApplicationProperties["customProp"] = "customValue";

        await DeadLetterMessageAsync(target, original, "MaxRetries");

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new ResubmitDlqMessagesCommand("ignored-by-emulator",
                                                                      target,
                                                                      targetQueue),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(1);

        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5),
                                                          TestContext.Current.CancellationToken);
        received.ShouldNotBeNull();
        received.Subject.ShouldBe("Order.Failed");
        received.ContentType.ShouldBe("application/json");
        received.CorrelationId.ShouldBe("corr-123");
        received.Body.ToString().ShouldBe("test-body");
        received.ApplicationProperties["customProp"].ShouldBe("customValue");
    }

    [Fact]
    public async Task ReturnZeroResubmitted_WhenDlqIsEmpty()
    {
        // Arrange
        var sourceQueue = GetQueue("resub-empty-src");
        var targetQueue = GetQueue("resub-empty-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new ResubmitDlqMessagesCommand("ignored-by-emulator",
                                                                      target,
                                                                      targetQueue),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }
}
