using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.ResubmitDlq;

public class ResubmitDlqMessagesCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly ResubmitDlqMessagesCommandHandler _handler;

    public ResubmitDlqMessagesCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create().WithSender();
        _handler = new ResubmitDlqMessagesCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ResubmitAllMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithBody("test body 1")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithBody("test body 2")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(2);
        result.Value.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendMessagesToTargetEntity_WhenHandlingCommand()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithMessageId("msg-1")
                                                      .WithBody("test body")
                                                      .WithSequenceNumber(1)
                                                      .Build();

        _mockFactory.WithMessagesToReturn(message);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockFactory.Client.Received(1).CreateSender(Arg.Is<string>(s => s == "target-queue"));

        await _mockFactory.Sender.Received(1).SendMessagesAsync(Arg.Is<IEnumerable<ServiceBusMessage>>(msgs => msgs.Any()),
                                                                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteOriginalMessages_WhenSendSucceeds()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithMessageId("msg-1")
                                                      .WithBody("test body")
                                                      .WithSequenceNumber(1)
                                                      .Build();

        _mockFactory.WithMessagesToReturn(message);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Receiver.Received(1).CompleteMessageAsync(Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "msg-1"),
                                                                     Arg.Any<CancellationToken>());
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
                                                              .WithReplyToSessionId("reply-session")
                                                              .WithSessionId("session-1")
                                                              .WithPartitionKey("session-1")
                                                              .WithTimeToLive(TimeSpan.FromHours(2))
                                                              .WithApplicationProperty("custom-prop", "custom-value")
                                                              .WithSequenceNumber(1)
                                                              .Build();

        _mockFactory.WithMessagesToReturn(originalMessage);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

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
        capturedMessage.ReplyToSessionId.ShouldBe("reply-session");
        capturedMessage.SessionId.ShouldBe("session-1");
        capturedMessage.PartitionKey.ShouldBe("session-1");
        capturedMessage.TimeToLive.ShouldBe(TimeSpan.FromHours(2));
        capturedMessage.ApplicationProperties.ShouldContainKey("custom-prop");
        capturedMessage.ApplicationProperties["custom-prop"].ShouldBe("custom-value");
    }

    [Fact]
    public async Task ResubmitMatchingAndSkipNonMatching_WhenBeforeTimeFilterProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var oldMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithMessageId("old-msg")
                                                         .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-2))
                                                         .WithSequenceNumber(1)
                                                         .Build();

        var newMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithMessageId("new-msg")
                                                         .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(1))
                                                         .WithSequenceNumber(2)
                                                         .Build();

        _mockFactory.WithMessagesToReturn(oldMessage, newMessage);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue",
                                                     cutoffTime);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResubmitMatchingCategories_WhenCategoryFilterProvided()
    {
        // Arrange
        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-1")
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(1)
                                                   .Build();

        var msg2 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-2")
                                                   .WithSubject("PaymentHandler")
                                                   .WithDeadLetterReason("TimeoutExceeded")
                                                   .WithSequenceNumber(2)
                                                   .Build();

        _mockFactory.WithMessagesToReturn(msg1, msg2);

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue",
                                                     CategoryFilter: categoryFilter);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task AbandonNonMatchingMessages_WhenFilterProvided()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithMessageId("msg-1")
                                                      .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(1))
                                                      .WithSequenceNumber(1)
                                                      .Build();

        _mockFactory.WithMessagesToReturn(message);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue",
                                                     DateTimeOffset.UtcNow);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Receiver.Received(1).AbandonMessageAsync(Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "msg-1"),
                                                                    Arg.Any<IDictionary<string, object>>(),
                                                                    Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnZeroCounts_WhenNoMessages()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task UseCorrectPath_WhenSubscriptionTargetProvided()
    {
        // Arrange
        _mockFactory.WithMessagesToReturn(ServiceBusReceivedMessageBuilder.Create()
                                                                          .WithMessageId("msg-1")
                                                                          .WithSequenceNumber(1)
                                                                          .Build());

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Subscription("source-topic", "source-subscription"),
                                                     "target-queue");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockFactory.Client.Received(1).CreateReceiver(Arg.Is<string>(s => s == "source-topic"),
                                                       Arg.Is<string>(s => s == "source-subscription"),
                                                       Arg.Any<ServiceBusReceiverOptions>());
    }

    [Fact]
    public async Task ReportProgress_WhenProgressProvided()
    {
        // Arrange
        var progressReports = new List<(int Resubmitted, int Skipped)>();
        var progress = new Progress<(int Resubmitted, int Skipped)>(p => progressReports.Add(p));

        _mockFactory.WithMessagesToReturn(ServiceBusReceivedMessageBuilder.Create()
                                                                          .WithMessageId("msg-1")
                                                                          .WithSequenceNumber(1)
                                                                          .Build());

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue",
                                                     Progress: progress);

        // Act
        await _handler.Handle(command, CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken); // Allow Progress<T> thread pool callback to fire

        // Assert
        progressReports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CombineFilters_WhenBothTimeAndCategoryFiltersProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var matchingMessage = ServiceBusReceivedMessageBuilder.Create()
                                                              .WithMessageId("matching")
                                                              .WithSubject("OrderProcessor")
                                                              .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                              .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-2))
                                                              .WithSequenceNumber(1)
                                                              .Build();

        var tooNewMessage = ServiceBusReceivedMessageBuilder.Create()
                                                            .WithMessageId("too-new")
                                                            .WithSubject("OrderProcessor")
                                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(1))
                                                            .WithSequenceNumber(2)
                                                            .Build();

        _mockFactory.WithMessagesToReturn(matchingMessage, tooNewMessage);

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue",
                                                     cutoffTime,
                                                     categoryFilter);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResubmittedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task DisposeClient_WhenHandlingCompletes()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task PreserveMessageBody_WhenResubmitting()
    {
        // Arrange
        var messageBody = "{\"order\": {\"id\": 123, \"items\": []}}";
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
                                                              .WithMessageId("msg-1")
                                                              .WithBody(messageBody)
                                                              .WithSequenceNumber(1)
                                                              .Build();

        _mockFactory.WithMessagesToReturn(originalMessage);

        var command = new ResubmitDlqMessagesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue("source-queue"),
                                                     "target-queue");

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        capturedMessage.ShouldNotBeNull();
        capturedMessage!.Body.ToString().ShouldBe(messageBody);
    }
}
