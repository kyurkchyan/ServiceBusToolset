using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqMessageServiceShould
{
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
