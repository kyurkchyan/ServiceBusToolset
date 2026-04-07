using ServiceBusToolset.Application.DeadLetters.PeekDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.PeekDlq;

public class PeekDlqBatchCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly PeekDlqBatchCommandHandler _handler;

    public PeekDlqBatchCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new PeekDlqBatchCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoMessages()
    {
        _mockFactory.WithNoMessages();

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.ShouldBeEmpty();
        result.Value.PeekedInBatch.ShouldBe(0);
        result.Value.HasMoreMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractOperationId_WhenDiagnosticIdIsProvided()
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
        result.Value.PeekedInBatch.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(1);
    }

    [Fact]
    public async Task DeduplicateOperationIds_WhenDuplicatesExist()
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
        result.Value.PeekedInBatch.ShouldBe(2);
    }

    [Fact]
    public async Task ReturnLastSequenceNumber_WhenBatchCompleted()
    {
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId("abc123def456abc123def456abc12345")
                                            .WithSequenceNumber(42)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithDiagnosticId("def456abc123def456abc123def45678")
                                            .WithSequenceNumber(99)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.LastSequenceNumber.ShouldBe(99);
    }

    [Fact]
    public async Task PreserveEnqueuedTime_WhenMessagesAreEnqueued()
    {
        var traceId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        var enqueuedTime = new DateTimeOffset(2026,
                                              3,
                                              20,
                                              8,
                                              0,
                                              0,
                                              TimeSpan.Zero);

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

    [Fact]
    public async Task SetHasMoreMessages_WhenBatchIsFull()
    {
        // Create exactly BatchSize messages — handler should report HasMoreMessages=true
        var batchSize = 5;
        var messages = Enumerable.Range(1, batchSize)
                                 .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                              .WithMessageId($"msg-{i}")
                                                                              .WithDiagnosticId($"{i:D32}")
                                                                              .WithSequenceNumber(i)
                                                                              .Build())
                                 .ToArray();

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand(batchSize);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.PeekedInBatch.ShouldBe(batchSize);
        result.Value.HasMoreMessages.ShouldBeTrue();
    }

    [Fact]
    public async Task SetHasMoreMessagesFalse_WhenFewerThanBatchSize()
    {
        // Create fewer messages than BatchSize — handler should report HasMoreMessages=false
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId("abc123def456abc123def456abc12345")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.PeekedInBatch.ShouldBe(1);
        result.Value.HasMoreMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task FillBatchAcrossMultipleSubBatches_WhenSubBatchesAreRequired()
    {
        // Create more messages than a single sub-batch (100) but within BatchSize
        // The mock returns messages paginated by maxMessages per call,
        // so the handler must loop to collect them all
        var messageCount = 250;
        var messages = Enumerable.Range(1, messageCount)
                                 .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                              .WithMessageId($"msg-{i}")
                                                                              .WithDiagnosticId($"{i:D32}")
                                                                              .WithSequenceNumber(i)
                                                                              .Build())
                                 .ToArray();

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.PeekedInBatch.ShouldBe(messageCount);
        result.Value.Messages.Count.ShouldBe(messageCount);
        result.Value.HasMoreMessages.ShouldBeFalse();
    }

    [Fact]
    public async Task UseKnownDeadLetterCount_WhenProvided()
    {
        _mockFactory.WithNoMessages();

        var command = new PeekDlqBatchCommand("test.servicebus.windows.net",
                                              EntityTargetBuilder.Queue(),
                                              KnownDeadLetterCount:42);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalDeadLetterCount.ShouldBe(42);
    }

    private static PeekDlqBatchCommand CreateCommand(int batchSize = 500, long? fromSequenceNumber = null) =>
        new("test.servicebus.windows.net",
            EntityTargetBuilder.Queue(),
            batchSize,
            fromSequenceNumber);
}
