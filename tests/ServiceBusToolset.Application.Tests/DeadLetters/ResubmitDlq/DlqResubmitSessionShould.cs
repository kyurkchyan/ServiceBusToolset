using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.ResubmitDlq;

public class DlqResubmitSessionShould
{
    private static DlqResubmitSession CreateSession(
        ReactiveMessageCache<ServiceBusReceivedMessage, long>? cache = null,
        ResubmitTracker? tracker = null)
    {
        cache ??= new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        tracker ??= new ResubmitTracker();
        var categoryStream = Observable.Empty<DlqCategorySnapshot>();
        return new DlqResubmitSession(cache, categoryStream, tracker);
    }

    [Fact]
    public void ReturnFilteredSnapshot_WhenCategoryKeysProvided()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        using var session = CreateSession(cache);

        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(1)
                                                   .Build();

        var msg2 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithSubject("PaymentHandler")
                                                   .WithDeadLetterReason("TimeoutExceeded")
                                                   .WithSequenceNumber(2)
                                                   .Build();

        cache.AddOrUpdate([msg1, msg2]);

        var selectedKeys = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        // Act
        var result = session.SnapshotForCategories(selectedKeys);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Subject.ShouldBe("OrderProcessor");
    }

    [Fact]
    public void ExcludeResubmittedMessages_WhenSnapshotTaken()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var tracker = new ResubmitTracker();
        using var session = CreateSession(cache, tracker);

        var msg1 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-1")
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(1)
                                                   .Build();

        var msg2 = ServiceBusReceivedMessageBuilder.Create()
                                                   .WithMessageId("msg-2")
                                                   .WithSubject("OrderProcessor")
                                                   .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                   .WithSequenceNumber(2)
                                                   .Build();

        cache.AddOrUpdate([msg1, msg2]);
        tracker.MarkResubmitted("msg-1");

        var selectedKeys = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        // Act
        var result = session.SnapshotForCategories(selectedKeys);

        // Assert
        result.Count.ShouldBe(1);
        result[0].MessageId.ShouldBe("msg-2");
    }

    [Fact]
    public void ReturnEmptyList_WhenNoCategoriesMatch()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        using var session = CreateSession(cache);

        var msg = ServiceBusReceivedMessageBuilder.Create()
                                                  .WithSubject("OrderProcessor")
                                                  .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                  .WithSequenceNumber(1)
                                                  .Build();

        cache.AddOrUpdate([msg]);

        var selectedKeys = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("UnknownHandler", "UnknownReason") };

        // Act
        var result = session.SnapshotForCategories(selectedKeys);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyBeforeTimeFilter_WhenBeforeTimeProvided()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        using var session = CreateSession(cache);

        var cutoffTime = DateTimeOffset.UtcNow;

        var oldMsg = ServiceBusReceivedMessageBuilder.Create()
                                                     .WithSubject("OrderProcessor")
                                                     .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                     .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                                     .WithSequenceNumber(1)
                                                     .Build();

        var newMsg = ServiceBusReceivedMessageBuilder.Create()
                                                     .WithSubject("OrderProcessor")
                                                     .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                     .WithEnqueuedTime(cutoffTime.AddHours(1))
                                                     .WithSequenceNumber(2)
                                                     .Build();

        cache.AddOrUpdate([oldMsg, newMsg]);

        var selectedKeys = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        // Act
        var result = session.SnapshotForCategories(selectedKeys, cutoffTime);

        // Assert
        result.Count.ShouldBe(1);
        result[0].SequenceNumber.ShouldBe(1);
    }
}
