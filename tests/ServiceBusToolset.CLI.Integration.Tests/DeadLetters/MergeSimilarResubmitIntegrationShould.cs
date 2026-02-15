using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Integration.Tests.Infrastructure;
using ServiceBusToolset.IntegrationTesting;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.CLI.Integration.Tests.DeadLetters;

public class MergeSimilarResubmitIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task ResubmitAllMergedMessages_WhenSingleMergedCategorySelected()
    {
        // Arrange
        var sourceQueue = GetQueue("merge-single-src");
        var targetQueue = GetQueue("merge-single-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);

        // Group A: 3 messages with similar subjects
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("alice-body") { Subject = "Error processing user Alice" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("bob-body") { Subject = "Error processing user Bob" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("charlie-body") { Subject = "Error processing user Charlie" },
            "MaxRetries");

        // Group B: 5 messages with similar subjects
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("order-body") { Subject = "Timeout for service OrderAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("payment-body") { Subject = "Timeout for service PaymentAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("inventory-body") { Subject = "Timeout for service InventoryAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("shipping-body") { Subject = "Timeout for service ShippingAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("notification-body") { Subject = "Timeout for service NotificationAPI" },
            "MaxRetries");

        await WaitForDlqCountAsync(target, 8, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Act - Stream categories to build cache
        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Build snapshot with merge-similar enabled
        var snapshot = StreamDlqCategoriesCommandHandler.BuildCategorySnapshot(session.Cache, mergeSimilar: true);

        snapshot.TotalMessageCount.ShouldBe(8);
        snapshot.MergeResult.ShouldNotBeNull();
        snapshot.MergeResult.MergedCategories.Count.ShouldBe(2);

        // Find the "Error processing user *" merged category
        var errorCategory = snapshot.MergeResult.MergedCategories
            .FirstOrDefault(c => c.Label.Contains("Error processing"));
        errorCategory.ShouldNotBeNull();
        errorCategory.Count.ShouldBe(3);

        // Select just the error category
        var selectedKeys = new HashSet<DlqCategoryKey>
        {
            new(errorCategory.Label, errorCategory.DeadLetterReason)
        };

        // Expand merged keys to original keys
        var expandedKeys = snapshot.MergeResult.ExpandKeys(selectedKeys);

        // Snapshot messages for expanded keys
        var messagesToResubmit = session.SnapshotForCategories(expandedKeys);
        messagesToResubmit.Count.ShouldBe(3);

        // Resubmit
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
    public async Task ResubmitAllMessages_WhenAllMergedCategoriesSelected()
    {
        // Arrange
        var sourceQueue = GetQueue("merge-all-src");
        var targetQueue = GetQueue("merge-all-tgt");
        await CreateQueueAsync(sourceQueue);
        await CreateQueueAsync(targetQueue);

        var target = EntityTarget.ForQueue(sourceQueue);

        // Group A: 3 messages with similar subjects
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("alice-body") { Subject = "Error processing user Alice" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("bob-body") { Subject = "Error processing user Bob" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("charlie-body") { Subject = "Error processing user Charlie" },
            "MaxRetries");

        // Group B: 5 messages with similar subjects
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("order-body") { Subject = "Timeout for service OrderAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("payment-body") { Subject = "Timeout for service PaymentAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("inventory-body") { Subject = "Timeout for service InventoryAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("shipping-body") { Subject = "Timeout for service ShippingAPI" },
            "MaxRetries");
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("notification-body") { Subject = "Timeout for service NotificationAPI" },
            "MaxRetries");

        await WaitForDlqCountAsync(target, 8, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Act - Stream categories to build cache
        var streamResult = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Build snapshot with merge-similar enabled
        var snapshot = StreamDlqCategoriesCommandHandler.BuildCategorySnapshot(session.Cache, mergeSimilar: true);

        snapshot.TotalMessageCount.ShouldBe(8);
        snapshot.MergeResult.ShouldNotBeNull();
        snapshot.MergeResult.MergedCategories.Count.ShouldBe(2);

        // Select all merged categories
        var selectedKeys = snapshot.MergeResult.MergedCategories
            .Select(c => new DlqCategoryKey(c.Label, c.DeadLetterReason))
            .ToHashSet();

        // Expand merged keys to original keys
        var expandedKeys = snapshot.MergeResult.ExpandKeys(selectedKeys);

        // Snapshot messages for expanded keys
        var messagesToResubmit = session.SnapshotForCategories(expandedKeys);
        messagesToResubmit.Count.ShouldBe(8);

        // Resubmit
        var result = await sender.Send(new ResubmitFromCacheCommand("ignored-by-emulator",
                target,
                targetQueue,
                messagesToResubmit,
                session.ResubmitTracker),
            TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(8);

        // Verify messages landed in the target queue
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(20,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        received.Count.ShouldBe(8);
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
