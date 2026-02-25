using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DumpDlq;

public class DumpDlqMessagesCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly DumpDlqMessagesCommandHandler _handler;
    private readonly string _testOutputPath;

    public DumpDlqMessagesCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new DumpDlqMessagesCommandHandler(_mockFactory.Object);
        _testOutputPath = Path.Combine(Path.GetTempPath(), $"test-dump-{Guid.NewGuid()}.json");
    }

    [Fact]
    public async Task DumpMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithBody("{\"data\":\"test1\"}")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithBody("{\"data\":\"test2\"}")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(2);
        result.Value.OutputFilePath.ShouldBe(_testOutputPath);
    }

    [Fact]
    public async Task DumpMessages_WhenTimeFilterProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("old-msg")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("new-msg")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath,
                                                 cutoffTime);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(1); // Only the old message
    }

    [Fact]
    public async Task DumpMessages_WhenCategoryFilterProvided()
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

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath,
                                                 CategoryFilter: categoryFilter);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task DumpMessages_WhenCombinedFiltersProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("matching")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("wrong-time")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(2)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("wrong-category")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-1))
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var categoryFilter = new HashSet<DlqCategoryKey> { DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded") };

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath,
                                                 cutoffTime,
                                                 categoryFilter);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(1); // Only "matching" passes both filters
    }

    [Fact]
    public async Task ReturnZeroCount_WhenQueueIsEmpty()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReturnZeroCount_WhenNoMessagesMatchFilter()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(1))
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath,
                                                 DateTimeOffset.UtcNow);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task WriteToFile_WhenMessagesExist()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithBody("{\"test\":\"data\"}")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.Exists(_testOutputPath).ShouldBeTrue();

        var fileContent = await File.ReadAllTextAsync(_testOutputPath, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("msg-1");
    }

    [Fact]
    public async Task DisposeClient_WhenHandlingCompletes()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Queue(),
                                                 _testOutputPath);

        // Act
        await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        await _mockFactory.Client.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task UseSubscriptionPath_WhenSubscriptionTargetProvided()
    {
        // Arrange
        _mockFactory.WithMessagesToReturn(ServiceBusReceivedMessageBuilder.Create()
                                                                          .WithMessageId("msg-1")
                                                                          .WithSequenceNumber(1)
                                                                          .Build());

        var command = new DumpDlqMessagesCommand("test.servicebus.windows.net",
                                                 EntityTargetBuilder.Subscription("test-topic", "test-sub"),
                                                 _testOutputPath);

        // Act
        await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        _mockFactory.Client.Received(1).CreateReceiver(Arg.Is<string>(s => s == "test-topic"),
                                                       Arg.Is<string>(s => s == "test-sub"),
                                                       Arg.Any<ServiceBusReceiverOptions>());
    }
}
