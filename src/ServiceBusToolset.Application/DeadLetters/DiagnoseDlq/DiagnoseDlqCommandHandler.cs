using System.Text;
using System.Text.Json.Nodes;
using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed class DiagnoseDlqCommandHandler(IServiceBusClientFactory clientFactory,
                                              IAppInsightsService appInsightsService) : ICommandHandler<DiagnoseDlqCommand, Result<DiagnoseDlqResult>>
{
    public async ValueTask<Result<DiagnoseDlqResult>> Handle(
        DiagnoseDlqCommand command,
        CancellationToken cancellationToken)
    {
        // Initialize Application Insights connection
        appInsightsService.Initialize(command.AppInsightsResourceId);

        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        // Peek messages
        var messages = await MessageOperations.PeekAsync(receiver,
                                                         command.MaxMessages,
                                                         progress:command.Progress,
                                                         cancellationToken:cancellationToken);

        // Apply time filter
        var filteredMessages = MessageFilters.FilterByEnqueueTime(messages, command.BeforeTime).ToList();

        // Apply category filter
        if (command.CategoryFilter is { Count: > 0 })
        {
            filteredMessages = DlqMessageService.FilterByCategories(filteredMessages, command.CategoryFilter).ToList();
        }

        // Limit to max messages
        filteredMessages = filteredMessages.Take(command.MaxMessages).ToList();

        if (filteredMessages.Count == 0)
        {
            return Result.Success(new DiagnoseDlqResult([],
                                                        0,
                                                        0,
                                                        0));
        }

        // Diagnose messages
        var (results, skipped) = await DiagnoseMessagesAsync(filteredMessages, command.BatchProgress, cancellationToken);

        var resultsWithTelemetry = results
            .Count(r => r.Exceptions.Count > 0 || r.Traces.Count > 0 || r.FailedDependencies.Count > 0);

        return Result.Success(new DiagnoseDlqResult(results,
                                                    filteredMessages.Count,
                                                    skipped,
                                                    resultsWithTelemetry));
    }

    private async Task<(List<DiagnosticResult> Results, int Skipped)> DiagnoseMessagesAsync(
        List<ServiceBusReceivedMessage> messages,
        IProgress<(int Current, int Total)>? batchProgress,
        CancellationToken cancellationToken)
    {
        // Extract operation IDs and build mapping to messages
        var messagesByOperationId = new Dictionary<string, ServiceBusReceivedMessage>();
        var operations = new List<(string OperationId, DateTimeOffset EnqueuedTime)>();
        var skipped = 0;

        foreach (var message in messages)
        {
            var operationId = ExtractOperationId(message);
            if (string.IsNullOrEmpty(operationId))
            {
                skipped++;
                continue;
            }

            // Handle duplicate operation IDs by keeping the first one
            if (messagesByOperationId.TryAdd(operationId, message))
            {
                operations.Add((operationId, message.EnqueuedTime));
            }
        }

        if (operations.Count == 0)
        {
            return ([], skipped);
        }

        // Batch query Application Insights
        var diagnosticResults = await appInsightsService.DiagnoseBatchAsync(operations,
                                                                            (current, total) => batchProgress?.Report((current, total)),
                                                                            cancellationToken);

        // Enrich results with message info
        var results = new List<DiagnosticResult>();
        foreach (var (operationId, result) in diagnosticResults)
        {
            if (messagesByOperationId.TryGetValue(operationId, out var message))
            {
                result.MessageId = message.MessageId;
                result.Subject = message.Subject;
                result.DeadLetterReason = message.DeadLetterReason;
                result.Body = TryDecodeBody(message);
                results.Add(result);
            }
        }

        return (results, skipped);
    }

    private static string? ExtractOperationId(ServiceBusReceivedMessage message)
    {
        // Try to extract from Diagnostic-Id (W3C trace context format)
        if (message.ApplicationProperties.TryGetValue("Diagnostic-Id", out var diagnosticId) &&
            diagnosticId is string diagStr)
        {
            // Format: 00-{traceId}-{spanId}-{flags}
            var parts = diagStr.Split('-');
            if (parts.Length >= 2)
            {
                return parts[1]; // traceId is the operation_Id
            }
        }

        // Try traceparent header
        if (message.ApplicationProperties.TryGetValue("traceparent", out var traceparent) &&
            traceparent is string tpStr)
        {
            var parts = tpStr.Split('-');
            if (parts.Length >= 2)
            {
                return parts[1];
            }
        }

        // Try Operation-Id directly
        if (message.ApplicationProperties.TryGetValue("Operation-Id", out var opId) &&
            opId is string opIdStr)
        {
            return opIdStr;
        }

        // Fall back to CorrelationId
        return !string.IsNullOrEmpty(message.CorrelationId) ? message.CorrelationId : null;
    }

    private static JsonNode? TryDecodeBody(ServiceBusReceivedMessage msg)
    {
        string? text = null;

        if (msg.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
            msg.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                text = msg.Body.ToString();
            }
            catch
            {
                // Fall through
            }
        }

        if (text == null)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(msg.Body.ToArray());
                if (!decoded.Contains('\uFFFD'))
                {
                    text = decoded;
                }
            }
            catch
            {
                // Fall through to Base64
            }
        }

        if (text != null)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch
            {
                return JsonValue.Create(text);
            }
        }

        return JsonValue.Create(Convert.ToBase64String(msg.Body.ToArray()));
    }
}
