using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;
using ServiceBusMessageDto = ServiceBusToolset.Application.Common.ServiceBus.Models.ServiceBusMessage;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class DumpDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task WriteAllMessagesToFile_WhenNoFiltersProvided()
    {
        // Arrange
        var queue = GetQueue("dump-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"msg-{i}") { Subject = "Order.Failed" },
                                         "MaxRetries");
        }

        var outputPath = TempFilePath();
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DumpDlqMessagesCommand("ignored-by-emulator",
                                                                  target,
                                                                  outputPath),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(3);
        result.Value.OutputFilePath.ShouldBe(outputPath);

        File.Exists(outputPath).ShouldBeTrue();
        var json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        var messages = JsonSerializer.Deserialize<List<ServiceBusMessageDto>>(json, JsonOptions);
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(3);
        messages.ShouldAllBe(m => m.Subject == "Order.Failed");
        messages.ShouldAllBe(m => m.DeadLetterReason == "MaxRetries");
    }

    [Fact]
    public async Task WriteOnlyMatchingMessages_WhenCategoryFilterProvided()
    {
        // Arrange
        var queue = GetQueue("dump-filtered");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        for (var i = 0; i < 2; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"order-{i}") { Subject = "OrderFailed" },
                                         "MaxRetries");
        }

        for (var i = 0; i < 3; i++)
        {
            await DeadLetterMessageAsync(target,
                                         new ServiceBusMessage($"payment-{i}") { Subject = "PaymentError" },
                                         "Expired");
        }

        var categoryFilter = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var outputPath = TempFilePath();
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DumpDlqMessagesCommand("ignored-by-emulator",
                                                                  target,
                                                                  outputPath,
                                                                  CategoryFilter:categoryFilter),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(2);

        var json = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
        var messages = JsonSerializer.Deserialize<List<ServiceBusMessageDto>>(json, JsonOptions);
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(2);
        messages.ShouldAllBe(m => m.Subject == "OrderFailed");
    }

    [Fact]
    public async Task ReturnZeroCount_WhenDlqIsEmpty()
    {
        // Arrange
        var queue = GetQueue("dump-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var outputPath = TempFilePath();
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DumpDlqMessagesCommand("ignored-by-emulator",
                                                                  target,
                                                                  outputPath),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.MessageCount.ShouldBe(0);
        File.Exists(outputPath).ShouldBeFalse();
    }
}
