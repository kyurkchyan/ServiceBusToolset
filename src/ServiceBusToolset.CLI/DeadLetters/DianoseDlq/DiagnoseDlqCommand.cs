using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;
using ServiceBusToolset.CLI.DeadLetters.DianoseDlq.AppInsights;

namespace ServiceBusToolset.CLI.DeadLetters.DianoseDlq;

public class DiagnoseDlqCommand(IServiceBusClientFactory clientFactory,
                                IConsoleOutput output,
                                IDlqCategoryAnalyzer categoryAnalyzer,
                                IAppInsightsService appInsightsService) : BaseCommand<DiagnoseDlqCliCommand>(clientFactory, output), ICommand<DiagnoseDlqCliCommand>
{
    private const int MaxBatchSize = 100;
    private const int EmptyBatchThreshold = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<int> ExecuteAsync(DiagnoseDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
    {
        var validationError = cliCommand.Validate();
        if (validationError != null)
        {
            Output.Error(validationError);
            return 1;
        }

        var entityDescription = GetEntityDescription(cliCommand.Queue, cliCommand.Topic, cliCommand.Subscription);

        try
        {
            // Initialize Application Insights connection
            Output.Info("Connecting to Application Insights...");
            appInsightsService.Initialize(cliCommand.AppInsightsResourceId);
            Output.Verbose($"Connected to App Insights: {cliCommand.AppInsightsResourceId}", cliCommand.Verbose);

            await using var client = ClientFactory.CreateClient(cliCommand.Namespace);

            if (cliCommand.Interactive)
            {
                return await ExecuteInteractiveDiagnoseAsync(client,
                                                             cliCommand,
                                                             entityDescription,
                                                             cancellationToken);
            }

            return await ExecuteDiagnoseAsync(client,
                                              cliCommand,
                                              entityDescription,
                                              cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            Output.Error($"Authentication failed: {ex.Message}");
            Output.Error("Ensure you are logged in with 'az login' or have valid environment credentials.");
            return 1;
        }
        catch (ServiceBusException ex)
        {
            Output.Error($"Service Bus error: {ex.Message}");
            Output.Verbose($"Reason: {ex.Reason}", cliCommand.Verbose);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Output.Warning("\nOperation cancelled.");
            return 1;
        }
    }

    private async Task<int> ExecuteDiagnoseAsync(
        ServiceBusClient client,
        DiagnoseDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Diagnosing DLQ messages for {entityDescription}...");

        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var messages = await PeekMessagesAsync(receiver, cliCommand, cancellationToken);

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = cliCommand.BeforeEnqueueTime.Value;
            messages = messages.Where(m => m.EnqueuedTime < beforeTime).ToList();
        }

        if (messages.Count == 0)
        {
            Output.Info("No messages found matching criteria.");
            return 0;
        }

        var results = await DiagnoseMessagesAsync(messages, cliCommand, cancellationToken);

        await OutputResultsAsync(results, cliCommand, cancellationToken);

        return 0;
    }

    private async Task<int> ExecuteInteractiveDiagnoseAsync(
        ServiceBusClient client,
        DiagnoseDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Analyzing DLQ for {entityDescription}...");

        var categories = await categoryAnalyzer.AnalyzeCategoriesAsync(client,
                                                                       cliCommand.Queue,
                                                                       cliCommand.Topic,
                                                                       cliCommand.Subscription,
                                                                       Output,
                                                                       cancellationToken);

        if (categories.Count == 0)
        {
            Output.Info("No messages found in DLQ.");
            return 0;
        }

        DisplayCategoryTable(categories);

        Output.Info("");
        Console.Write("Select categories to diagnose (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        var selectedIndices = ParseSelection(input, categories.Count);
        if (selectedIndices == null)
        {
            Output.Info("Operation cancelled.");
            return 0;
        }

        if (selectedIndices.Count == 0)
        {
            Output.Warning("No valid categories selected.");
            return 0;
        }

        var selectedCategories = new HashSet<(string Label, string Reason)>();
        var totalToDiagnose = 0;
        foreach (var cat in selectedIndices.Select(idx => categories[idx]))
        {
            selectedCategories.Add((cat.Label, cat.DeadLetterReason));
            totalToDiagnose += cat.Count;
        }

        Output.Info($"Diagnosing up to {Math.Min(totalToDiagnose, cliCommand.MaxMessages)} messages from {selectedIndices.Count} categories...");

        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var allMessages = await PeekMessagesAsync(receiver, cliCommand, cancellationToken);

        var filteredMessages = allMessages
                               .Where(m =>
                               {
                                   var label = m.Subject ?? "(none)";
                                   var reason = m.DeadLetterReason ?? "(none)";
                                   return selectedCategories.Contains((label, reason));
                               })
                               .Take(cliCommand.MaxMessages)
                               .ToList();

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = cliCommand.BeforeEnqueueTime.Value;
            filteredMessages = filteredMessages.Where(m => m.EnqueuedTime < beforeTime).ToList();
        }

        var results = await DiagnoseMessagesAsync(filteredMessages, cliCommand, cancellationToken);

        await OutputResultsAsync(results, cliCommand, cancellationToken);

        return 0;
    }

    private async Task<List<ServiceBusReceivedMessage>> PeekMessagesAsync(
        ServiceBusReceiver receiver,
        DiagnoseDlqCliCommand cliCommand,
        CancellationToken cancellationToken)
    {
        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested &&
               emptyBatches < EmptyBatchThreshold &&
               allMessages.Count < cliCommand.MaxMessages)
        {
            var remaining = cliCommand.MaxMessages - allMessages.Count;
            var batchSize = Math.Min(MaxBatchSize, remaining);

            var messages = await receiver.PeekMessagesAsync(batchSize, cancellationToken:cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            allMessages.AddRange(messages);

            Output.Progress($"Peeked {allMessages.Count} messages...");
        }

        Console.WriteLine();
        return allMessages;
    }

    private async Task<List<DiagnosticResult>> DiagnoseMessagesAsync(
        List<ServiceBusReceivedMessage> messages,
        DiagnoseDlqCliCommand cliCommand,
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
                Output.Verbose($"Skipping message {message.MessageId} - no operation ID found", cliCommand.Verbose);
                continue;
            }

            // Handle duplicate operation IDs by keeping the first one
            if (!messagesByOperationId.ContainsKey(operationId))
            {
                messagesByOperationId[operationId] = message;
                operations.Add((operationId, message.EnqueuedTime));
            }
        }

        if (operations.Count == 0)
        {
            Output.Warning($"No messages with valid operation IDs found (skipped {skipped})");
            return [];
        }

        Output.Info($"Querying Application Insights for {operations.Count} messages (skipped {skipped} without operation ID)...");

        // Batch query Application Insights
        var diagnosticResults = await appInsightsService.DiagnoseBatchAsync(operations,
                                                                            (current, total) => Output.Progress($"Querying App Insights batch {current}/{total}..."),
                                                                            cancellationToken);
        Console.WriteLine();

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

        Output.Info($"Diagnosed {results.Count} messages");
        return results;
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
        if (!string.IsNullOrEmpty(message.CorrelationId))
        {
            return message.CorrelationId;
        }

        return null;
    }

    private async Task OutputResultsAsync(
        List<DiagnosticResult> results,
        DiagnoseDlqCliCommand cliCommand,
        CancellationToken cancellationToken)
    {
        // Filter to only results with actual telemetry
        var resultsWithTelemetry = results
                                   .Where(r => r.Exceptions.Count > 0 || r.Traces.Count > 0 || r.FailedDependencies.Count > 0)
                                   .ToList();

        if (resultsWithTelemetry.Count == 0)
        {
            Output.Warning("No telemetry found for any of the diagnosed messages.");
            Output.Info("This could mean:");
            Output.Info("  - The messages were processed by a service not sending telemetry to this App Insights");
            Output.Info("  - The telemetry has been purged (default retention is 90 days)");
            Output.Info("  - The operation IDs don't match the expected format");
            return;
        }

        Output.Success($"Found telemetry for {resultsWithTelemetry.Count} of {results.Count} messages");

        // Print summary to console - grouped by Subject
        PrintDiagnosticSummary(resultsWithTelemetry);

        // Write to file if specified
        if (!string.IsNullOrEmpty(cliCommand.OutputFile))
        {
            var json = JsonSerializer.Serialize(resultsWithTelemetry, JsonOptions);
            await File.WriteAllTextAsync(cliCommand.OutputFile, json, cancellationToken);
            Output.Success($"Full diagnostic results written to '{cliCommand.OutputFile}'");
        }
    }

    private void PrintDiagnosticSummary(List<DiagnosticResult> results)
    {
        Output.Info("");
        Output.Info("Diagnostic Summary by Message Type:");
        Output.Info("====================================");

        // Group by Subject (message type)
        var groupedBySubject = results
                               .GroupBy(r => r.Subject ?? "(none)")
                               .OrderByDescending(g => g.Count());

        foreach (var subjectGroup in groupedBySubject)
        {
            var messageCount = subjectGroup.Count();
            var totalExceptions = subjectGroup.Sum(r => r.Exceptions.Count);

            Output.Info("");
            Output.Info($"[{subjectGroup.Key}] - {messageCount} messages, {totalExceptions} exceptions");
            Output.Info(new string('-', 60));

            // Get exceptions for this subject, grouped by type only
            var exceptionGroups = subjectGroup
                                  .SelectMany(r => r.Exceptions)
                                  .GroupBy(e => e.ExceptionType ?? "(unknown)")
                                  .OrderByDescending(g => g.Count())
                                  .Take(5)
                                  .ToList();

            if (exceptionGroups.Count > 0)
            {
                var headers = new[]
                {
                    "Count",
                    "Exception Type",
                    "Sample Message"
                };
                var rows = exceptionGroups.Select(g => new[]
                {
                    g.Count().ToString(),
                    g.Key,
                    GetExceptionMessage(g.First())
                });
                Output.Table(headers, rows);
            }
            else
            {
                Output.Info("  No exceptions found (check traces/dependencies in output file)");
            }

            // Show failed dependencies if any
            var dependencyGroups = subjectGroup
                                   .SelectMany(r => r.FailedDependencies)
                                   .GroupBy(d => new
                                   {
                                       d.Type,
                                       d.Target
                                   })
                                   .OrderByDescending(g => g.Count())
                                   .Take(3);

            if (dependencyGroups.Any())
            {
                Output.Info("");
                Output.Info("  Failed Dependencies:");
                foreach (var dep in dependencyGroups)
                {
                    Output.Info($"    - [{dep.Count()}x] {dep.Key.Type}: {TruncateString(dep.Key.Target ?? "", 40)}");
                }
            }
        }

        // Overall summary
        Output.Info("");
        Output.Info("Overall Top Exceptions:");
        Output.Info("=======================");

        var allExceptions = results
                            .SelectMany(r => r.Exceptions)
                            .GroupBy(e => e.ExceptionType ?? "(unknown)")
                            .OrderByDescending(g => g.Count())
                            .Take(10)
                            .ToList();

        if (allExceptions.Count > 0)
        {
            var headers = new[]
            {
                "Count",
                "Type",
                "Sample Message"
            };
            var rows = allExceptions.Select(g => new[]
            {
                g.Count().ToString(),
                g.Key,
                GetExceptionMessage(g.First())
            });
            Output.Table(headers, rows);
        }
    }

    private void DisplayCategoryTable(IReadOnlyCollection<DlqCategory> categories)
    {
        Output.Info("");
        Output.Info("Dead Letter Summary:");

        var headers = new[]
        {
            "#",
            "Label",
            "DeadLetterReason",
            "Count"
        };
        var rows = categories.Select((cat, index) => new[]
        {
            (index + 1).ToString(),
            cat.Label.ReplaceLineEndings(" "),
            cat.DeadLetterReason.ReplaceLineEndings(" "),
            cat.Count.ToString()
        });

        Output.Table(headers, rows);

        var total = categories.Sum(c => c.Count);
        Output.Info($"Total: {total} messages");
    }

    private static List<int>? ParseSelection(string? input, int maxIndex)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().ToLowerInvariant();

        if (trimmed == "q" || trimmed == "quit")
        {
            return null;
        }

        if (trimmed == "all" || trimmed == "a")
        {
            return Enumerable.Range(0, maxIndex).ToList();
        }

        var indices = new List<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', 2);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        var idx = i - 1;
                        if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                        {
                            indices.Add(idx);
                        }
                    }
                }
            }
            else if (int.TryParse(part, out var num))
            {
                var idx = num - 1;
                if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                {
                    indices.Add(idx);
                }
            }
        }

        return indices;
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

    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    private static string GetExceptionMessage(ExceptionInfo ex)
    {
        // Prefer innermostMessage, fall back to outerMessage
        if (!string.IsNullOrWhiteSpace(ex.InnermostMessage))
        {
            return ex.InnermostMessage;
        }

        if (!string.IsNullOrWhiteSpace(ex.OuterMessage))
        {
            return ex.OuterMessage;
        }

        return "(no message)";
    }
}
