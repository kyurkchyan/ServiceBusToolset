using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqScanSessionShould
{
    private static DlqScanSession CreateSession(
        ReactiveMessageCache<ServiceBusReceivedMessage, long>? cache = null)
    {
        cache ??= new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var categoryStream = Observable.Empty<DlqCategorySnapshot>();
        return new DlqScanSession(cache, categoryStream);
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
    public void CancelScanning_WhenStopScanningCalled()
    {
        // Arrange
        using var session = CreateSession();

        // Act
        session.StopScanning();

        // Assert
        session.ScanCancellationToken.IsCancellationRequested.ShouldBeTrue();
    }
}
