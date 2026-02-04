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
    public async Task AnalyzeCategories_WhenMessagesHaveVariousCategories()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();

        var messages = new[]
        {
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
        };

        mockFactory.WithMessagesToReturn(messages);

        // Act
        var categories = await DlqMessageService.AnalyzeCategoriesAsync(mockFactory.Client,
                                                                        EntityTargetBuilder.Queue(),
                                                                        null,
                                                                        CancellationToken.None);

        // Assert
        categories.Count.ShouldBe(2);
        categories[0].Label.ShouldBe("OrderProcessor");
        categories[0].DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        categories[0].Count.ShouldBe(2);
        categories[1].Label.ShouldBe("PaymentHandler");
        categories[1].DeadLetterReason.ShouldBe("TimeoutExceeded");
        categories[1].Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnalyzeCategories_SortByCountDescending()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create();

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("LowCount")
                                            .WithDeadLetterReason("Reason")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("HighCount")
                                            .WithDeadLetterReason("Reason")
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("HighCount")
                                            .WithDeadLetterReason("Reason")
                                            .WithSequenceNumber(3)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("HighCount")
                                            .WithDeadLetterReason("Reason")
                                            .WithSequenceNumber(4)
                                            .Build()
        };

        mockFactory.WithMessagesToReturn(messages);

        // Act
        var categories = await DlqMessageService.AnalyzeCategoriesAsync(mockFactory.Client,
                                                                        EntityTargetBuilder.Queue(),
                                                                        null,
                                                                        CancellationToken.None);

        // Assert
        categories.Count.ShouldBe(2);
        categories[0].Label.ShouldBe("HighCount"); // Higher count first
        categories[0].Count.ShouldBe(3);
        categories[1].Label.ShouldBe("LowCount");
        categories[1].Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnalyzeCategories_WhenQueueIsEmpty()
    {
        // Arrange
        var mockFactory = MockServiceBusClientFactory.Create().WithNoMessages();

        // Act
        var categories = await DlqMessageService.AnalyzeCategoriesAsync(mockFactory.Client,
                                                                        EntityTargetBuilder.Queue(),
                                                                        null,
                                                                        CancellationToken.None);

        // Assert
        categories.ShouldBeEmpty();
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
}
