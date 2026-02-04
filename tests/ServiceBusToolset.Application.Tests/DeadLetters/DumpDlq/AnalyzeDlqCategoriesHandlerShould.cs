using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DumpDlq;

public class AnalyzeDlqCategoriesHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly AnalyzeDlqCategoriesHandler _handler;

    public AnalyzeDlqCategoriesHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new AnalyzeDlqCategoriesHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task GroupMessagesByCategory_WhenMessagesExist()
    {
        // Arrange
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

        _mockFactory.WithMessagesToReturn(messages);

        var command = new AnalyzeDlqCategoriesCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Categories.Count.ShouldBe(2);
        result.Value.TotalMessageCount.ShouldBe(3);
    }

    [Fact]
    public async Task SortCategoriesByCountDescending()
    {
        // Arrange
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

        _mockFactory.WithMessagesToReturn(messages);

        var command = new AnalyzeDlqCategoriesCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Categories[0].Label.ShouldBe("HighCount");
        result.Value.Categories[0].Count.ShouldBe(3);
        result.Value.Categories[1].Label.ShouldBe("LowCount");
        result.Value.Categories[1].Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReturnEmptyList_WhenQueueIsEmpty()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new AnalyzeDlqCategoriesCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Categories.ShouldBeEmpty();
        result.Value.TotalMessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task HandleNullSubjectsAndReasons()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSequenceNumber(1)
                                            // No subject or reason
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithSubject("HasSubject")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new AnalyzeDlqCategoriesCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Categories.Count.ShouldBe(2);
        result.Value.Categories.ShouldContain(c => c.Label == "(none)");
    }

    [Fact]
    public async Task DisposeClient_WhenHandlingCompletes()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new AnalyzeDlqCategoriesCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockFactory.Client.Received(1).DisposeAsync();
    }
}
