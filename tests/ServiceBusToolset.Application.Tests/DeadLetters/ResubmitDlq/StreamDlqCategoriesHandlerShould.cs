using System.Diagnostics;
using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.ResubmitDlq;

public class StreamDlqCategoriesHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly StreamDlqCategoriesCommandHandler _handler;

    public StreamDlqCategoriesHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new StreamDlqCategoriesCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnSession_WhenHandled()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new StreamDlqCategoriesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value.Cache.ShouldNotBeNull();
        result.Value.ResubmitTracker.ShouldNotBeNull();
        result.Value.CategoryStream.ShouldNotBeNull();
    }

    [Fact]
    public async Task PopulateCacheWithMessages_WhenFeedCompletes()
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

        var command = new StreamDlqCategoriesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Wait for background feed to complete
        await WaitForComplete(result.Value);

        // Assert
        result.Value.Cache.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EmitCategorySnapshots_WhenMessagesArrive()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new StreamDlqCategoriesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Get the first snapshot (StartWith emits the initial empty one)
        var firstSnapshot = await result.Value.CategoryStream.FirstAsync();

        // Assert
        firstSnapshot.ShouldNotBeNull();
        firstSnapshot.TotalMessageCount.ShouldBe(0);
        firstSnapshot.Categories.ShouldBeEmpty();
    }

    [Fact]
    public async Task GroupMessagesIntoCategoriesCorrectly()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(1)
                                                   .Build();

        var msg2 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(2)
                                                   .Build();

        var msg3 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithSubject("PaymentHandler")
                                                   .WithDeadLetterReason("TimeoutExceeded")
                                                   .WithSequenceNumber(3)
                                                   .Build();

        cache.AddOrUpdate([msg1, msg2, msg3]);

        // Act
        var snapshot = StreamDlqCategoriesCommandHandler.BuildCategorySnapshot(cache);

        // Assert
        snapshot.TotalMessageCount.ShouldBe(3);
        snapshot.Categories.Count.ShouldBe(2);
        snapshot.Categories.ShouldContain(c => c.Label == "OrderProcessor" && c.Count == 2);
        snapshot.Categories.ShouldContain(c => c.Label == "PaymentHandler" && c.Count == 1);

        cache.Dispose();
    }

    [Fact]
    public async Task MarkCacheAsComplete_WhenAllMessagesPeeked()
    {
        // Arrange
        _mockFactory.WithMessagesToReturn(ServiceBusReceivedMessageBuilder.Create()
                                                                          .WithSequenceNumber(1)
                                                                          .Build());

        var command = new StreamDlqCategoriesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        await WaitForComplete(result.Value);

        // Assert
        result.Value.Cache.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task ExcludePreviouslyResubmittedMessages_WhenFeeding()
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

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var tracker = new ResubmitTracker();
        tracker.MarkResubmitted("msg-1");

        var session = new DlqResubmitSession(cache,
                                             cache.Connect().Select(_ => StreamDlqCategoriesCommandHandler.BuildCategorySnapshot(cache)),
                                             tracker);

        var command = new StreamDlqCategoriesCommand("test.servicebus.windows.net",
                                                     EntityTargetBuilder.Queue());

        // Act
        await StreamDlqCategoriesCommandHandler.FeedCacheAsync(_mockFactory.Object,
                                                               command,
                                                               cache,
                                                               tracker,
                                                               session,
                                                               CancellationToken.None);

        // Assert
        cache.Count.ShouldBe(1);
        cache.Lookup(2).ShouldNotBeNull();
        cache.Lookup(1).ShouldBeNull();

        cache.Dispose();
    }

    private static async Task WaitForComplete(DlqResubmitSession session, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!session.Cache.IsComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
    }
}
