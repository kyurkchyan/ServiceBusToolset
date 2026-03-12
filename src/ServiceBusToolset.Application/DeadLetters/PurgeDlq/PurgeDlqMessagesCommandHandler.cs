using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.PurgeDlq;

public sealed class PurgeDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<PurgeDlqMessagesCommand, Result<PurgeDlqResult>>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async ValueTask<Result<PurgeDlqResult>> Handle(
        PurgeDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var hasFilter = command.BeforeTime.HasValue || command.CategoryFilter is { Count: > 0 };

        if (hasFilter)
        {
            return await PurgeWithFilterAsync(client, command, cancellationToken);
        }

        return await PurgeAllAsync(client, command, cancellationToken);
    }

    private static async Task<Result<PurgeDlqResult>> PurgeAllAsync(
        ServiceBusClient client,
        PurgeDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target,
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
                continue;
            }

            emptyBatches = 0;
            totalDeleted += messages.Count;

            command.Progress?.Report((totalDeleted, 0));
        }

        return Result.Success(new PurgeDlqResult(totalDeleted, 0));
    }

    /// <summary>
    /// Purges messages from the target DLQ that satisfy the command's filters and abandons messages that do not, reporting progress as items are processed.
    /// </summary>
    /// <param name="client">Service Bus client used to create a DLQ receiver.</param>
    /// <param name="command">Command that specifies the target, optional BeforeTime, optional CategoryFilter and Schema, and an optional progress reporter.</param>
    /// <param name="cancellationToken">Token used to observe cancellation of the purge operation.</param>
    /// <returns>A Result containing a PurgeDlqResult with the total number of messages deleted and the number of skipped (left in DLQ) sequence numbers.</returns>
    private static async Task<Result<PurgeDlqResult>> PurgeWithFilterAsync(
        ServiceBusClient client,
        PurgeDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target);

        var resolver = new CategoryPropertyResolver();
        var totalDeleted = 0;
        var skippedSequenceNumbers = new HashSet<long>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            var toComplete = new List<ServiceBusReceivedMessage>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (ShouldPurge(message, command, resolver))
                {
                    toComplete.Add(message);
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            var tasks = new List<Task>();
            tasks.AddRange(toComplete.Select(m => receiver.CompleteMessageAsync(m, cancellationToken)));
            tasks.AddRange(toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken)));
            await Task.WhenAll(tasks);

            if (toComplete.Count > 0)
            {
                emptyBatches = 0;
            }
            else
            {
                emptyBatches++;
            }

            totalDeleted += toComplete.Count;
            foreach (var m in toAbandon)
            {
                skippedSequenceNumbers.Add(m.SequenceNumber);
            }

            command.Progress?.Report((totalDeleted, skippedSequenceNumbers.Count));
        }

        return Result.Success(new PurgeDlqResult(totalDeleted, skippedSequenceNumbers.Count));
    }

    /// <summary>
    /// Determines whether a received DLQ message meets the command's time and category criteria and therefore should be removed.
    /// </summary>
    /// <param name="resolver">Resolver used to derive the message's category key for schema-aware filtering.</param>
    /// <returns>`true` if the message satisfies the command's filters and should be purged, `false` otherwise.</returns>
    private static bool ShouldPurge(ServiceBusReceivedMessage message,
                                    PurgeDlqMessagesCommand command,
                                    CategoryPropertyResolver resolver)
    {
        if (command.BeforeTime.HasValue && message.EnqueuedTime >= command.BeforeTime.Value)
        {
            return false;
        }

        if (command.CategoryFilter is { Count: > 0 })
        {
            var schema = command.Schema ?? CategorizationSchema.Default;
            var key = DlqCategoryKey.FromMessage(message, schema, resolver);
            if (!command.CategoryFilter.Contains(key))
            {
                return false;
            }
        }

        return true;
    }
}
