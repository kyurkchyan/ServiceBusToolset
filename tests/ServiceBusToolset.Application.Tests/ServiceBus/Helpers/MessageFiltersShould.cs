using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Helpers;

public class MessageFiltersShould
{
    [Fact]
    public void ReturnAllMessages_WhenBeforeTimeIsNull()
    {
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-2))
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-1))
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow)
                                            .Build()
        };

        var result = MessageFilters.FilterByEnqueueTime(messages, null);

        result.Count.ShouldBe(3);
    }

    [Fact]
    public void FilterCorrectly_WhenBeforeTimeProvided()
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(-1);

        var oldMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-2))
                                                         .WithMessageId("old")
                                                         .Build();

        var newMessage = ServiceBusReceivedMessageBuilder.Create()
                                                         .WithEnqueuedTime(DateTimeOffset.UtcNow)
                                                         .WithMessageId("new")
                                                         .Build();

        var messages = new[]
        {
            oldMessage,
            newMessage
        };

        var result = MessageFilters.FilterByEnqueueTime(messages, cutoffTime);

        result.Count.ShouldBe(1);
        result.ShouldContain(m => m.MessageId == "old");
    }

    [Fact]
    public void ReturnEmptyList_WhenCollectionIsEmpty()
    {
        var messages = Array.Empty<ServiceBusReceivedMessage>();
        var result = MessageFilters.FilterByEnqueueTime(messages, DateTimeOffset.UtcNow);
        result.ShouldBeEmpty();
    }
}
