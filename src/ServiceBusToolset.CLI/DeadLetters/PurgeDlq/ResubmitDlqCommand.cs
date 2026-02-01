using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlqMessages;

namespace ServiceBusToolset.CLI.DeadLetters.PurgeDlq;

public class ResubmitDlqCommand(IServiceBusClientFactory clientFactory,
                                IConsoleOutput output,
                                IDlqCategoryAnalyzer categoryAnalyzer) : BaseCommand<ResubmitDlqCliCommand>(clientFactory, output), ICommand<ResubmitDlqCliCommand>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async Task<int> ExecuteAsync(ResubmitDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
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
                return await ExecuteInteractiveResubmitAsync(client,
                                                             cliCommand,
                                                             entityDescription,
                                                             cancellationToken);
            }

            return await ExecuteResubmitAsync(client,
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
        ResubmitDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredDryRunAsync(client, cliCommand, cancellationToken);
        }

        return await ExecuteFastDryRunAsync(cliCommand, entityDescription, cancellationToken);
    }

    private async Task<int> ExecuteFastDryRunAsync(
        ResubmitDlqCliCommand cliCommand,
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
        ResubmitDlqCliCommand cliCommand,
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

    private async Task<int> ExecuteResubmitAsync(
        ServiceBusClient client,
        ResubmitDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var targetInfo = GetTargetInfo(cliCommand);
        Output.Info($"Resubmitting DLQ messages for {entityDescription}{targetInfo}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredResubmitAsync(client,
                                                      cliCommand,
                                                      entityDescription,
                                                      cancellationToken);
        }

        return await ExecuteFullResubmitAsync(client,
                                              cliCommand,
                                              entityDescription,
                                              cancellationToken);
    }

    private async Task<int> ExecuteFullResubmitAsync(
        ServiceBusClient client,
        ResubmitDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);
        await using var sender = CreateSender(client, cliCommand.EffectiveTarget);

        var targetInfo = GetTargetInfo(cliCommand);
        var totalResubmitted = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize, MaxWaitTime, cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;

            var newMessages = messages.Select(CreateResubmitMessage).ToList();
            await sender.SendMessagesAsync(newMessages, cancellationToken);

            var completeTasks = messages.Select(m => receiver.CompleteMessageAsync(m, cancellationToken));
            await Task.WhenAll(completeTasks);

            totalResubmitted += messages.Count;
            Output.Progress($"Resubmitted {totalResubmitted} messages...");
            Output.Verbose($"\nProcessed batch of {messages.Count} messages", cliCommand.Verbose);
        }

        Console.WriteLine();
        Output.Success($"Resubmitted {totalResubmitted} messages from DLQ for {entityDescription}{targetInfo}");
        return 0;
    }

    private async Task<int> ExecuteFilteredResubmitAsync(
        ServiceBusClient client,
        ResubmitDlqCliCommand cliCommand,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);
        await using var sender = CreateSender(client, cliCommand.EffectiveTarget);

        var targetInfo = GetTargetInfo(cliCommand);
        var totalResubmitted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;
        var beforeTime = cliCommand.BeforeEnqueueTime!.Value;

        Output.Verbose($"Filtering messages enqueued before {beforeTime:O}", cliCommand.Verbose);

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize, MaxWaitTime, cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;

            var toResubmit = new List<(ServiceBusReceivedMessage Original, ServiceBusMessage New)>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (message.EnqueuedTime < beforeTime)
                {
                    toResubmit.Add((message, CreateResubmitMessage(message)));
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            if (toResubmit.Count > 0)
            {
                await sender.SendMessagesAsync(toResubmit.Select(x => x.New).ToList(), cancellationToken);
                var completeTasks = toResubmit.Select(x => receiver.CompleteMessageAsync(x.Original, cancellationToken));
                await Task.WhenAll(completeTasks);
                totalResubmitted += toResubmit.Count;
            }

            if (toAbandon.Count > 0)
            {
                var abandonTasks = toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken));
                await Task.WhenAll(abandonTasks);
                totalSkipped += toAbandon.Count;
            }

            Output.Progress($"Resubmitted {totalResubmitted} messages (skipped {totalSkipped})...");
        }

        Console.WriteLine();
        Output.Success($"Resubmitted {totalResubmitted} messages from DLQ for {entityDescription}{targetInfo} (skipped {totalSkipped} newer messages)");
        return 0;
    }

    private async Task<int> ExecuteInteractiveResubmitAsync(
        ServiceBusClient client,
        ResubmitDlqCliCommand cliCommand,
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
        Console.Write("Select categories to resubmit (comma-separated numbers, 'all', or 'q' to quit): ");
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
        var totalToResubmit = 0;
        foreach (var cat in selectedIndices.Select(idx => categories[idx]))
        {
            selectedCategories.Add((cat.Label, cat.DeadLetterReason));
            totalToResubmit += cat.Count;
        }

        var targetInfo = GetTargetInfo(cliCommand);
        Output.Info($"Resubmitting {totalToResubmit} messages from {selectedIndices.Count} categories{targetInfo}...");

        var totalResubmitted = await ResubmitByCategoriesAsync(client,
                                                               cliCommand,
                                                               selectedCategories,
                                                               cancellationToken);

        Console.WriteLine();
        Output.Success($"Resubmitted {totalResubmitted} messages from DLQ for {entityDescription}{targetInfo}.");
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

    private async Task<int> ResubmitByCategoriesAsync(
        ServiceBusClient client,
        ResubmitDlqCliCommand cliCommand,
        HashSet<(string Label, string Reason)> selectedCategories,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     cliCommand.Queue,
                                                     cliCommand.Topic,
                                                     cliCommand.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);
        await using var sender = CreateSender(client, cliCommand.EffectiveTarget);

        var totalResubmitted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize, MaxWaitTime, cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", cliCommand.Verbose);
                continue;
            }

            emptyBatches = 0;

            var toResubmit = new List<(ServiceBusReceivedMessage Original, ServiceBusMessage New)>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                var label = message.Subject ?? "(none)";
                var reason = message.DeadLetterReason ?? "(none)";
                var key = (label, reason);

                if (selectedCategories.Contains(key))
                {
                    toResubmit.Add((message, CreateResubmitMessage(message)));
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            if (toResubmit.Count > 0)
            {
                await sender.SendMessagesAsync(toResubmit.Select(x => x.New).ToList(), cancellationToken);
                var completeTasks = toResubmit.Select(x => receiver.CompleteMessageAsync(x.Original, cancellationToken));
                await Task.WhenAll(completeTasks);
                totalResubmitted += toResubmit.Count;
            }

            if (toAbandon.Count > 0)
            {
                var abandonTasks = toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken));
                await Task.WhenAll(abandonTasks);
                totalSkipped += toAbandon.Count;
            }

            Output.Progress($"Resubmitted {totalResubmitted} messages (skipped {totalSkipped})...");
        }

        return totalResubmitted;
    }

    private static ServiceBusMessage CreateResubmitMessage(ServiceBusReceivedMessage original)
    {
        var message = new ServiceBusMessage(original.Body)
        {
            ContentType = original.ContentType,
            Subject = original.Subject,
            MessageId = original.MessageId,
            CorrelationId = original.CorrelationId,
            To = original.To,
            ReplyTo = original.ReplyTo,
            ReplyToSessionId = original.ReplyToSessionId,
            SessionId = original.SessionId,
            PartitionKey = original.PartitionKey,
            TransactionPartitionKey = original.TransactionPartitionKey,
            TimeToLive = original.TimeToLive
        };

        foreach (var prop in original.ApplicationProperties)
        {
            message.ApplicationProperties[prop.Key] = prop.Value;
        }

        return message;
    }

    private static ServiceBusSender CreateSender(ServiceBusClient client, string targetEntity) => client.CreateSender(targetEntity);

    private static string GetTargetInfo(ResubmitDlqCliCommand cliCommand)
    {
        var hasCustomTarget = !string.IsNullOrEmpty(cliCommand.TargetQueue) || !string.IsNullOrEmpty(cliCommand.TargetTopic);
        if (!hasCustomTarget)
        {
            return string.Empty;
        }

        var targetType = !string.IsNullOrEmpty(cliCommand.TargetQueue) ? "queue" : "topic";
        return $" to {targetType} '{cliCommand.EffectiveTarget}'";
    }
}
