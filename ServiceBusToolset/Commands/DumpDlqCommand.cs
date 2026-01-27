using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Models;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;

namespace ServiceBusToolset.Commands;

public class DumpDlqCommand(IServiceBusClientFactory clientFactory,
                            IConsoleOutput output,
                            IDlqCategoryAnalyzer categoryAnalyzer) : BaseCommand<DumpDlqOptions>(clientFactory, output), ICommand<DumpDlqOptions>
{
    private const int MaxBatchSize = 100;
    private const int EmptyBatchThreshold = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<int> ExecuteAsync(DumpDlqOptions options, CancellationToken cancellationToken = default)
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
            await using var client = ClientFactory.CreateClient(options.Namespace);

            if (options.DryRun)
            {
                return await ExecuteDryRunAsync(client,
                                                options,
                                                entityDescription,
                                                cancellationToken);
            }

            if (options.Interactive)
            {
                return await ExecuteInteractiveDumpAsync(client,
                                                         options,
                                                         entityDescription,
                                                         cancellationToken);
            }

            return await ExecuteDumpAsync(client,
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

    private async Task<int> ExecuteDryRunAsync(
        ServiceBusClient client,
        DumpDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (options.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredDryRunAsync(client, options, cancellationToken);
        }

        return await ExecuteFastDryRunAsync(options, entityDescription, cancellationToken);
    }

    private async Task<int> ExecuteFastDryRunAsync(
        DumpDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var adminClient = ClientFactory.CreateAdministrationClient(options.Namespace);

        long count;
        if (options.IsQueueMode)
        {
            var props = await adminClient.GetQueueRuntimePropertiesAsync(options.Queue!, cancellationToken);
            count = props.Value.DeadLetterMessageCount;
        }
        else
        {
            var props = await adminClient.GetSubscriptionRuntimePropertiesAsync(options.Topic!, options.Subscription!, cancellationToken);
            count = props.Value.DeadLetterMessageCount;
        }

        Output.Success($"[DRY RUN] Found {count} messages in DLQ for {entityDescription}");
        return 0;
    }

    private async Task<int> ExecuteFilteredDryRunAsync(
        ServiceBusClient client,
        DumpDlqOptions options,
        CancellationToken cancellationToken)
    {
        Output.Verbose("Using slow count due to --before filter", options.Verbose);

        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var count = 0;
        var filteredCount = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken:cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            count += messages.Count;
            filteredCount += messages.Count(m => m.EnqueuedTime < options.BeforeEnqueueTime!.Value);

            Output.Progress($"Counted {count} messages...");
        }

        Console.WriteLine();
        Output.Success($"[DRY RUN] Found {filteredCount} messages enqueued before {options.BeforeEnqueueTime!.Value:O} (total: {count})");
        return 0;
    }

    private async Task<int> ExecuteDumpAsync(
        ServiceBusClient client,
        DumpDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Dumping DLQ messages for {entityDescription}...");

        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var messages = await PeekAllMessagesAsync(receiver, options, cancellationToken);

        if (options.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = options.BeforeEnqueueTime.Value;
            messages = messages.Where(m => m.EnqueuedTime < beforeTime).ToList();
            Output.Verbose($"Filtered to {messages.Count} messages enqueued before {beforeTime:O}", options.Verbose);
        }

        if (messages.Count == 0)
        {
            Output.Info("No messages found matching criteria.");
            return 0;
        }

        var dumpedMessages = messages.Select(ToDto).ToList();
        await WriteJsonAsync(options.OutputFile!, dumpedMessages, cancellationToken);

        Output.Success($"Dumped {dumpedMessages.Count} messages to '{options.OutputFile}'");
        return 0;
    }

    private async Task<int> ExecuteInteractiveDumpAsync(
        ServiceBusClient client,
        DumpDlqOptions options,
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
        Console.Write("Select categories to dump (comma-separated numbers, 'all', or 'q' to quit): ");
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
        var totalToDump = 0;
        foreach (var cat in selectedIndices.Select(idx => categories[idx]))
        {
            selectedCategories.Add((cat.Label, cat.DeadLetterReason));
            totalToDump += cat.Count;
        }

        Output.Info($"Dumping {totalToDump} messages from {selectedIndices.Count} categories...");

        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var allMessages = await PeekAllMessagesAsync(receiver, options, cancellationToken);

        var filteredMessages = allMessages
                               .Where(m =>
                               {
                                   var label = m.Subject ?? "(none)";
                                   var reason = m.DeadLetterReason ?? "(none)";
                                   return selectedCategories.Contains((label, reason));
                               })
                               .ToList();

        if (options.BeforeEnqueueTime.HasValue)
        {
            var beforeTime = options.BeforeEnqueueTime.Value;
            filteredMessages = filteredMessages.Where(m => m.EnqueuedTime < beforeTime).ToList();
        }

        var dumpedMessages = filteredMessages.Select(ToDto).ToList();
        await WriteJsonAsync(options.OutputFile!, dumpedMessages, cancellationToken);

        Output.Success($"Dumped {dumpedMessages.Count} messages to '{options.OutputFile}'");
        return 0;
    }

    private async Task<List<ServiceBusReceivedMessage>> PeekAllMessagesAsync(
        ServiceBusReceiver receiver,
        DumpDlqOptions options,
        CancellationToken cancellationToken)
    {
        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken:cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            allMessages.AddRange(messages);

            Output.Progress($"Peeked {allMessages.Count} messages...");
            Output.Verbose($"\nPeeked batch of {messages.Count} messages", options.Verbose);
        }

        Console.WriteLine();
        return allMessages;
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

    private static DumpedMessage ToDto(ServiceBusReceivedMessage msg)
    {
        return new DumpedMessage
        {
            MessageId = msg.MessageId,
            CorrelationId = msg.CorrelationId,
            Subject = msg.Subject,
            ContentType = msg.ContentType,
            Body = TryDecodeBody(msg),
            DeadLetterReason = msg.DeadLetterReason,
            DeadLetterErrorDescription = msg.DeadLetterErrorDescription,
            EnqueuedTime = msg.EnqueuedTime,
            ExpiresAt = msg.ExpiresAt,
            SequenceNumber = msg.SequenceNumber,
            SessionId = msg.SessionId,
            PartitionKey = msg.PartitionKey,
            To = msg.To,
            ReplyTo = msg.ReplyTo,
            ReplyToSessionId = msg.ReplyToSessionId,
            TimeToLive = msg.TimeToLive,
            ApplicationProperties = msg.ApplicationProperties.ToDictionary(kvp => kvp.Key,
                                                                           kvp => (object?)kvp.Value)
        };
    }

    private static JsonNode? TryDecodeBody(ServiceBusReceivedMessage msg)
    {
        string? text = null;

        // Try to get text content
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

        // Try to decode as UTF-8 text if we don't have text yet
        if (text == null)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(msg.Body.ToArray());
                // Check if it's valid UTF-8 text (no replacement characters)
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

        // If we have text, try to parse it as JSON
        if (text != null)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch
            {
                // Not valid JSON, return as string
                return JsonValue.Create(text);
            }
        }

        // Return Base64 encoded binary content
        return JsonValue.Create(Convert.ToBase64String(msg.Body.ToArray()));
    }

    private static async Task WriteJsonAsync(string filePath, List<DumpedMessage> messages, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(messages, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }
}
