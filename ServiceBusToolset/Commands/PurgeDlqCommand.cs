using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;

namespace ServiceBusToolset.Commands;

public class PurgeDlqCommand(IServiceBusClientFactory clientFactory, IConsoleOutput output) : BaseCommand<PurgeDlqOptions>(clientFactory, output), ICommand<PurgeDlqOptions>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async Task<int> ExecuteAsync(PurgeDlqOptions options, CancellationToken cancellationToken = default)
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

            return await ExecutePurgeAsync(client,
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
        PurgeDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (options.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredDryRunAsync(client,
                                                    options,
                                                    entityDescription,
                                                    cancellationToken);
        }

        return await ExecuteFastDryRunAsync(options, entityDescription, cancellationToken);
    }

    private async Task<int> ExecuteFastDryRunAsync(
        PurgeDlqOptions options,
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
        PurgeDlqOptions options,
        string entityDescription,
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

    private async Task<int> ExecutePurgeAsync(
        ServiceBusClient client,
        PurgeDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Purging DLQ for {entityDescription}...");

        if (options.BeforeEnqueueTime.HasValue)
        {
            return await ExecuteFilteredPurgeAsync(client,
                                                   options,
                                                   entityDescription,
                                                   cancellationToken);
        }

        return await ExecuteFullPurgeAsync(client,
                                           options,
                                           entityDescription,
                                           cancellationToken);
    }

    private async Task<int> ExecuteFullPurgeAsync(
        ServiceBusClient client,
        PurgeDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
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
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", options.Verbose);
                continue;
            }

            emptyBatches = 0;
            totalDeleted += messages.Count;

            Output.Progress($"Purged {totalDeleted} messages...");
            Output.Verbose($"\nReceived batch of {messages.Count} messages", options.Verbose);
        }

        Console.WriteLine();
        Output.Success($"Purged {totalDeleted} messages from DLQ for {entityDescription}");
        return 0;
    }

    private async Task<int> ExecuteFilteredPurgeAsync(
        ServiceBusClient client,
        PurgeDlqOptions options,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client,
                                                     options.Queue,
                                                     options.Topic,
                                                     options.Subscription,
                                                     ServiceBusReceiveMode.PeekLock);

        var totalDeleted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;
        var beforeTime = options.BeforeEnqueueTime!.Value;

        Output.Verbose($"Filtering messages enqueued before {beforeTime:O}", options.Verbose);

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                Output.Verbose($"Empty batch {emptyBatches}/{EmptyBatchThreshold}", options.Verbose);
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
}
