using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DiagnoseDlq;

public class DiagnoseFromCacheCommandHandlerShould
{
    private readonly IAppInsightsService _mockAppInsights;
    private readonly DiagnoseFromCacheCommandHandler _handler;

    public DiagnoseFromCacheCommandHandlerShould()
    {
        _mockAppInsights = Substitute.For<IAppInsightsService>();
        _handler = new DiagnoseFromCacheCommandHandler(_mockAppInsights);
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoMessages()
    {
        // Arrange
        var command = new DiagnoseFromCacheCommand("test-resource", 100, []);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.ShouldBeEmpty();
        result.Value.TotalProcessed.ShouldBe(0);
        result.Value.SkippedNoOperationId.ShouldBe(0);
    }

    [Fact]
    public async Task DiagnoseMessagesWithOperationId_WhenMessagesProvided()
    {
        // Arrange
        var traceId = "abc123def456abc123def456abc12345";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId(traceId)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        SetupAppInsightsResponse(traceId);

        var command = new DiagnoseFromCacheCommand("test-resource", 100, messages);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.Count.ShouldBe(1);
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Any(o => o.OperationId == traceId)),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipMessagesWithoutOperationId_WhenNoOpIdPresent()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-no-opid")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        var command = new DiagnoseFromCacheCommand("test-resource", 100, messages);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedNoOperationId.ShouldBe(1);
        result.Value.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task RespectMaxMessages_WhenMoreMessagesThanLimit()
    {
        // Arrange
        var traceId1 = "abc123def456abc123def456abc12345";
        var traceId2 = "def456abc123def456abc123def45678";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId(traceId1)
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithDiagnosticId(traceId2)
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        SetupAppInsightsResponse(traceId1);

        var command = new DiagnoseFromCacheCommand("test-resource", 1, messages);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAppInsights_WhenHandled()
    {
        // Arrange
        var command = new DiagnoseFromCacheCommand("test-resource", 100, []);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockAppInsights.Received(1).Initialize("test-resource");
    }

    private void SetupAppInsightsResponse(string operationId)
    {
        var results = new Dictionary<string, DiagnosticResult>
        {
            [operationId] = new DiagnosticResult { OperationId = operationId }
        };

        _mockAppInsights.DiagnoseBatchAsync(
            Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>())
            .Returns(results);
    }
}
