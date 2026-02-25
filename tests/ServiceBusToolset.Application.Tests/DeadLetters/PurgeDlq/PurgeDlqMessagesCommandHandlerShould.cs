using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.PurgeDlq;

public class PurgeDlqMessagesCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly PurgeDlqMessagesCommandHandler _handler;

    public PurgeDlqMessagesCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new PurgeDlqMessagesCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task PurgeAllMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(2);
        result.Value.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task PurgeMatchingMessages_WhenTimeFilterProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("old-msg")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("new-msg")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task PurgeMatchingMessages_WhenCategoryFilterProvided()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  CategoryFilter: categoryFilter);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteMatchingMessages_WhenFilterProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var oldMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithMessageId("old-msg")
                                                         .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                                         .WithSequenceNumber(1)
                                                         .Build();

        _mockFactory.WithMessagesToReturn(oldMessage);

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Receiver.Received(1).CompleteMessageAsync(Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "old-msg"),
                                                                     Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbandonNonMatchingMessages_WhenFilterProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var newMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithMessageId("new-msg")
                                                         .WithEnqueuedTime(cutoffTime.AddHours(1))
                                                         .WithSequenceNumber(1)
                                                         .Build();

        _mockFactory.WithMessagesToReturn(newMessage);

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Receiver.Received(1).AbandonMessageAsync(Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "new-msg"),
                                                                    Arg.Any<IDictionary<string, object>?>(),
                                                                    Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnZeroCounts_WhenQueueIsEmpty()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(0);
        result.Value.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReportProgress_WhenProgressProvided()
    {
        // Arrange
        var progressReports = new List<(int Purged, int Skipped)>();
        var progress = new Progress<(int Purged, int Skipped)>(p => progressReports.Add(p));

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  Progress: progress);

        // Act
        await _handler.Handle(command, CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken); // Allow Progress<T> thread pool callback to fire

        // Assert
        progressReports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task PurgeWithCombinedFilters_WhenBothFiltersProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("matching")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("wrong-time")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("wrong-category")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-1))
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime,
                                                  categoryFilter);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(1);
        result.Value.SkippedCount.ShouldBe(2);
    }

    [Fact]
    public async Task UseSubscriptionPath_WhenSubscriptionTargetProvided()
    {
        // Arrange
        _mockFactory.WithMessagesToReturn(ServiceBusReceivedMessageBuilder.Create()
                                                                          .WithMessageId("msg-1")
                                                                          .WithSequenceNumber(1)
                                                                          .Build());

        var command = new PurgeDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Subscription("test-topic", "test-sub"));

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockFactory.Client.Received(1).CreateReceiver(Arg.Is<string>(s => s == "test-topic"),
                                                       Arg.Is<string>(s => s == "test-sub"),
                                                       Arg.Any<ServiceBusReceiverOptions>());
    }
}
