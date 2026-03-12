using System.Reactive.Linq;
using Azure.Messaging.ServiceBus;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqCategoryScannerShould
{
    [Fact]
    public void BuildCategorySnapshot_GroupsMessagesByCategory()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithSequenceNumber(3)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache);

        // Assert
        snapshot.TotalMessageCount.ShouldBe(3);
        snapshot.Categories.Count.ShouldBe(2);
        snapshot.Categories.ShouldContain(c => c.Label == "OrderProcessor" && c.Count == 2);
        snapshot.Categories.ShouldContain(c => c.Label == "PaymentHandler" && c.Count == 1);
        snapshot.MergeResult.ShouldBeNull();

        cache.Dispose();
    }

    [Fact]
    public void BuildCategorySnapshot_MergesCategories_WhenMergeSimilarTrue()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("Order 123 failed")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("Order 456 failed")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(2)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache, true);

        // Assert
        snapshot.MergeResult.ShouldNotBeNull();
        snapshot.TotalMessageCount.ShouldBe(2);

        cache.Dispose();
    }

    [Fact]
    public void BuildCategorySnapshot_ReturnsEmptySnapshot_WhenCacheEmpty()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache);

        // Assert
        snapshot.TotalMessageCount.ShouldBe(0);
        snapshot.Categories.ShouldBeEmpty();

        cache.Dispose();
    }

    [Fact]
    public async Task FeedCacheAsync_PopulatesCacheWithMessages()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
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

        mockFactory.WithMessagesToReturn(messages);

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var session = new DlqScanSession(cache, Observable.Empty<DlqCategorySnapshot>());

        // Act
        await DlqCategoryScanner.FeedCacheAsync(mockFactory.Object,
                                                "test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                cache,
                                                session,
                                                cancellationToken:CancellationToken.None);

        // Assert
        cache.Count.ShouldBe(2);
        cache.IsComplete.ShouldBeTrue();
        session.Error.ShouldBeNull();

        cache.Dispose();
    }

    [Fact]
    public async Task FeedCacheAsync_AppliesMessageFilter_WhenProvided()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
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

        mockFactory.WithMessagesToReturn(messages);

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var session = new DlqScanSession(cache, Observable.Empty<DlqCategorySnapshot>());

        // Act
        await DlqCategoryScanner.FeedCacheAsync(mockFactory.Object,
                                                "test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                cache,
                                                session,
                                                m => m.MessageId == "msg-2",
                                                CancellationToken.None);

        // Assert
        cache.Count.ShouldBe(1);
        cache.Lookup(2).ShouldNotBeNull();
        cache.Lookup(1).ShouldBeNull();

        cache.Dispose();
    }

    [Fact]
    public async Task FeedCacheAsync_SkipsFilter_WhenNoFilterProvided()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
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

        mockFactory.WithMessagesToReturn(messages);

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var session = new DlqScanSession(cache, Observable.Empty<DlqCategorySnapshot>());

        // Act
        await DlqCategoryScanner.FeedCacheAsync(mockFactory.Object,
                                                "test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                cache,
                                                session,
                                                cancellationToken:CancellationToken.None);

        // Assert
        cache.Count.ShouldBe(2);

        cache.Dispose();
    }

    [Fact]
    public async Task FeedCacheAsync_SetsScanCompletion_WhenDone()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
        mockFactory.WithNoMessages();

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var session = new DlqScanSession(cache, Observable.Empty<DlqCategorySnapshot>());

        // Act
        await DlqCategoryScanner.FeedCacheAsync(mockFactory.Object,
                                                "test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                cache,
                                                session,
                                                cancellationToken:CancellationToken.None);

        // Assert
        session.ScanCompletion.Task.IsCompleted.ShouldBeTrue();
        cache.IsComplete.ShouldBeTrue();

        cache.Dispose();
    }

    [Fact]
    public async Task FeedCacheAsync_SetsSessionError_WhenExceptionOccurs()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();

        // Configure the receiver to throw an exception during peek
        mockFactory.WithNoMessages();
        mockFactory.Receiver
                   .PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                   .ThrowsAsync(new InvalidOperationException("Test exception"));

        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var session = new DlqScanSession(cache, Observable.Empty<DlqCategorySnapshot>());

        // Act
        await DlqCategoryScanner.FeedCacheAsync(mockFactory.Object,
                                                "test.servicebus.windows.net",
                                                EntityTargetBuilder.Queue(),
                                                cache,
                                                session,
                                                cancellationToken:CancellationToken.None);

        // Assert
        session.Error.ShouldNotBeNull();
        session.Error.ShouldBeOfType<InvalidOperationException>();
        cache.IsComplete.ShouldBeTrue();
        session.ScanCompletion.Task.IsCompleted.ShouldBeTrue();

        cache.Dispose();
    }

    /// <summary>
    /// Verifies that building a category snapshot groups dead-lettered messages by the provided schema field (`$tier`) instead of the default Subject/DeadLetterReason.
    /// </summary>
    /// <remarks>
    /// Expects a total message count of 3 and two categories: one for tier "1" with count 2 and one for tier "2" with count 1.
    /// </remarks>

    [Fact]
    public void BuildCategorySnapshot_GroupsByCustomSchema()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var schema = CategorizationSchema.Parse(["$tier"]);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { tier = 1 })
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithJsonBody(new { tier = 1 })
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { tier = 2 })
                                            .WithSequenceNumber(3)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache, schema:schema);

        // Assert — grouped by $tier instead of Subject/DeadLetterReason
        snapshot.TotalMessageCount.ShouldBe(3);
        snapshot.Categories.Count.ShouldBe(2);
        snapshot.Categories.ShouldContain(c => c.Values[0] == "1" && c.Count == 2);
        snapshot.Categories.ShouldContain(c => c.Values[0] == "2" && c.Count == 1);

        cache.Dispose();
    }

    /// <summary>
    /// Verifies that BuildCategorySnapshot groups messages by a mixed schema containing the dead-letter reason and the JSON field "errorCode".
    /// </summary>
    /// <remarks>
    /// Expects a snapshot with a total message count of 3 and two categories:
    /// one for ["MaxDeliveryCountExceeded", "E001"] with count 2, and one for ["MaxDeliveryCountExceeded", "E002"] with count 1.
    /// </remarks>
    [Fact]
    public void BuildCategorySnapshot_GroupsByMixedSchema()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var schema = CategorizationSchema.Parse(["#DeadLetterReason", "$errorCode"]);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { errorCode = "E001" })
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { errorCode = "E001" })
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { errorCode = "E002" })
                                            .WithSequenceNumber(3)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache, schema:schema);

        // Assert
        snapshot.TotalMessageCount.ShouldBe(3);
        snapshot.Categories.Count.ShouldBe(2);
        snapshot.Categories.ShouldContain(c => c.Values[0] == "MaxDeliveryCountExceeded" && c.Values[1] == "E001" && c.Count == 2);
        snapshot.Categories.ShouldContain(c => c.Values[0] == "MaxDeliveryCountExceeded" && c.Values[1] == "E002" && c.Count == 1);

        cache.Dispose();
    }

    [Fact]
    public void BuildCategorySnapshot_IncludesSchemaInSnapshot()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var schema = CategorizationSchema.Parse(["$tier"]);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithJsonBody(new { tier = 1 })
                                            .WithSequenceNumber(1)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache, schema:schema);

        // Assert
        snapshot.Schema.ShouldNotBeNull();
        snapshot.Schema.ShouldBeSameAs(schema);

        cache.Dispose();
    }

    [Fact]
    public void BuildCategorySnapshot_MergesWithCustomSchema()
    {
        // Arrange
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var schema = CategorizationSchema.Parse(["#DeadLetterReason"]);

        cache.AddOrUpdate([
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithDeadLetterReason("Retry after 3 attempts")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithDeadLetterReason("Retry after 5 attempts")
                                            .WithSequenceNumber(2)
                                            .Build()
        ]);

        // Act
        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(cache, true, schema);

        // Assert
        snapshot.MergeResult.ShouldNotBeNull();
        snapshot.MergeResult.MergedCategories.Count.ShouldBe(1);
        snapshot.MergeResult.MergedCategories[0].Values[0].ShouldBe("Retry after * attempts");

        cache.Dispose();
    }
}
