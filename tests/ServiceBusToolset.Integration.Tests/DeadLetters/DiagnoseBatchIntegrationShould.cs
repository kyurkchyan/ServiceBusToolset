using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class DiagnoseBatchIntegrationShould : BaseIntegrationTest
{
    private readonly IAppInsightsService _mockAppInsights;

    public DiagnoseBatchIntegrationShould(ServiceBusEmulatorFixture fixture)
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
    public async Task ReturnDiagnosticResults_ForOperationBatch()
    {
        var operationId = "abc123def456abc123def456abc12345";
        var enqueuedTime = DateTimeOffset.UtcNow;

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(callInfo =>
                        {
                            var ops = callInfo.ArgAt<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(0);
                            var results = new Dictionary<string, DiagnosticResult>();
                            foreach (var (opId, _) in ops)
                            {
                                results[opId] = new DiagnosticResult
                                {
                                    OperationId = opId,
                                    Exceptions =
                                    [
                                        new ExceptionInfo
                                        {
                                            ExceptionType = "TestException",
                                            OuterMessage = "Test error"
                                        }
                                    ]
                                };
                            }

                            return results;
                        });

        var operations = new List<OperationInfo>
        {
            new(operationId,
                enqueuedTime,
                "msg-1",
                "OrderCreated",
                "MaxDeliveryCountExceeded")
        };

        var sender = CreateSender();

        var result = await sender.Send(new DiagnoseBatchCommand("test-resource", operations),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].MessageId.ShouldBe("msg-1");
        result.Value[0].Subject.ShouldBe("OrderCreated");
        result.Value[0].DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        result.Value[0].Exceptions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReturnEmptyResults_WhenNoOperations()
    {
        var sender = CreateSender();

        var result = await sender.Send(new DiagnoseBatchCommand("test-resource", []),
                                       TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }
}
