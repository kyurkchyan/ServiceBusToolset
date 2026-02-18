using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class DiagnoseFromCacheIntegrationShould : BaseIntegrationTest
{
    private readonly IAppInsightsService _mockAppInsights;

    public DiagnoseFromCacheIntegrationShould(ServiceBusEmulatorFixture fixture)
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
    public async Task DiagnoseSelectedMessages_WhenCategoriesSelected()
    {
        // Arrange
        var queue = GetQueue("cache-diag-sel");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);

        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("order-body")
                                     {
                                         Subject = "OrderFailed",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-aaa111bbb222ccc333ddd444eee55566-0123456789ab-01" }
                                     },
                                     "MaxRetries");

        await DeadLetterMessageAsync(target,
                                     new ServiceBusMessage("payment-body")
                                     {
                                         Subject = "PaymentError",
                                         ApplicationProperties = { ["Diagnostic-Id"] = "00-bbb222ccc333ddd444eee55566fff777-0123456789ab-01" }
                                     },
                                     "Expired");

        await WaitForDlqCountAsync(target, 2, TestContext.Current.CancellationToken);

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

        // Stream to build cache
        var streamResult = await sender.Send(new StreamDlqForDiagnoseCommand("ignored-by-emulator", target),
                                             TestContext.Current.CancellationToken);
        streamResult.IsSuccess.ShouldBeTrue();

        using var session = streamResult.Value;
        await WaitForSessionComplete(session);

        // Select only OrderFailed category
        var selectedKeys = new HashSet<DlqCategoryKey> { new("OrderFailed", "MaxRetries") };
        var messagesToDiagnose = session.SnapshotForCategories(selectedKeys);
        messagesToDiagnose.Count.ShouldBe(1);

        // Act
        var result = await sender.Send(new DiagnoseFromCacheCommand("test-resource",
                                                                     100,
                                                                     messagesToDiagnose),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
        result.Value.ResultsWithTelemetry.ShouldBe(1);
        result.Value.Results.Count.ShouldBe(1);
        result.Value.Results[0].Subject.ShouldBe("OrderFailed");
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoCachedMessages()
    {
        // Arrange
        var sender = CreateSender();

        // Act
        var result = await sender.Send(new DiagnoseFromCacheCommand("test-resource", 100, []),
                                       TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(0);
        result.Value.Results.ShouldBeEmpty();
    }

    private static async Task WaitForSessionComplete(DlqScanSession session, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!session.Cache.IsComplete && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(100);
        }

        if (!session.Cache.IsComplete)
        {
            throw new TimeoutException("Session cache did not complete within timeout.");
        }
    }
}
