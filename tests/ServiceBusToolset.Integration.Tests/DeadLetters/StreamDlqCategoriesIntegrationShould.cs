using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class StreamDlqCategoriesIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task PopulateCacheWithAllMessages_WhenDlqHasMessages()
    {
        // Arrange
        var queue = GetQueue("stream-all");
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

        // Act
        var result = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        using var session = result.Value;
        await WaitForSessionComplete(session);

        session.Cache.Count.ShouldBe(3);
        session.Cache.IsComplete.ShouldBeTrue();
        session.Error.ShouldBeNull();
    }

    [Fact]
    public async Task GroupMessagesIntoCategories_WhenMultipleCategoriesExist()
    {
        // Arrange
        var queue = GetQueue("stream-cats");
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

        // Act
        var result = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        using var session = result.Value;
        await WaitForSessionComplete(session);

        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(session.Cache);
        snapshot.TotalMessageCount.ShouldBe(3);
        snapshot.Categories.Count.ShouldBe(2);
        snapshot.Categories.ShouldContain(c => c.Label == "OrderFailed" && c.Count == 2);
        snapshot.Categories.ShouldContain(c => c.Label == "PaymentError" && c.Count == 1);
    }

    [Fact]
    public async Task EmitCategorySnapshots_WhenSubscribedToStream()
    {
        // Arrange
        var queue = GetQueue("stream-emit");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("msg-0") { Subject = "OrderFailed" },
                                     "MaxRetries");

        await WaitForDlqCountAsync(target, 1, TestContext.Current.CancellationToken);

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        using var session = result.Value;

        // Get the initial snapshot (StartWith emits empty)
        var firstSnapshot = await session.CategoryStream.FirstAsync();

        // Assert
        firstSnapshot.ShouldNotBeNull();
        firstSnapshot.TotalMessageCount.ShouldBe(0);
        firstSnapshot.Categories.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnEmptyCache_WhenDlqIsEmpty()
    {
        // Arrange
        var queue = GetQueue("stream-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new StreamDlqCategoriesCommand("ignored-by-emulator", target),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();

        using var session = result.Value;
        await WaitForSessionComplete(session);

        session.Cache.Count.ShouldBe(0);
        session.Cache.IsComplete.ShouldBeTrue();
        session.Error.ShouldBeNull();
    }
}
