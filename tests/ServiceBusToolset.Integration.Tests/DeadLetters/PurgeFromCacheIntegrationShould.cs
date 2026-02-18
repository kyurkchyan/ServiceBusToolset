using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class PurgeFromCacheIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task PurgeSelectedMessages_WhenCategoriesSelected()
    {
        // Arrange
        var queue = GetQueue("cache-purge-sel");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

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
        var streamResult = await sender.Send(new StreamDlqForPurgeCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Select only OrderFailed category
        var selectedKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToPurge = session.SnapshotForCategories(selectedKeys);
        messagesToPurge.Count.ShouldBe(2);

        // Act
        var result = await sender.Send(new PurgeFromCacheCommand("ignored-by-emulator",
                                                                  target,
                                                                  messagesToPurge),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(2);

        // Verify only PaymentError messages remain in DLQ
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessagesAsync(10, cancellationToken:TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(2);
        remaining.ShouldAllBe(m => m.Subject == "PaymentError");
    }

    [Fact]
    public async Task PurgeAllCachedMessages_WhenAllSelected()
    {
        // Arrange
        var queue = GetQueue("cache-purge-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        await WaitForDlqCountAsync(target, 3, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Stream to build cache
        var streamResult = await sender.Send(new StreamDlqForPurgeCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        var allKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToPurge = session.SnapshotForCategories(allKeys);
        messagesToPurge.Count.ShouldBe(3);

        // Act
        var result = await sender.Send(new PurgeFromCacheCommand("ignored-by-emulator",
                                                                  target,
                                                                  messagesToPurge),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(3);

        // Verify DLQ is empty
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessageAsync(cancellationToken:TestContext.Current.CancellationToken);
        remaining.ShouldBeNull();
    }

    private static async Task WaitForSessionComplete(DlqScanSession session, int timeoutMs = 15000)
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
