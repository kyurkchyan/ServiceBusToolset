using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.PeekDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class PeekDlqBatchIntegrationShould : BaseIntegrationTest
{
    public PeekDlqBatchIntegrationShould(ServiceBusEmulatorFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task ReturnPeekedMessages_WithExtractedOperationIds()
    {
        var queue = GetQueue("peek-batch-with-id");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var msg = new ServiceBusMessage("test-body")
        {
            Subject = "Order.Failed",
            ApplicationProperties = { ["Diagnostic-Id"] = "00-abc123def456-0123456789ab-01" }
        };

        await DeadLetterMessageAsync(target, msg, "ProcessingFailed");

        var sender = CreateSender();

        var result = await sender.Send(new PeekDlqBatchCommand("ignored-by-emulator", target, 100),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.PeekedInBatch.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(0);
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.Messages[0].OperationId.ShouldBe("abc123def456");
        result.Value.Messages[0].Subject.ShouldBe("Order.Failed");
        result.Value.HasMoreMessages.ShouldBeFalse();
        result.Value.LastSequenceNumber.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenDlqIsEmpty()
    {
        var queue = GetQueue("peek-batch-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        var result = await sender.Send(new PeekDlqBatchCommand("ignored-by-emulator", target, 100),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.PeekedInBatch.ShouldBe(0);
        result.Value.Messages.ShouldBeEmpty();
        result.Value.HasMoreMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task PaginateThroughMessages_UsingSequenceNumber()
    {
        var queue = GetQueue("peek-batch-paginate");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // Send 3 messages to DLQ
        for (var i = 0; i < 3; i++)
        {
            var msg = new ServiceBusMessage($"body-{i}")
            {
                Subject = $"Event.{i}",
                ApplicationProperties = { ["Diagnostic-Id"] = $"00-trace{i:D32}-span-01" }
            };
            await DeadLetterMessageAsync(target, msg, $"Reason{i}");
        }

        var sender = CreateSender();

        // First batch: get 2 messages
        var result1 = await sender.Send(new PeekDlqBatchCommand("ignored-by-emulator", target, 2),
                                        TestContext.Current.CancellationToken);

        result1.IsSuccess.ShouldBeTrue();
        result1.Value.PeekedInBatch.ShouldBe(2);
        result1.Value.HasMoreMessages.ShouldBeTrue();
        result1.Value.LastSequenceNumber.ShouldNotBeNull();

        // Second batch: resume from last sequence number
        var result2 = await sender.Send(new PeekDlqBatchCommand("ignored-by-emulator",
                                                                target,
                                                                2,
                                                                result1.Value.LastSequenceNumber),
                                        TestContext.Current.CancellationToken);

        result2.IsSuccess.ShouldBeTrue();
        result2.Value.PeekedInBatch.ShouldBe(1);
        result2.Value.HasMoreMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task ReturnTotalDeadLetterCount_WhenDlqHasMessages()
    {
        var queue = GetQueue("peek-batch-count");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        for (var i = 0; i < 3; i++)
        {
            var msg = new ServiceBusMessage($"body-{i}") { ApplicationProperties = { ["Diagnostic-Id"] = $"00-count{i:D32}-span-01" } };
            await DeadLetterMessageAsync(target, msg);
        }

        await WaitForDlqCountAsync(target, 3);

        var sender = CreateSender();

        var result = await sender.Send(new PeekDlqBatchCommand("ignored-by-emulator", target, 100),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalDeadLetterCount.ShouldBe(3);
    }
}
