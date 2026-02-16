using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;
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
    public async Task ResubmitSelectedMergedCategory_WhenSingleCategoryChosen()
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

        var mockOutput = Substitute.For<IConsoleOutput>();
        // "1" selects the first merged category (sorted by count desc → 5-message "Timeout" group)
        mockOutput.ReadLine().Returns("1");

        var sender = CreateSender();
        var handler = new ResubmitDlqCommandHandler(sender, mockOutput);

        var command = new ResubmitDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = sourceQueue,
            Interactive = true,
            MergeSimilar = true,
            TargetQueue = targetQueue
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, verbose: false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        mockOutput.Received().Success(Arg.Is<string>(s => s.Contains("5")));

        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(10,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        received.Count.ShouldBe(5);
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

        var mockOutput = Substitute.For<IConsoleOutput>();
        mockOutput.ReadLine().Returns("all");

        var sender = CreateSender();
        var handler = new ResubmitDlqCommandHandler(sender, mockOutput);

        var command = new ResubmitDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = sourceQueue,
            Interactive = true,
            MergeSimilar = true,
            TargetQueue = targetQueue
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, verbose: false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        mockOutput.Received().Success(Arg.Is<string>(s => s.Contains("8")));

        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(targetQueue);
        var received = await receiver.ReceiveMessagesAsync(20,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        received.Count.ShouldBe(8);
    }
}
