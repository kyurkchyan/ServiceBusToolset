using System.Diagnostics;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DiagnoseDlq;

public class StreamDlqForDiagnoseHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly StreamDlqForDiagnoseCommandHandler _handler;

    public StreamDlqForDiagnoseHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new StreamDlqForDiagnoseCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnSession_WhenHandled()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = new StreamDlqForDiagnoseCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value.Cache.ShouldNotBeNull();
        result.Value.CategoryStream.ShouldNotBeNull();
    }

    [Fact]
    public async Task PopulateCacheWithMessages_WhenFeedCompletes()
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

        var command = new StreamDlqForDiagnoseCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Wait for background feed to complete
        await WaitForComplete(result.Value);

        // Assert
        result.Value.Cache.Count.ShouldBe(2);
    }

    [Fact]
    public async Task IncludeAllMessages_WithoutFiltering()
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
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-3")
                                            .WithSubject("NotificationService")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithSequenceNumber(3)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = new StreamDlqForDiagnoseCommand("test.servicebus.windows.net",
                                                      EntityTargetBuilder.Queue());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);
        await WaitForComplete(result.Value);

        // Assert - all messages should be in cache (no filtering)
        result.Value.Cache.Count.ShouldBe(3);

        var snapshot = DlqCategoryScanner.BuildCategorySnapshot(result.Value.Cache);
        snapshot.Categories.Count.ShouldBe(3);
    }

    private static async Task WaitForComplete(DlqScanSession session, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!session.Cache.IsComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
    }
}
