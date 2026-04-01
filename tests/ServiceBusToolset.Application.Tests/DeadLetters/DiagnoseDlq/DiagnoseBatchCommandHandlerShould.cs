using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DiagnoseDlq;

public class DiagnoseBatchCommandHandlerShould
{
    private readonly IAppInsightsService _mockAppInsights;
    private readonly DiagnoseBatchCommandHandler _handler;

    public DiagnoseBatchCommandHandlerShould()
    {
        _mockAppInsights = Substitute.For<IAppInsightsService>();
        _handler = new DiagnoseBatchCommandHandler(_mockAppInsights);
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoOperations()
    {
        var command = new DiagnoseBatchCommand("test-resource", []);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitializeAppInsightsService()
    {
        var operations = new List<OperationInfo>
        {
            new("op-1", DateTimeOffset.UtcNow, "msg-1", "Subject1", "Reason1")
        };

        SetupAppInsightsResponse("op-1");

        var command = new DiagnoseBatchCommand("my-app-insights-resource", operations);
        await _handler.Handle(command, CancellationToken.None);

        _mockAppInsights.Received(1).Initialize("my-app-insights-resource");
    }

    [Fact]
    public async Task ReturnDiagnosticResults_WithEnrichedMessageInfo()
    {
        var operationId = "abc123def456abc123def456abc12345";
        var enqueuedTime = DateTimeOffset.UtcNow;
        var operations = new List<OperationInfo>
        {
            new(operationId, enqueuedTime, "msg-1", "OrderCreated", "MaxDeliveryCountExceeded")
        };

        var appInsightsResults = new Dictionary<string, DiagnosticResult>
        {
            [operationId] = new()
            {
                OperationId = operationId,
                Exceptions = [new ExceptionInfo { ExceptionType = "TestException", OuterMessage = "Something failed" }]
            }
        };

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(appInsightsResults);

        var command = new DiagnoseBatchCommand("test-resource", operations);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);

        var diagnostic = result.Value[0];
        diagnostic.MessageId.ShouldBe("msg-1");
        diagnostic.Subject.ShouldBe("OrderCreated");
        diagnostic.DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        diagnostic.Exceptions.Count.ShouldBe(1);
        diagnostic.Exceptions[0].ExceptionType.ShouldBe("TestException");
    }

    [Fact]
    public async Task HandleMultipleOperations()
    {
        var operations = new List<OperationInfo>
        {
            new("op-1", DateTimeOffset.UtcNow, "msg-1", "Subject1", "Reason1"),
            new("op-2", DateTimeOffset.UtcNow, "msg-2", "Subject2", "Reason2"),
            new("op-3", DateTimeOffset.UtcNow, "msg-3", "Subject3", "Reason3")
        };

        var appInsightsResults = new Dictionary<string, DiagnosticResult>
        {
            ["op-1"] = new() { OperationId = "op-1", Exceptions = [new ExceptionInfo { ExceptionType = "Ex1" }] },
            ["op-2"] = new() { OperationId = "op-2" },
            ["op-3"] = new() { OperationId = "op-3", FailedDependencies = [new DependencyInfo { Type = "HTTP" }] }
        };

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(appInsightsResults);

        var command = new DiagnoseBatchCommand("test-resource", operations);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(3);
        result.Value.First(r => r.MessageId == "msg-1").Exceptions.Count.ShouldBe(1);
        result.Value.First(r => r.MessageId == "msg-3").FailedDependencies.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PassCorrectOperationsToAppInsights()
    {
        var enqueuedTime = new DateTimeOffset(2026, 3, 20, 8, 0, 0, TimeSpan.Zero);
        var operations = new List<OperationInfo>
        {
            new("op-abc", enqueuedTime, "msg-1", "Subject1", "Reason1")
        };

        SetupAppInsightsResponse("op-abc");

        var command = new DiagnoseBatchCommand("test-resource", operations);
        await _handler.Handle(command, CancellationToken.None);

        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Count == 1 && ops[0].OperationId == "op-abc" && ops[0].EnqueuedTime == enqueuedTime),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    private void SetupAppInsightsResponse(string operationId)
    {
        var results = new Dictionary<string, DiagnosticResult>
        {
            [operationId] = new() { OperationId = operationId }
        };

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(results);
    }
}
