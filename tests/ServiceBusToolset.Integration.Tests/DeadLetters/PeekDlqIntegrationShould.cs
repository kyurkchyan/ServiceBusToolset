using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.PeekDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class PeekDlqIntegrationShould : BaseIntegrationTest
{
    public PeekDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task ReturnPeekedMessages_WithExtractedOperationIds()
    {
        var queue = GetQueue("peek-with-id");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var msg = new ServiceBusMessage("test-body")
        {
            Subject = "Order.Failed",
            ApplicationProperties = { ["Diagnostic-Id"] = "00-abc123def456-0123456789ab-01" }
        };

        await DeadLetterMessageAsync(target, msg, "ProcessingFailed");

        var sender = CreateSender();

        var result = await sender.Send(
            new PeekDlqCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalPeeked.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(0);
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.Messages[0].OperationId.ShouldBe("abc123def456");
        result.Value.Messages[0].Subject.ShouldBe("Order.Failed");
        result.Value.Messages[0].DeadLetterReason.ShouldBe("ProcessingFailed");
    }

    [Fact]
    public async Task SkipMessages_WhenNoOperationId()
    {
        var queue = GetQueue("peek-no-id");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("no-op-id") { Subject = "Event.Error" },
            "NoHandler");

        var sender = CreateSender();

        var result = await sender.Send(
            new PeekDlqCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalPeeked.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(1);
        result.Value.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenDlqIsEmpty()
    {
        var queue = GetQueue("peek-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        var result = await sender.Send(
            new PeekDlqCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalPeeked.ShouldBe(0);
        result.Value.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeduplicateOperationIds_WhenMultipleMessagesShareSameId()
    {
        var queue = GetQueue("peek-dedup");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var diagnosticId = "00-sameid12345678-0123456789ab-01";

        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("body1") { ApplicationProperties = { ["Diagnostic-Id"] = diagnosticId } },
            "Reason1");

        await DeadLetterMessageAsync(target,
            new ServiceBusMessage("body2") { ApplicationProperties = { ["Diagnostic-Id"] = diagnosticId } },
            "Reason2");

        var sender = CreateSender();

        var result = await sender.Send(
            new PeekDlqCommand("ignored-by-emulator", target),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalPeeked.ShouldBe(2);
        result.Value.Messages.Count.ShouldBe(1);
    }
}
