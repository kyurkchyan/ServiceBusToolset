using NSubstitute;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Application.Tests.Common.Builders;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.DiagnoseDlq;

public class DiagnoseDlqCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly IAppInsightsService _mockAppInsights;
    private readonly DiagnoseDlqCommandHandler _handler;

    public DiagnoseDlqCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _mockAppInsights = Substitute.For<IAppInsightsService>();
        _handler = new DiagnoseDlqCommandHandler(_mockFactory.Object, _mockAppInsights);
    }

    [Fact]
    public async Task ReturnEmptyResult_WhenNoMessages()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.ShouldBeEmpty();
        result.Value.TotalProcessed.ShouldBe(0);
        result.Value.SkippedNoOperationId.ShouldBe(0);
    }

    [Fact]
    public async Task InitializeAppInsightsService()
    {
        // Arrange
        _mockFactory.WithNoMessages();

        var command = CreateCommand();

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _mockAppInsights.Received(1).Initialize("test-app-insights-resource");
    }

    [Fact]
    public async Task ExtractOperationIdFromDiagnosticId()
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

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(traceId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Any(o => o.OperationId == traceId)),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractOperationIdFromTraceparent()
    {
        // Arrange
        var traceId = "traceparent123456traceparent1234";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithTraceparent(traceId)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(traceId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Any(o => o.OperationId == traceId)),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractOperationIdFromOperationIdProperty()
    {
        // Arrange
        var operationId = "direct-operation-id";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithOperationId(operationId)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(operationId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Any(o => o.OperationId == operationId)),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FallbackToCorrelationId_WhenNoOtherOperationId()
    {
        // Arrange
        var correlationId = "correlation-id-fallback";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithCorrelationId(correlationId)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(correlationId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Any(o => o.OperationId == correlationId)),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipMessages_WhenNoOperationId()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-no-opid")
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.SkippedNoOperationId.ShouldBe(1);
        result.Value.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyTimeFilter_WhenBeforeTimeProvided()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;
        // Use 32-char hex string (no dashes) for W3C trace context format
        var oldTraceId = "0123456789abcdef0123456789abcdef";

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("old-msg")
                                            .WithDiagnosticId(oldTraceId)
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("new-msg")
                                            .WithDiagnosticId("fedcba9876543210fedcba9876543210")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(oldTraceId);

        var command = CreateCommand(beforeTime: cutoffTime);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
    }

    [Fact]
    public async Task ApplyCategoryFilter_WhenCategoryFilterProvided()
    {
        // Arrange
        // Use 32-char hex string (no dashes) for W3C trace context format
        var matchingTraceId = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("matching-msg")
                                            .WithSubject("OrderProcessor")
                                            .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                            .WithDiagnosticId(matchingTraceId)
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("non-matching-msg")
                                            .WithSubject("PaymentHandler")
                                            .WithDeadLetterReason("TimeoutExceeded")
                                            .WithDiagnosticId("b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5")
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(matchingTraceId);

        var categoryFilter = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded")
        };

        var command = CreateCommand(categoryFilter: categoryFilter);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalProcessed.ShouldBe(1);
    }

    [Fact]
    public async Task EnrichResultsWithMessageInfo()
    {
        // Arrange
        // Use 32-char hex string (no dashes) for W3C trace context format
        var traceId = "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6";
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("test-msg-id")
                                            .WithSubject("TestSubject")
                                            .WithDeadLetterReason("TestReason")
                                            .WithBody("{\"key\": \"value\"}")
                                            .WithContentType("application/json")
                                            .WithDiagnosticId(traceId)
                                            .WithSequenceNumber(1)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(traceId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Results.Count.ShouldBe(1);

        var diagnostic = result.Value.Results.First();
        diagnostic.MessageId.ShouldBe("test-msg-id");
        diagnostic.Subject.ShouldBe("TestSubject");
        diagnostic.DeadLetterReason.ShouldBe("TestReason");
        diagnostic.Body.ShouldNotBeNull();
    }

    [Fact]
    public async Task CountResultsWithTelemetry()
    {
        // Arrange
        // Use 32-char hex strings (no dashes) for W3C trace context format
        var traceIdWithTelemetry = "d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1";
        var traceIdNoTelemetry = "e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-with-telemetry")
                                            .WithDiagnosticId(traceIdWithTelemetry)
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-no-telemetry")
                                            .WithDiagnosticId(traceIdNoTelemetry)
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);

        var appInsightsResults = new Dictionary<string, DiagnosticResult>
        {
            [traceIdWithTelemetry] = new DiagnosticResult
            {
                OperationId = traceIdWithTelemetry,
                Exceptions = [new ExceptionInfo { ExceptionType = "TestException" }]
            },
            [traceIdNoTelemetry] = new DiagnosticResult
            {
                OperationId = traceIdNoTelemetry
            }
        };

        _mockAppInsights.DiagnoseBatchAsync(
            Arg.Any<IReadOnlyList<(string, DateTimeOffset)>>(),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>())
            .Returns(appInsightsResults);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ResultsWithTelemetry.ShouldBe(1);
    }

    [Fact]
    public async Task HandleDuplicateOperationIds()
    {
        // Arrange
        // Use 32-char hex string (no dashes) for W3C trace context format
        var sameTraceId = "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-1")
                                            .WithDiagnosticId(sameTraceId)
                                            .WithSequenceNumber(1)
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("msg-2")
                                            .WithDiagnosticId(sameTraceId)
                                            .WithSequenceNumber(2)
                                            .Build()
        };

        _mockFactory.WithMessagesToReturn(messages);
        SetupAppInsightsResponse(sameTraceId);

        var command = CreateCommand();

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        // Only one unique operation ID should be queried
        await _mockAppInsights.Received(1).DiagnoseBatchAsync(
            Arg.Is<IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)>>(
                ops => ops.Count == 1),
            Arg.Any<Action<int, int>?>(),
            Arg.Any<CancellationToken>());
    }

    private DiagnoseDlqCommand CreateCommand(
        DateTimeOffset? beforeTime = null,
        IReadOnlySet<DlqCategoryKey>? categoryFilter = null)
    {
        return new DiagnoseDlqCommand(
            "test.servicebus.windows.net",
            EntityTargetBuilder.Queue("test-queue"),
            "test-app-insights-resource",
            MaxMessages: 100,
            BeforeTime: beforeTime,
            CategoryFilter: categoryFilter);
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
