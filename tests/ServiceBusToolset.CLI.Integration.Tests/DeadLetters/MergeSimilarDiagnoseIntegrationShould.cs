using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.CLI.Integration.Tests.Infrastructure;
using ServiceBusToolset.IntegrationTesting;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.CLI.Integration.Tests.DeadLetters;

public class MergeSimilarDiagnoseIntegrationShould : BaseIntegrationTest
{
    private readonly IAppInsightsService _mockAppInsights;

    public MergeSimilarDiagnoseIntegrationShould(ServiceBusEmulatorFixture fixture)
        : base(fixture, ConfigureMock(out var mock))
    {
        _mockAppInsights = mock;
    }

    private static Action<IServiceCollection> ConfigureMock(out IAppInsightsService mock)
    {
        var m = Substitute.For<IAppInsightsService>();
        mock = m;
        return services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAppInsightsService));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(m);
        };
    }

    [Fact]
    public async Task DiagnoseSelectedMergedCategory_WhenSingleCategoryChosen()
    {
        // Arrange
        var queue = GetQueue("merge-diag-single");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // Group A: 3 messages with similar subjects
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("alice-body")
                                     {
                                         Subject = "Error processing user Alice",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("bob-body")
                                     {
                                         Subject = "Error processing user Bob",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("charlie-body")
                                     {
                                         Subject = "Error processing user Charlie",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3-0123456789ab-01" }
                                     },
                                     "MaxRetries");

        // Group B: 5 messages with similar subjects
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("order-body")
                                     {
                                         Subject = "Timeout for service OrderAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("payment-body")
                                     {
                                         Subject = "Timeout for service PaymentAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("inventory-body")
                                     {
                                         Subject = "Timeout for service InventoryAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("shipping-body")
                                     {
                                         Subject = "Timeout for service ShippingAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7a7-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("notification-body")
                                     {
                                         Subject = "Timeout for service NotificationAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-b8b8b8b8b8b8b8b8b8b8b8b8b8b8b8b8-0123456789ab-01" }
                                     },
                                     "MaxRetries");

        await WaitForDlqCountAsync(target, 8, TestContext.Current.CancellationToken);

        SetupAppInsightsResponseForAll();

        var mockOutput = Substitute.For<IConsoleOutput>();
        // "1" selects the first merged category (sorted by count desc -> 5-message "Timeout" group)
        mockOutput.ReadLine().Returns("1");

        var sender = CreateSender();
        var handler = new DiagnoseDlqCommandHandler(sender, mockOutput);

        var command = new DiagnoseDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = queue,
            AppInsightsResourceId = "test-resource",
            Interactive = true,
            MergeSimilar = true
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        // Should have diagnosed 5 messages from the "Timeout" group
        mockOutput.Received().Info(Arg.Is<string>(s => s.Contains("5") && s.Contains("Diagnosing")));
    }

    [Fact]
    public async Task DiagnoseAllMessages_WhenAllMergedCategoriesSelected()
    {
        // Arrange
        var queue = GetQueue("merge-diag-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // Group A: 3 messages with similar subjects
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("alice-body")
                                     {
                                         Subject = "Error processing user Alice",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-aa11aa11aa11aa11aa11aa11aa11aa11-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("bob-body")
                                     {
                                         Subject = "Error processing user Bob",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-bb22bb22bb22bb22bb22bb22bb22bb22-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("charlie-body")
                                     {
                                         Subject = "Error processing user Charlie",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-cc33cc33cc33cc33cc33cc33cc33cc33-0123456789ab-01" }
                                     },
                                     "MaxRetries");

        // Group B: 5 messages with similar subjects
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("order-body")
                                     {
                                         Subject = "Timeout for service OrderAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-dd44dd44dd44dd44dd44dd44dd44dd44-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("payment-body")
                                     {
                                         Subject = "Timeout for service PaymentAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-ee55ee55ee55ee55ee55ee55ee55ee55-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("inventory-body")
                                     {
                                         Subject = "Timeout for service InventoryAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-ff66ff66ff66ff66ff66ff66ff66ff66-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("shipping-body")
                                     {
                                         Subject = "Timeout for service ShippingAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-aa77aa77aa77aa77aa77aa77aa77aa77-0123456789ab-01" }
                                     },
                                     "MaxRetries");
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("notification-body")
                                     {
                                         Subject = "Timeout for service NotificationAPI",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-bb88bb88bb88bb88bb88bb88bb88bb88-0123456789ab-01" }
                                     },
                                     "MaxRetries");

        await WaitForDlqCountAsync(target, 8, TestContext.Current.CancellationToken);

        SetupAppInsightsResponseForAll();

        var mockOutput = Substitute.For<IConsoleOutput>();
        mockOutput.ReadLine().Returns("all");

        var sender = CreateSender();
        var handler = new DiagnoseDlqCommandHandler(sender, mockOutput);

        var command = new DiagnoseDlqCliCommand
        {
            Namespace = "ignored-by-emulator",
            Queue = queue,
            AppInsightsResourceId = "test-resource",
            Interactive = true,
            MergeSimilar = true
        };

        // Act
        var exitCode = await handler.ExecuteAsync(command, false, TestContext.Current.CancellationToken);

        // Assert
        exitCode.ShouldBe(0);
        // Should have diagnosed all 8 messages
        mockOutput.Received().Info(Arg.Is<string>(s => s.Contains("8") && s.Contains("Diagnosing")));
    }

    private void SetupAppInsightsResponseForAll()
    {
        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            var ops = callInfo.ArgAt<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(0);
                            var results = new Dictionary<string, DiagnosticResult>();
                            foreach (var (opId, enqueuedTime) in ops)
                            {
                                results[opId] = new DiagnosticResult
                                {
                                    OperationId = opId,
                                    EnqueuedTime = enqueuedTime,
                                    Exceptions = [new ExceptionInfo { OuterMessage = "Test exception" }]
                                };
                            }

                            return results;
                        });
    }
}
