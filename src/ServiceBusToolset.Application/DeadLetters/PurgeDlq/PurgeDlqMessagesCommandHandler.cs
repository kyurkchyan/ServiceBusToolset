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

    private static async Task<Result<PurgeDlqResult>> PurgeWithFilterAsync(
        ServiceBusClient client,
        PurgeDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target);

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
                if (ShouldPurge(message, command))
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
            tasks.AddRange(toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken: cancellationToken)));
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

    private static bool ShouldPurge(ServiceBusReceivedMessage message, PurgeDlqMessagesCommand command)
    {
        if (command.BeforeTime.HasValue && message.EnqueuedTime >= command.BeforeTime.Value)
        {
            return false;
        }

        if (command.CategoryFilter is { Count: > 0 })
        {
            var key = DlqCategoryKey.FromMessage(message.Subject, message.DeadLetterReason);
            if (!command.CategoryFilter.Contains(key))
            {
                return false;
            }
        }

        return true;
    }
}
