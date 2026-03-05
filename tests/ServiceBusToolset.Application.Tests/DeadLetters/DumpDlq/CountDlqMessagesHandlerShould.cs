using Azure;
using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DumpDlq;

public class CountDlqMessagesHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly CountDlqMessagesHandler _handler;

    public CountDlqMessagesHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        var messageService = new DlqMessageService(_mockFactory.Object);
        _handler = new CountDlqMessagesHandler(_mockFactory.Object, messageService);
    }

    [Fact]
    public async Task CountAllMessages_WhenNoFilterProvided()
    {
        // Arrange
        var runtimeProperties = ServiceBusModelFactory.QueueRuntimeProperties("test-queue",
                                                                              deadLetterMessageCount:42);

        _mockFactory.AdminClient.GetQueueRuntimePropertiesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Response.FromValue(runtimeProperties, Substitute.For<Response>()));

        var command = new CountDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(42);
        result.Value.FilteredCount.ShouldBeNull();
        result.Value.BeforeTime.ShouldBeNull();
    }

    [Fact]
    public async Task CountFilteredMessages_WhenTimeFilterProvided()
    {
        // Arrange
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

        _mockFactory.WithMessagesToReturn(messages);

        var command = new CountDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(3);
        result.Value.FilteredCount.ShouldBe(2);
        result.Value.BeforeTime.ShouldBe(cutoffTime);
    }

    [Fact]
    public async Task ReturnZero_WhenQueueIsEmpty()
    {
        // Arrange
        var runtimeProperties = ServiceBusModelFactory.QueueRuntimeProperties("test-queue",
                                                                              deadLetterMessageCount:0);

        _mockFactory.AdminClient.GetQueueRuntimePropertiesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Response.FromValue(runtimeProperties, Substitute.For<Response>()));

        var command = new CountDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task CountSubscriptionMessages_WhenSubscriptionTargetProvided()
    {
        // Arrange
        var runtimeProperties = ServiceBusModelFactory.SubscriptionRuntimeProperties("test-topic",
                                                                                     "test-subscription",
                                                                                     deadLetterMessageCount:25);

        _mockFactory.AdminClient.GetSubscriptionRuntimePropertiesAsync(Arg.Any<string>(),
                                                                       Arg.Any<string>(),
                                                                       Arg.Any<CancellationToken>())
                    .Returns(Response.FromValue(runtimeProperties, Substitute.For<Response>()));

        var command = new CountDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Subscription(),
                                                  null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(25);
    }

    [Fact]
    public async Task ReturnZeroFiltered_WhenNoMessagesMatchFilter()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(-5);

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new CountDlqMessagesCommand("test.servicebus.windows.net",
                                                  EntityTargetBuilder.Queue(),
                                                  cutoffTime);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalCount.ShouldBe(1);
        result.Value.FilteredCount.ShouldBe(0);
    }
}
