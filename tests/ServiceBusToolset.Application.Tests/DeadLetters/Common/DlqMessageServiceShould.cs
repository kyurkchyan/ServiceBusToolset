using Azure;
using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqMessageServiceShould
{
    [Fact]
    public async Task GetMessageCount_WhenQueueTarget()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
        var runtimeProperties = ServiceBusModelFactory.QueueRuntimeProperties("test-queue",
                                                                              deadLetterMessageCount:42);

        mockFactory.AdminClient.GetQueueRuntimePropertiesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Response.FromValue(runtimeProperties, Substitute.For<Response>()));

        var service = new DlqMessageService(mockFactory.Object);

        // Act
        var count = await service.GetMessageCountAsync("test.servicebus.windows.net",
                                                       EntityTargetBuilder.Queue(),
                                                       CancellationToken.None);

        // Assert
        count.ShouldBe(42);
    }

    [Fact]
    public async Task GetMessageCount_WhenSubscriptionTarget()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
        var runtimeProperties = ServiceBusModelFactory.SubscriptionRuntimeProperties("test-topic",
                                                                                     "test-subscription",
                                                                                     deadLetterMessageCount:15);

        mockFactory.AdminClient.GetSubscriptionRuntimePropertiesAsync(Arg.Any<string>(),
                                                                      Arg.Any<string>(),
                                                                      Arg.Any<CancellationToken>())
                   .Returns(Response.FromValue(runtimeProperties, Substitute.For<Response>()));

        var service = new DlqMessageService(mockFactory.Object);

        // Act
        var count = await service.GetMessageCountAsync("test.servicebus.windows.net",
                                                       EntityTargetBuilder.Subscription(),
                                                       CancellationToken.None);

        // Assert
        count.ShouldBe(15);
    }

    [Fact]
    public async Task CountMessagesWithFilter_WhenMessagesMatchFilter()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(cutoffTime.AddHours(-1))
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        mockFactory.WithMessagesToReturn(messages);

        // Act
        var result = await DlqMessageService.CountMessagesWithFilterAsync(mockFactory.Client,
                                                                          EntityTargetBuilder.Queue(),
                                                                          cutoffTime,
                                                                          null,
                                                                          CancellationToken.None);

        // Assert
        result.TotalCount.ShouldBe(3);
        result.FilteredCount.ShouldBe(2); // Two messages before cutoff
    }

    [Fact]
    public async Task PeekAllMessages_WhenQueueHasMessages()
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

        // Act
        var result = await DlqMessageService.PeekAllMessagesAsync(mockFactory.Client,
                                                                  EntityTargetBuilder.Queue(),
                                                                  null,
                                                                  CancellationToken.None);

        // Assert
        result.Count.ShouldBe(2);
        result[0].MessageId.ShouldBe("msg-1");
        result[1].MessageId.ShouldBe("msg-2");
    }

    [Fact]
    public async Task PeekAllMessages_WhenQueueIsEmpty()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create().WithNoMessages();

        // Act
        var result = await DlqMessageService.PeekAllMessagesAsync(mockFactory.Client,
                                                                  EntityTargetBuilder.Queue(),
                                                                  null,
                                                                  CancellationToken.None);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ReturnFilteredMessages_WhenCategoriesMatch()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldAllBe(m => m.Subject == "OrderProcessor");
    }

    [Fact]
    public void ReturnAllMatching_WhenMultipleCategoriesProvided()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithSubject("ShippingService")
                                            .WithDeadLetterReason("ValidationError")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded"),
            DlqCategoryKey.FromMessage("PaymentHandler", "TimeoutExceeded")
        };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.Count.ShouldBe(2);
        result.Select(m => m.MessageId).ShouldBe(new[]
        {
            "msg-1",
            "msg-2"
        });
    }

    [Fact]
    public void ReturnEmptyList_WhenNoCategoriesMatch()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("DifferentLabel", "DifferentReason") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ReturnEmptyList_WhenMessagesAreEmpty()
    {
        // Arrange
        var messages = Array.Empty<ServiceBusReceivedMessage>();
        var categories = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("Label", "Reason") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void MatchNoneCategory_WhenMessageSubjectIsNull()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            // No subject set - will be null
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("HasSubject")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage(null, "MaxDeliveryCountExceeded") // "(none)" label
        };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.Count.ShouldBe(1);
        result.Single().MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void MatchNoneCategory_WhenMessageReasonIsNull()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("TestSubject")
                                            // No dead letter reason set - will be null
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("TestSubject")
                                            .WithDeadLetterReason("HasReason")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("TestSubject", null) // "(none)" reason
        };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.Count.ShouldBe(1);
        result.Single().MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void PreserveOrder_WhenFilteringMessages()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("Label")
                                            .WithDeadLetterReason("Reason")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithSubject("Other")
                                            .WithDeadLetterReason("Reason")
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithSubject("Label")
                                            .WithDeadLetterReason("Reason")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("Label", "Reason") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.Count.ShouldBe(2);
        result[0].MessageId.ShouldBe("msg-1");
        result[1].MessageId.ShouldBe("msg-3");
    }

    [Fact]
    public void BeCaseSensitive_WhenComparingCategories()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("orderprocessor", "maxdeliverycountexceeded") // lowercase
        };

        // Act
        var result = DlqMessageService.FilterByCategories(messages, categories);

        // Assert
        result.ShouldBeEmpty(); // Case-sensitive comparison
    }

    // --- Schema-aware filtering ---

    [Fact]
    public void FilterByBodyProperty_WhenSchemaUsesBodyProperty()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithJsonBody(new
                                            {
                                                tier = 1,
                                                errorCode = "E001"
                                            })
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithJsonBody(new
                                            {
                                                tier = 2,
                                                errorCode = "E002"
                                            })
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithJsonBody(new
                                            {
                                                tier = 1,
                                                errorCode = "E003"
                                            })
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        var schema = CategorizationSchema.Parse(["$tier"]);
        var resolver = new CategoryPropertyResolver();
        var categories = new HashSet<DlqCategoryKey> { new("1") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages,
                                                          categories,
                                                          schema,
                                                          resolver);

        // Assert
        result.Count.ShouldBe(2);
        result.Select(m => m.MessageId).ShouldBe(["msg-1", "msg-3"]);
    }

    [Fact]
    public void FilterByMixedProperties_WhenSchemaHasSystemAndBody()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { errorCode = "E001" })
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithJsonBody(new { errorCode = "E002" })
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithDeadLetterReason("TTLExpired")
                                            .WithJsonBody(new { errorCode = "E001" })
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        var schema = CategorizationSchema.Parse(["#DeadLetterReason", "$errorCode"]);
        var resolver = new CategoryPropertyResolver();
        var categories = new HashSet<DlqCategoryKey> { new("MaxDeliveryCountExceeded", "E001") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages,
                                                          categories,
                                                          schema,
                                                          resolver);

        // Assert
        result.Count.ShouldBe(1);
        result.Single().MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void FilterByNestedBodyProperty_WhenSchemaUsesNestedPath()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithJsonBody(new { error = new { severity = "critical" } })
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithJsonBody(new { error = new { severity = "warning" } })
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        var schema = CategorizationSchema.Parse(["$error.severity"]);
        var resolver = new CategoryPropertyResolver();
        var categories = new HashSet<DlqCategoryKey> { new("critical") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages,
                                                          categories,
                                                          schema,
                                                          resolver);

        // Assert
        result.Count.ShouldBe(1);
        result.Single().MessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void UseDefaultSchema_WhenSchemaIsNull()
    {
        // Arrange — same test as ReturnFilteredMessages_WhenCategoriesMatch but explicit null schema
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .Build()
        };

        var categories = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        // Act
        var result = DlqMessageService.FilterByCategories(messages,
                                                          categories,
                                                          null,
                                                          null);

        // Assert
        result.Count.ShouldBe(1);
    }
}
