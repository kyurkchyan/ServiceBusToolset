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
        var command = new DiagnoseFromCacheCommand("test-resource", []);

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

        var command = new DiagnoseFromCacheCommand("test-resource", messages);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.Count.ShouldBe(1);
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(ops => ops.Any(o => o.OperationId == traceId)),
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

        var command = new DiagnoseFromCacheCommand("test-resource", messages);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedNoOperationId.ShouldBe(1);
        result.Value.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task InitializeAppInsights_WhenHandled()
    {
        // Arrange
        var command = new DiagnoseFromCacheCommand("test-resource", []);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockAppInsights.Received(1).Initialize("test-resource");
    }

    [Fact]
    public async Task NotInitializeAppInsightsService_WhenAppInsightsResourceIdIsNull()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId("abc123def456abc123def456abc12345")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        var command = new DiagnoseFromCacheCommand(null, messages);

        // Act
        await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        _mockAppInsights.DidNotReceive().Initialize(Arg.Any<string>());
    }

    [Fact]
    public async Task ReturnResultForEveryMessage_WhenAppInsightsResourceIdIsNull()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-with-opid")
                                            .WithDiagnosticId("abc123def456abc123def456abc12345")
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-without-opid")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        var command = new DiagnoseFromCacheCommand(null, messages);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.Count.ShouldBe(2);
        result.Value.SkippedNoOperationId.ShouldBe(0);
        result.Value.TotalProcessed.ShouldBe(2);
    }

    private void SetupAppInsightsResponse(string operationId)
    {
        var results = new Dictionary<string, DiagnosticResult> { [operationId] = new() { OperationId = operationId } };

        _mockAppInsights.DiagnoseBatchAsync(Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
                                            Arg.Any<Action<int, int>?>(),
                                            Arg.Any<CancellationToken>())
                        .Returns(results);
    }
}
