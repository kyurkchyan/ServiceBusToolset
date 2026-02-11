using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record ResubmitFromCacheCommand(string FullyQualifiedNamespace,
                                              EntityTarget Target,
                                              string TargetEntity,
                                              IReadOnlyList<ServiceBusReceivedMessage> MessagesToResubmit,
                                              ResubmitTracker ResubmitTracker,
                                              IProgress<(int Resubmitted, int Skipped)>? Progress = null) : ICommand<Result<ResubmitDlqResult>>;

public sealed class ResubmitFromCacheCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<ResubmitFromCacheCommand, Result<ResubmitDlqResult>>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async ValueTask<Result<ResubmitDlqResult>> Handle(
        ResubmitFromCacheCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MessagesToResubmit.Count == 0)
        {
            return Result.Success(new ResubmitDlqResult(0, 0));
        }

        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);
        await using var sender = client.CreateSender(command.TargetEntity);

        var targetSequenceNumbers = new HashSet<long>(command.MessagesToResubmit.Select(m => m.SequenceNumber));

        var totalResubmitted = 0;
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

            var toResubmit = new List<(ServiceBusReceivedMessage Original, ServiceBusMessage New)>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (targetSequenceNumbers.Remove(message.SequenceNumber))
                {
                    toResubmit.Add((message, MessageResubmitHelper.CreateResubmitMessage(message)));
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

                foreach (var (original, _) in toResubmit)
                {
                    command.ResubmitTracker.MarkResubmitted(original.MessageId);
                }

                totalResubmitted += toResubmit.Count;
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

            command.Progress?.Report((totalResubmitted, totalSkipped));
        }

        return Result.Success(new ResubmitDlqResult(totalResubmitted, totalSkipped));
    }
}
