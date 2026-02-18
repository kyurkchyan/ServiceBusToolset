using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class DumpFromCacheIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task WriteSelectedMessagesToFile_WhenCategoriesSelected()
    {
        // Arrange
        var queue = GetQueue("dump-cache-sel");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"order-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("payment-0") { Subject = "PaymentError" },
                                     "Expired");

        await WaitForDlqCountAsync(target, 3, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Stream and populate cache
        var streamResult = await sender.Send(new StreamDlqCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Select only "OrderFailed" category
        var selectedKeys = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderFailed", "MaxRetries") };
        var messagesToDump = session.SnapshotForCategories(selectedKeys);

        var outputPath = TempFilePath();

        // Act
        var dumpResult = await sender.Send(new DumpFromCacheCommand(messagesToDump, outputPath),
                                           TestContext.Current.CancellationToken);

        // Assert
        dumpResult.IsSuccess.ShouldBeTrue();
        dumpResult.Value.MessageCount.ShouldBe(2);
        File.Exists(outputPath).ShouldBeTrue();

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        content.ShouldContain("OrderFailed");
        content.ShouldNotContain("PaymentError");
    }

    [Fact]
    public async Task WriteAllCachedMessagesToFile_WhenAllSelected()
    {
        // Arrange
        var queue = GetQueue("dump-cache-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("order-0") { Subject = "OrderFailed" },
                                     "MaxRetries");

        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("payment-0") { Subject = "PaymentError" },
                                     "Expired");

        await WaitForDlqCountAsync(target, 2, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Stream and populate cache
        var streamResult = await sender.Send(new StreamDlqCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Select all categories
        var selectedKeys = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("OrderFailed", "MaxRetries"),
            DlqCategoryKey.FromMessage("PaymentError", "Expired")
        };
        var messagesToDump = session.SnapshotForCategories(selectedKeys);

        var outputPath = TempFilePath();

        // Act
        var dumpResult = await sender.Send(new DumpFromCacheCommand(messagesToDump, outputPath),
                                           TestContext.Current.CancellationToken);

        // Assert
        dumpResult.IsSuccess.ShouldBeTrue();
        dumpResult.Value.MessageCount.ShouldBe(2);
        File.Exists(outputPath).ShouldBeTrue();

        var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        content.ShouldContain("OrderFailed");
        content.ShouldContain("PaymentError");
    }
}
