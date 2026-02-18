using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.PurgeDlq;

public class PurgeFromCacheCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly PurgeFromCacheCommandHandler _handler;

    public PurgeFromCacheCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new PurgeFromCacheCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task PurgeMatchingMessages_WhenMessagesProvided()
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

        var command = new PurgeFromCacheCommand("test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                [msg1]);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(1);
        await _mockFactory.Receiver.Received(1).CompleteMessageAsync(
            Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "msg-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbandonNonMatchingMessages_WhenMessagesProvided()
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

        var command = new PurgeFromCacheCommand("test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                [msg1]);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Receiver.Received(1).AbandonMessageAsync(
            Arg.Is<ServiceBusReceivedMessage>(m => m.MessageId == "msg-2"),
            Arg.Any<IDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnZeroCounts_WhenNoMessages()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new PurgeFromCacheCommand("test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                []);

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

        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                    .WithMessageId("msg-1")
                                                    .WithSequenceNumber(1)
                                                    .Build();

        _mockFactory.WithMessagesToReturn(msg1);

        var command = new PurgeFromCacheCommand("test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                [msg1],
                                                progress);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Allow Progress<T> callback to fire via thread pool
        await Task.Delay(100);

        // Assert
        progressReports.ShouldNotBeEmpty();
    }
}
