using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class DiagnoseDlqIntegrationShould : BaseIntegrationTest
{
    private readonly IAppInsightsService _mockAppInsights;

    public DiagnoseDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
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
            // Replace the real IAppInsightsService with mock
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAppInsightsService));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(m);
        };
    }

    [Fact]
    public async Task ReturnDiagnosticResults_WhenMessagesHaveDiagnosticId()
    {
        // Arrange
        var queue = GetQueue("diag-with-id");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var msg = new ServiceBusMessage("test-body")
        {
            Subject = "Order.Failed",
            ApplicationProperties = { ["Diagnostic-Id"] = "00-abc123def456-0123456789ab-01" }
        };

        await DeadLetterMessageAsync(target, msg, "ProcessingFailed");

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

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DiagnoseDlqCommand("ignored-by-emulator",
                                                              target,
                                                              "test-resource",
                                                              10),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(0);
        result.Value.ResultsWithTelemetry.ShouldBe(1);
        result.Value.Results.Count.ShouldBe(1);
        result.Value.Results[0].OperationId.ShouldBe("abc123def456");
        result.Value.Results[0].Subject.ShouldBe("Order.Failed");
    }

    [Fact]
    public async Task SkipMessages_WhenNoOperationIdPresent()
    {
        // Arrange
        var queue = GetQueue("diag-no-id");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        // Message without any operation ID properties
        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("no-op-id") { Subject = "Event.Error" },
                                     "NoHandler");

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(new Dictionary<string, DiagnosticResult>());

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DiagnoseDlqCommand("ignored-by-emulator",
                                                              target,
                                                              "test-resource",
                                                              10),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
        result.Value.SkippedNoOperationId.ShouldBe(1);
        result.Value.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnEmptyResults_WhenDlqIsEmpty()
    {
        // Arrange
        var queue = GetQueue("diag-empty");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DiagnoseDlqCommand("ignored-by-emulator",
                                                              target,
                                                              "test-resource",
                                                              10),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(0);
        result.Value.SkippedNoOperationId.ShouldBe(0);
        result.Value.Results.ShouldBeEmpty();
    }
}
