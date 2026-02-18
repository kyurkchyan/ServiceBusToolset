using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.Integration.Tests.Infrastructure;
using ServiceBusToolset.IntegrationTesting;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.CLI.Integration.Tests.DeadLetters;

public class MergeSimilarPurgeIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task PurgeSelectedMergedCategory_WhenSingleCategoryChosen()
    {
        // Arrange
        var queue = GetQueue("merge-purge-single");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

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

        var mockOutput = Substitute.For<IConsoleOutput>();
        // "1" selects the first merged category (sorted by count desc -> 5-message "Timeout" group)
        mockOutput.ReadLine().Returns("1");

        var sender = CreateSender();
        var handler = new PurgeDlqCommandHandler(sender, mockOutput);

        var command = new PurgeDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = queue,
            Interactive = true,
            MergeSimilar = true
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        mockOutput.Received().Success(Arg.Is<string>(s => s.Contains("5")));

        // Verify remaining DLQ messages are only from the "Error processing" group
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessagesAsync(10, cancellationToken:TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(3);
        remaining.ShouldAllBe(m => m.Subject!.Contains("Error processing"));
    }

    [Fact]
    public async Task PurgeAllMessages_WhenAllMergedCategoriesSelected()
    {
        // Arrange
        var queue = GetQueue("merge-purge-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

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

        var mockOutput = Substitute.For<IConsoleOutput>();
        mockOutput.ReadLine().Returns("all");

        var sender = CreateSender();
        var handler = new PurgeDlqCommandHandler(sender, mockOutput);

        var command = new PurgeDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = queue,
            Interactive = true,
            MergeSimilar = true
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        mockOutput.Received().Success(Arg.Is<string>(s => s.Contains("8")));

        // Verify DLQ is empty
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
                                                         new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessageAsync(cancellationToken:TestContext.Current.CancellationToken);
        remaining.ShouldBeNull();
    }
}
