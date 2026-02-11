using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.ResubmitDlq;

public class ResubmitFromCacheCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly ResubmitFromCacheCommandHandler _handler;
    private readonly ResubmitTracker _tracker;

    public ResubmitFromCacheCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create().WithSender();
        _handler = new ResubmitFromCacheCommandHandler(_mockFactory.Object);
        _tracker = new ResubmitTracker();
    }

    [Fact]
    public async Task ResubmitMatchingMessages_WhenSnapshotProvided()
    {
        // Arrange
        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-1")
                                                   .WithSequenceNumber(1)
                                                   .Build();

        var msg2 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-2")
                                                   .WithSequenceNumber(2)
                                                   .Build();

        _mockFactory.WithMessagesToReturn(msg1, msg2);

        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [msg1, msg2],
                                                   _tracker);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(2);
    }

    [Fact]
    public async Task AbandonNonMatchingMessages_WhenNotInSnapshot()
    {
        // Arrange
        var matchingMsg = ServiceBusReceivedMessageBuilder.Create()
                                                          .WithMessageId("msg-1")
                                                          .WithSequenceNumber(1)
                                                          .Build();

        var nonMatchingMsg = ServiceBusReceivedMessageBuilder.Create()
                                                             .WithMessageId("msg-2")
                                                             .WithSequenceNumber(2)
                                                             .Build();

        _mockFactory.WithMessagesToReturn(matchingMsg, nonMatchingMsg);

        // Only include msg-1 in snapshot
        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [matchingMsg],
                                                   _tracker);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(1);
        await _mockFactory.Receiver.Received().AbandonMessageAsync(Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "msg-2"),
                                                                   Arg.Any<IDictionary<string, object>>(),
                                                                   Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackResubmittedMessageIds_WhenResubmitSucceeds()
    {
        // Arrange
        var msg = ServiceBusReceivedMessageBuilder.Create()
                                                  .WithMessageId("msg-1")
                                                  .WithSequenceNumber(1)
                                                  .Build();

        _mockFactory.WithMessagesToReturn(msg);

        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [msg],
                                                   _tracker);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _tracker.WasResubmitted("msg-1").ShouldBeTrue();
    }

    [Fact]
    public async Task ReturnZeroCounts_WhenEmptySnapshot()
    {
        // Arrange
        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [],
                                                   _tracker);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task PreserveMessageProperties_WhenResubmitting()
    {
        // Arrange
        ServiceBusMessage? capturedMessage = null;
        _mockFactory.Sender.SendMessagesAsync(Arg.Any<IEnumerable<ServiceBusMessage>>(),
                                              Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask)
                    .AndDoes(callInfo =>
                    {
                        var msgs = callInfo.ArgAt<IEnumerable<ServiceBusMessage>>(0);
                        capturedMessage = msgs.FirstOrDefault();
                    });

        var originalMessage = ServiceBusReceivedMessageBuilder.Create()
                                                              .WithMessageId("msg-123")
                                                              .WithCorrelationId("corr-456")
                                                              .WithSubject("TestSubject")
                                                              .WithContentType("application/json")
                                                              .WithBody("{\"key\": \"value\"}")
                                                              .WithTo("destination")
                                                              .WithReplyTo("reply-address")
                                                              .WithApplicationProperty("custom-prop", "custom-value")
                                                              .WithSequenceNumber(1)
                                                              .Build();

        _mockFactory.WithMessagesToReturn(originalMessage);

        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [originalMessage],
                                                   _tracker);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedMessage.ShouldNotBeNull();
        capturedMessage!.MessageId.ShouldBe("msg-123");
        capturedMessage.CorrelationId.ShouldBe("corr-456");
        capturedMessage.Subject.ShouldBe("TestSubject");
        capturedMessage.ContentType.ShouldBe("application/json");
        capturedMessage.To.ShouldBe("destination");
        capturedMessage.ReplyTo.ShouldBe("reply-address");
        capturedMessage.ApplicationProperties.ShouldContainKey("custom-prop");
        capturedMessage.ApplicationProperties["custom-prop"].ShouldBe("custom-value");
    }

    [Fact]
    public async Task DisposeClient_WhenHandlingCompletes()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [ServiceBusReceivedMessageBuilder.Create().WithSequenceNumber(1).Build()],
                                                   _tracker);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ReportProgress_WhenProgressProvided()
    {
        // Arrange
        var progressReports = new List<(int Resubmitted, int Skipped)>();
        var progress = new SynchronousProgress<(int Resubmitted, int Skipped)>(p => progressReports.Add(p));

        var msg = ServiceBusReceivedMessageBuilder.Create()
                                                  .WithMessageId("msg-1")
                                                  .WithSequenceNumber(1)
                                                  .Build();

        _mockFactory.WithMessagesToReturn(msg);

        var command = new ResubmitFromCacheCommand("test.servicebus.windows.net",
                                                   EntityTargetBuilder.Queue("source-queue"),
                                                   "target-queue",
                                                   [msg],
                                                   _tracker,
                                                   progress);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        progressReports.ShouldNotBeEmpty();
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
