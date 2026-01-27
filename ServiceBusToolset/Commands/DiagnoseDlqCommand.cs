using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Models;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;

namespace ServiceBusToolset.Commands;

public class DiagnoseDlqCommand(IServiceBusClientFactory clientFactory,
                                IConsoleOutput output,
                                IDlqCategoryAnalyzer categoryAnalyzer,
                                IAppInsightsService appInsightsService) : BaseCommand<DiagnoseDlqOptions>(clientFactory, output), ICommand<DiagnoseDlqOptions>
{
    private const int MaxBatchSize = 100;
    private const int EmptyBatchThreshold = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<int> ExecuteAsync(DiagnoseDlqOptions options, CancellationToken cancellationToken = default)
    {
        var validationError = options.Validate();
        if (validationError != null)
        {
            Output.Error(validationError);
            return 1;
        }

        var entityDescription = GetEntityDescription(options.Queue, options.Topic, options.Subscription);

        try
        {
            // Initialize Application Insights connection
            Output.Info("Connecting to Application Insights...");
            appInsightsService.Initialize(options.AppInsightsResourceId);
            Output.Verbose($"Connected to App Insights: {options.AppInsightsResourceId}", options.Verbose);

            await using var client = ClientFactory.CreateClient(options.Namespace);

            if (options.Interactive)
            {
                return await ExecuteInteractiveDiagnoseAsync(client,
                                                             options,
                                                             entityDescription,
                                                             cancellationToken);
            }

            return await ExecuteDiagnoseAsync(client,
                                              options,
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
            Output.Verbose($"Reason: {ex.Reason}", options.Verbose);
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
        DiagnoseDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Diagnosing DLQ messages for {entityDescription}...");

        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var messages = await PeekMessagesAsync(receiver, options, cancellationToken);

        if (options.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = options.BeforeEnqueueTime.Value;
            messages = messages.Where(m => m.EnqueuedTime < beforeTime).ToList();
        }

        if (messages.Count == 0)
        {
            Output.Info("No messages found matching criteria.");
            return 0;
        }

        var results = await DiagnoseMessagesAsync(messages, options, cancellationToken);

        await OutputResultsAsync(results, options, cancellationToken);

        return 0;
    }

    private async Task<int> ExecuteInteractiveDiagnoseAsync(
        ServiceBusClient client,
        DiagnoseDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Analyzing DLQ for {entityDescription}...");

        var categories = await categoryAnalyzer.AnalyzeCategoriesAsync(client,
                                                                       options.Queue,
                                                                       options.Topic,
                                                                       options.Subscription,
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

        Output.Info($"Diagnosing up to {Math.Min(totalToDiagnose, options.MaxMessages)} messages from {selectedIndices.Count} categories...");

        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var allMessages = await PeekMessagesAsync(receiver, options, cancellationToken);

        var filteredMessages = allMessages
                               .Where(m =>
                               {
                                   var label = m.Subject ?? "(none)";
                                   var reason = m.DeadLetterReason ?? "(none)";
                                   return selectedCategories.Contains((label, reason));
                               })
                               .Take(options.MaxMessages)
                               .ToList();

        if (options.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = options.BeforeEnqueueTime.Value;
            filteredMessages = filteredMessages.Where(m => m.EnqueuedTime < beforeTime).ToList();
        }

        var results = await DiagnoseMessagesAsync(filteredMessages, options, cancellationToken);

        await OutputResultsAsync(results, options, cancellationToken);

        return 0;
    }

    private async Task<List<ServiceBusReceivedMessage>> PeekMessagesAsync(
        ServiceBusReceiver receiver,
        DiagnoseDlqOptions options,
        CancellationToken cancellationToken)
    {
        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested &&
               emptyBatches < EmptyBatchThreshold &&
               allMessages.Count < options.MaxMessages)
        {
            var remaining = options.MaxMessages - allMessages.Count;
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
        DiagnoseDlqOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        var diagnosed = 0;
        var skipped = 0;

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operationId = ExtractOperationId(message);
            if (string.IsNullOrEmpty(operationId))
            {
                skipped++;
                Output.Verbose($"Skipping message {message.MessageId} - no operation ID found", options.Verbose);
                continue;
            }

            try
            {
                var result = await appInsightsService.DiagnoseMessageAsync(operationId,
                                                                           message.EnqueuedTime,
                                                                           cancellationToken);

                // Enrich with message info
                result.MessageId = message.MessageId;
                result.Subject = message.Subject;
                result.DeadLetterReason = message.DeadLetterReason;
                result.Body = TryDecodeBody(message);

                results.Add(result);
                diagnosed++;

                Output.Progress($"Diagnosed {diagnosed}/{messages.Count} messages (skipped {skipped})...");
            }
            catch (Exception ex)
            {
                Output.Verbose($"Error diagnosing message {message.MessageId}: {ex.Message}", options.Verbose);
                skipped++;
            }
        }

        Console.WriteLine();
        Output.Info($"Diagnosed {diagnosed} messages, skipped {skipped} (no operation ID or query error)");

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
        DiagnoseDlqOptions options,
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

        // Print summary to console
        PrintDiagnosticSummary(resultsWithTelemetry);

        // Write to file if specified
        if (!string.IsNullOrEmpty(options.OutputFile))
        {
            var json = JsonSerializer.Serialize(resultsWithTelemetry, JsonOptions);
            await File.WriteAllTextAsync(options.OutputFile, json, cancellationToken);
            Output.Success($"Full diagnostic results written to '{options.OutputFile}'");
        }
    }

    private void PrintDiagnosticSummary(List<DiagnosticResult> results)
    {
        Output.Info("");
        Output.Info("Diagnostic Summary:");
        Output.Info("===================");

        // Group exceptions by type/message
        var exceptionGroups = results
                              .SelectMany(r => r.Exceptions)
                              .GroupBy(e => new
                              {
                                  e.ExceptionType,
                                  e.InnermostMessage
                              })
                              .OrderByDescending(g => g.Count())
                              .Take(10);

        if (exceptionGroups.Any())
        {
            Output.Info("");
            Output.Info("Top Exceptions:");
            var headers = new[]
            {
                "Count",
                "Type",
                "Message"
            };
            var rows = exceptionGroups.Select(g => new[]
            {
                g.Count().ToString(),
                TruncateString(g.Key.ExceptionType ?? "(unknown)", 40),
                TruncateString(g.Key.InnermostMessage ?? "(no message)", 60)
            });
            Output.Table(headers, rows);
        }

        // Group failed dependencies by target
        var dependencyGroups = results
                               .SelectMany(r => r.FailedDependencies)
                               .GroupBy(d => new
                               {
                                   d.Type,
                                   d.Target
                               })
                               .OrderByDescending(g => g.Count())
                               .Take(5);

        if (dependencyGroups.Any())
        {
            Output.Info("");
            Output.Info("Failed Dependencies:");
            var headers = new[]
            {
                "Count",
                "Type",
                "Target"
            };
            var rows = dependencyGroups.Select(g => new[]
            {
                g.Count().ToString(),
                g.Key.Type ?? "(unknown)",
                TruncateString(g.Key.Target ?? "(unknown)", 50)
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
}
