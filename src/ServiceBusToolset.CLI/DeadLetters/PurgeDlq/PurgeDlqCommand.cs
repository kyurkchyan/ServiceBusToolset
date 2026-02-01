using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;

namespace ServiceBusToolset.CLI.DeadLetters.PurgeDlq;

public class PurgeDlqCommand(IServiceBusClientFactory clientFactory,
                             IConsoleOutput output,
                             IDlqCategoryAnalyzer categoryAnalyzer) : BaseCommand<PurgeDlqCliCommand>(clientFactory, output), ICommand<PurgeDlqCliCommand>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async Task<int> ExecuteAsync(PurgeDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
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
            await using var client = ClientFactory.CreateClient(cliCommand.Namespace);

            if (cliCommand.DryRun)
            {
                return await ExecuteDryRunAsync(client,
                                                cliCommand,
                                                entityDescription,
                                                cancellationToken);
            }

            if (cliCommand.Interactive)
            {
                return await ExecuteInteractivePurgeAsync(client,
                                                          cliCommand,
                                                          entityDescription,
                                                          cancellationToken);
            }

            return await ExecutePurgeAsync(client,
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

    private async Task<int> ExecuteDryRunAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredDryRunAsync(client,
                                                    cliCommand,
                                                    cancellationToken);
        }

        return await ExecuteFastDryRunAsync(cliCommand, entityDescription, cancellationToken);
    }

    private async Task<int> ExecuteFastDryRunAsync(
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var adminClient = ClientFactory.CreateAdministrationClient(cliCommand.Namespace);

        long count;
        if (cliCommand.IsQueueMode)
        {
            var props = await adminClient.GetQueueRuntimePropertiesAsync(cliCommand.Queue!, cancellationToken);
            count = props.Value.DeadLetterMessageCount;
        }
        else
        {
            var props = await adminClient.GetSubscriptionRuntimePropertiesAsync(cliCommand.Topic!, cliCommand.Subscription!, cancellationToken);
            count = props.Value.DeadLetterMessageCount;
        }

        Output.Success($"[DRY RUN] Found {count} messages in DLQ for {entityDescription}");
        return 0;
    }

    private async Task<int> ExecuteFilteredDryRunAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        CancellationToken cancellationToken)
    {
        Output.Verbose("Using slow count due to --before filter", cliCommand.Verbose);

        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
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
            filteredCount += messages.Count(m => m.EnqueuedTime < cliCommand.BeforeEnqueueTime!.Value);

            Output.Progress($"Counted {count} messages...");
        }

        Console.WriteLine();
        Output.Success($"[DRY RUN] Found {filteredCount} messages enqueued before {cliCommand.BeforeEnqueueTime!.Value:O} (total: {count})");
        return 0;
    }

    private async Task<int> ExecutePurgeAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Purging DLQ for {entityDescription}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredPurgeAsync(client,
                                                   cliCommand,
                                                   entityDescription,
                                                   cancellationToken);
        }

        return await ExecuteFullPurgeAsync(client,
                                           cliCommand,
                                           entityDescription,
                                           cancellationToken);
    }

    private async Task<int> ExecuteFullPurgeAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.ReceiveAndDelete);

        var totalDeleted = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;
            totalDeleted += messages.Count;

            Output.Progress($"Purged {totalDeleted} messages...");
            Output.Verbose($"\nReceived batch of {messages.Count} messages", cliCommand.Verbose);
        }

        Console.WriteLine();
        Output.Success($"Purged {totalDeleted} messages from DLQ for {entityDescription}");
        return 0;
    }

    private async Task<int> ExecuteFilteredPurgeAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var totalDeleted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;
        var beforeTime = cliCommand.BeforeEnqueueTime!.Value;

        Output.Verbose($"Filtering messages enqueued before {beforeTime:O}", cliCommand.Verbose);

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;

            foreach (var message in messages)
            {
                if (message.EnqueuedTime < beforeTime)
                {
                    await receiver.CompleteMessageAsync(message, cancellationToken);
                    totalDeleted++;
                }
                else
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken:cancellationToken);
                    totalSkipped++;
                }
            }

            Output.Progress($"Purged {totalDeleted} messages (skipped {totalSkipped})...");
        }

        Console.WriteLine();
        Output.Success($"Purged {totalDeleted} messages from DLQ for {entityDescription} (skipped {totalSkipped} newer messages)");
        return 0;
    }

    private async Task<int> ExecuteInteractivePurgeAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Analyzing DLQ for {entityDescription}...");

        // Step 1: Peek all messages and build category dictionary
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

        // Step 2: Display category table
        DisplayCategoryTable(categories);

        // Step 3: Get user selection
        Output.Info("");
        Console.Write("Select categories to purge (comma-separated numbers, 'all', or 'q' to quit): ");
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

        // Step 4: Build set of selected category keys
        var selectedCategories = new HashSet<(string Label, string Reason)>();
        var totalToPurge = 0;
        foreach (var cat in selectedIndices.Select(idx => categories[idx]))
        {
            selectedCategories.Add((cat.Label, cat.DeadLetterReason));
            totalToPurge += cat.Count;
        }

        Output.Info($"Purging {totalToPurge} messages from {selectedIndices.Count} categories...");

        // Step 5: Receive messages and complete only those matching selected categories
        var totalDeleted = await PurgeByCategoriesAsync(client,
                                                        cliCommand,
                                                        selectedCategories,
                                                        cancellationToken);

        Console.WriteLine();
        Output.Success($"Purged {totalDeleted} messages from DLQ for {entityDescription}.");
        return 0;
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
            // Handle ranges like "1-5"
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', 2);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        var idx = i - 1; // Convert to 0-based
                        if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                        {
                            indices.Add(idx);
                        }
                    }
                }
            }
            else if (int.TryParse(part, out var num))
            {
                var idx = num - 1; // Convert to 0-based
                if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                {
                    indices.Add(idx);
                }
            }
        }

        return indices;
    }

    private async Task<int> PurgeByCategoriesAsync(
        ServiceBusClient client,
        PurgeDlqCliCommand cliCommand,
        HashSet<(string Label, string Reason)> selectedCategories,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var totalDeleted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;

            var toComplete = new List<ServiceBusReceivedMessage>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                var label = message.Subject ?? "(none)";
                var reason = message.DeadLetterReason ?? "(none)";
                var key = (label, reason);

                if (selectedCategories.Contains(key))
                {
                    toComplete.Add(message);
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            // Process completions and abandons in parallel
            var tasks = new List<Task>();
            tasks.AddRange(toComplete.Select(m => receiver.CompleteMessageAsync(m, cancellationToken)));
            tasks.AddRange(toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken)));
            await Task.WhenAll(tasks);

            totalDeleted += toComplete.Count;
            totalSkipped += toAbandon.Count;

            Output.Progress($"Purged {totalDeleted} messages (skipped {totalSkipped})...");
        }

        return totalDeleted;
    }
}
