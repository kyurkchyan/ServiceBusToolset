using System.Text;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common;

internal static class MessageDiagnostics
{
    public static async Task<(List<DiagnosticResult> Results, int Skipped)> DiagnoseMessagesAsync(
        IAppInsightsService appInsightsService,
        IReadOnlyList<ServiceBusReceivedMessage> messages,
        IProgress<(int Current, int Total)>? batchProgress,
        CancellationToken cancellationToken)
    {
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

            if (messagesByOperationId.TryAdd(operationId, message))
            {
                operations.Add((operationId, message.EnqueuedTime));
            }
        }

        if (operations.Count == 0)
        {
            return ([], skipped);
        }

        var diagnosticResults = await appInsightsService.DiagnoseBatchAsync(operations,
                                                                            (current, total) => batchProgress?.Report((current, total)),
                                                                            cancellationToken);

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

    public static string? ExtractOperationId(ServiceBusReceivedMessage message)
    {
        if (message.ApplicationProperties.TryGetValue("Diagnostic-Id", out var diagnosticId) &&
            diagnosticId is string diagStr)
        {
            var parts = diagStr.Split('-');
            if (parts.Length >= 2)
            {
                return parts[1];
            }
        }

        if (message.ApplicationProperties.TryGetValue("traceparent", out var traceparent) &&
            traceparent is string tpStr)
        {
            var parts = tpStr.Split('-');
            if (parts.Length >= 2)
            {
                return parts[1];
            }
        }

        if (message.ApplicationProperties.TryGetValue("Operation-Id", out var opId) &&
            opId is string opIdStr)
        {
            return opIdStr;
        }

        return !string.IsNullOrEmpty(message.CorrelationId) ? message.CorrelationId : null;
    }

    public static JsonNode? TryDecodeBody(ServiceBusReceivedMessage msg)
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
