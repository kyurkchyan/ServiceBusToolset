using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DumpDlq;

public class DumpFromCacheCommandHandlerShould
{
    private readonly DumpFromCacheCommandHandler _handler = new();

    [Fact]
    public async Task DumpMessages_WhenMessagesProvided()
    {
        // Arrange
        var outputPath = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid()}.json");
        try
        {
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

            var command = new DumpFromCacheCommand(messages, outputPath);

            // Act
            var result = await _handler.Handle(command, CancellationToken.None);

            // Assert
            result.IsSuccess.ShouldBeTrue();
            result.Value.MessageCount.ShouldBe(2);
            result.Value.OutputFilePath.ShouldBe(outputPath);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task ReturnZeroCount_WhenNoMessages()
    {
        // Arrange
        var outputPath = Path.Combine(Path.GetTempPath(), $"dump-test-empty-{Guid.NewGuid()}.json");
        var command = new DumpFromCacheCommand([], outputPath);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(0);
        result.Value.OutputFilePath.ShouldBe(outputPath);
        File.Exists(outputPath).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteToFile_WhenMessagesExist()
    {
        // Arrange
        var outputPath = Path.Combine(Path.GetTempPath(), $"dump-test-file-{Guid.NewGuid()}.json");
        try
        {
            var messages = new[]
            {
                ServiceBusReceivedMessageBuilder.Create()
                                                .WithMessageId("msg-1")
                                                .WithSubject("OrderProcessor")
                                                .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                .WithBody("{\"orderId\": 123}")
                                                .WithSequenceNumber(1)
                                                .Build()
            };

            var command = new DumpFromCacheCommand(messages, outputPath);

            // Act
            await _handler.Handle(command, CancellationToken.None);

            // Assert
            File.Exists(outputPath).ShouldBeTrue();
            var content = await File.ReadAllTextAsync(outputPath);
            content.ShouldContain("msg-1");
            content.ShouldContain("OrderProcessor");
            content.ShouldContain("MaxDeliveryCountExceeded");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
