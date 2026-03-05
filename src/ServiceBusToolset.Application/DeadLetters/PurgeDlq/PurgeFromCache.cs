using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.PurgeDlq;

public sealed record PurgeFromCacheCommand(string FullyQualifiedNamespace,
                                           EntityTarget Target,
                                           IReadOnlyList<ServiceBusReceivedMessage> MessagesToPurge,
                                           IProgress<(int Purged, int Skipped)>? Progress = null) : ICommand<Result<PurgeDlqResult>>;

public sealed class PurgeFromCacheCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<PurgeFromCacheCommand, Result<PurgeDlqResult>>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async ValueTask<Result<PurgeDlqResult>> Handle(
        PurgeFromCacheCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MessagesToPurge.Count == 0)
        {
            return Result.Success(new PurgeDlqResult(0, 0));
        }

        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        var targetSequenceNumbers = new HashSet<long>(command.MessagesToPurge.Select(m => m.SequenceNumber));

        var totalPurged = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested &&
               targetSequenceNumbers.Count > 0 &&
               emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize, MaxWaitTime, cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            var toComplete = new List<ServiceBusReceivedMessage>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (targetSequenceNumbers.Remove(message.SequenceNumber))
                {
                    toComplete.Add(message);
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            if (toComplete.Count > 0)
            {
                var completeTasks = toComplete.Select(m => receiver.CompleteMessageAsync(m, cancellationToken));
                await Task.WhenAll(completeTasks);

                totalPurged += toComplete.Count;
                emptyBatches = 0;
            }
            else
            {
                emptyBatches++;
            }

            if (toAbandon.Count > 0)
            {
                var abandonTasks = toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken));
                await Task.WhenAll(abandonTasks);
                totalSkipped += toAbandon.Count;
            }

            command.Progress?.Report((totalPurged, totalSkipped));
        }

        return Result.Success(new PurgeDlqResult(totalPurged, totalSkipped));
    }
}
