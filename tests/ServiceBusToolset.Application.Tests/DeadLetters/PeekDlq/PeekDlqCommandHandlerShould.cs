using ServiceBusToolset.Application.DeadLetters.PeekDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.PeekDlq;

public class PeekDlqCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly PeekDlqCommandHandler _handler;

    public PeekDlqCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new PeekDlqCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoMessages()
    {
        _mockFactory.WithNoMessages();

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.ShouldBeEmpty();
        result.Value.TotalPeeked.ShouldBe(0);
        result.Value.SkippedNoOperationId.ShouldBe(0);
    }

    [Fact]
    public async Task ExtractOperationIdFromDiagnosticId()
    {
        var traceId = "abc123def456abc123def456abc12345";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-1")
                .WithSubject("OrderCreated")
                .WithDeadLetterReason("MaxDeliveryCountExceeded")
                .WithDiagnosticId(traceId)
                .WithSequenceNumber(1)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.Messages[0].OperationId.ShouldBe(traceId);
        result.Value.Messages[0].MessageId.ShouldBe("msg-1");
        result.Value.Messages[0].Subject.ShouldBe("OrderCreated");
        result.Value.Messages[0].DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
    }

    [Fact]
    public async Task ExtractOperationIdFromTraceparent()
    {
        var traceId = "traceparent123456traceparent1234";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-1")
                .WithTraceparent(traceId)
                .WithSequenceNumber(1)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.Messages[0].OperationId.ShouldBe(traceId);
    }

    [Fact]
    public async Task SkipMessages_WhenNoOperationId()
    {
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-no-opid")
                .WithSequenceNumber(1)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.ShouldBeEmpty();
        result.Value.TotalPeeked.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(1);
    }

    [Fact]
    public async Task DeduplicateOperationIds()
    {
        var sameTraceId = "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-1")
                .WithDiagnosticId(sameTraceId)
                .WithSequenceNumber(1)
                .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-2")
                .WithDiagnosticId(sameTraceId)
                .WithSequenceNumber(2)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.TotalPeeked.ShouldBe(2);
    }

    [Fact]
    public async Task ApplyTimeFilter_WhenBeforeTimeProvided()
    {
        var cutoffTime = DateTimeOffset.UtcNow;
        var oldTraceId = "0123456789abcdef0123456789abcdef";

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("old-msg")
                .WithDiagnosticId(oldTraceId)
                .WithEnqueuedTime(cutoffTime.AddHours(-2))
                .WithSequenceNumber(1)
                .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("new-msg")
                .WithDiagnosticId("fedcba9876543210fedcba9876543210")
                .WithEnqueuedTime(cutoffTime.AddHours(1))
                .WithSequenceNumber(2)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand(beforeTime: cutoffTime);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
        result.Value.Messages[0].OperationId.ShouldBe(oldTraceId);
    }

    [Fact]
    public async Task PreserveEnqueuedTime()
    {
        var traceId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        var enqueuedTime = new DateTimeOffset(2026, 3, 20, 8, 0, 0, TimeSpan.Zero);

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                .WithMessageId("msg-1")
                .WithDiagnosticId(traceId)
                .WithEnqueuedTime(enqueuedTime)
                .WithSequenceNumber(1)
                .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages[0].EnqueuedTime.ShouldBe(enqueuedTime);
    }

    private static PeekDlqCommand CreateCommand(DateTimeOffset? beforeTime = null) =>
        new("test.servicebus.windows.net",
            EntityTargetBuilder.Queue(),
            BeforeTime: beforeTime);
}
