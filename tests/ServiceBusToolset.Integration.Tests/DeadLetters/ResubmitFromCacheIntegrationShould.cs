using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class ResubmitFromCacheIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task ResubmitAllMessagesFromSnapshot_WhenAllCategoriesSelected()
    {
        // Arrange
        var sourceQueue = GetQueue("cache-resub-all-src");
        var targetQueue = GetQueue("cache-resub-all-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        await WaitForDlqCountAsync(target, 3, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // First, stream to build the cache
        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        var allKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToResubmit = session.SnapshotForCategories(allKeys);
        messagesToResubmit.Count.ShouldBe(3);

        // Act
        var result = await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                                                                    target,
                                                                    targetQueue,
                                                                    messagesToResubmit,
                                                                    session.ResubmitTracker),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(3);

        // Verify messages landed in the target queue
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(10,
                                                           TimeSpan.FromSeconds(5),
                                                           TestContext.Current.CancellationToken);
        received.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ResubmitOnlySelectedCategories_WhenSubsetSelected()
    {
        // Arrange
        var sourceQueue = GetQueue("cache-resub-flt-src");
        var targetQueue = GetQueue("cache-resub-flt-tgt");
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

        await WaitForDlqCountAsync(target, 4, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Stream to build cache
        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Select only OrderFailed category
        var selectedKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToResubmit = session.SnapshotForCategories(selectedKeys);
        messagesToResubmit.Count.ShouldBe(2);

        // Act
        var result = await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                                                                    target,
                                                                    targetQueue,
                                                                    messagesToResubmit,
                                                                    session.ResubmitTracker),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(2);

        // Verify only matching messages in target
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(10,
                                                           TimeSpan.FromSeconds(5),
                                                           TestContext.Current.CancellationToken);
        received.Count.ShouldBe(2);
        received.ShouldAllBe(m => m.Subject == "OrderFailed");
    }

    [Fact]
    public async Task TrackResubmittedMessageIds_WhenResubmitCompletes()
    {
        // Arrange
        var sourceQueue = GetQueue("cache-resub-track-src");
        var targetQueue = GetQueue("cache-resub-track-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("msg-0") { Subject = "OrderFailed" },
                                     "MaxRetries");

        await WaitForDlqCountAsync(target, 1, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        var allKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToResubmit = session.SnapshotForCategories(allKeys);
        var resubmittedMessageId = messagesToResubmit[0].MessageId;

        // Act
        await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                                                       target,
                                                       targetQueue,
                                                       messagesToResubmit,
                                                       session.ResubmitTracker),
                          TestContext.Current.CancellationToken);

        // Assert
        session.ResubmitTracker.WasResubmitted(resubmittedMessageId).ShouldBeTrue();
    }

    [Fact]
    public async Task PreserveMessageProperties_WhenResubmittingFromCache()
    {
        // Arrange
        var sourceQueue = GetQueue("cache-resub-props-src");
        var targetQueue = GetQueue("cache-resub-props-tgt");
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
        await WaitForDlqCountAsync(target, 1, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        var allKeys = new HashSet<DlqCategoryKey> { new("Order.Failed", "MaxRetries") };
        var messagesToResubmit = session.SnapshotForCategories(allKeys);

        // Act
        var result = await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                                                                    target,
                                                                    targetQueue,
                                                                    messagesToResubmit,
                                                                    session.ResubmitTracker),
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
    public async Task ReturnZeroCounts_WhenEmptySnapshotProvided()
    {
        // Arrange
        var sourceQueue = GetQueue("cache-resub-empty-src");
        var targetQueue = GetQueue("cache-resub-empty-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);
        var tracker = new ResubmitTracker();
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                                                                    target,
                                                                    targetQueue,
                                                                    [],
                                                                    tracker),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }

    private static async Task WaitForSessionComplete(DlqResubmitSession session, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!session.Cache.IsComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(100);
        }

        if (!session.Cache.IsComplete)
        {
            throw new TimeoutException("Session cache did not complete within timeout.");
        }
    }
}
