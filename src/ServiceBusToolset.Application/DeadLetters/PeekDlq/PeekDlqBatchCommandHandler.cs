using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common;

namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed class PeekDlqBatchCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<PeekDlqBatchCommand, Result<PeekDlqBatchResult>>
{
    private const int PeekSubBatchSize = 100;
    private const int EmptyBatchThreshold = 3;

    public async ValueTask<Result<PeekDlqBatchResult>> Handle(
        PeekDlqBatchCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        // Get total DLQ count — use known count if provided, otherwise query admin API
        var totalDeadLetterCount = command.KnownDeadLetterCount
                                   ?? await GetDeadLetterCountAsync(clientFactory, command.FullyQualifiedNamespace, command.Target);

        // Peek messages in sub-batches until we reach BatchSize or run out
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        List<ServiceBusReceivedMessage> allMessages = [];
        var emptyBatches = 0;
        var isFirstPeek = true;
        long highestSequenceNumber = command.FromSequenceNumber ?? -1;

        while (allMessages.Count < command.BatchSize &&
               emptyBatches < EmptyBatchThreshold &&
               !cancellationToken.IsCancellationRequested)
        {
            var remaining = command.BatchSize - allMessages.Count;
            var subBatchSize = Math.Min(PeekSubBatchSize, remaining);

            IReadOnlyList<ServiceBusReceivedMessage> batch;
            if (isFirstPeek && command.FromSequenceNumber.HasValue)
            {
                batch = await receiver.PeekMessagesAsync(subBatchSize, command.FromSequenceNumber.Value + 1, cancellationToken);
                isFirstPeek = false;
            }
            else
            {
                batch = await receiver.PeekMessagesAsync(subBatchSize, cancellationToken: cancellationToken);
                isFirstPeek = false;
            }

            if (batch.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            // Detect wrap-around: if the batch contains messages with sequence numbers
            // we've already passed, the receiver has looped back to the beginning
            if (batch[0].SequenceNumber <= highestSequenceNumber)
            {
                break;
            }

            emptyBatches = 0;
            allMessages.AddRange(batch);
            highestSequenceNumber = batch[^1].SequenceNumber;
        }

        if (allMessages.Count == 0)
        {
            return Result.Success(new PeekDlqBatchResult([],
                0, 0, command.FromSequenceNumber, false, totalDeadLetterCount));
        }

        // Extract operation IDs
        List<PeekedMessage> messages = [];
        var skipped = 0;
        HashSet<string> seenOperationIds = [];

        foreach (var message in allMessages)
        {
            var operationId = MessageDiagnostics.ExtractOperationId(message);
            if (string.IsNullOrEmpty(operationId))
            {
                skipped++;
                continue;
            }

            if (seenOperationIds.Add(operationId))
            {
                messages.Add(new PeekedMessage(message.MessageId,
                    message.Subject,
                    operationId,
                    message.EnqueuedTime,
                    message.DeadLetterReason));
            }
        }

        var lastSequenceNumber = allMessages[^1].SequenceNumber;
        // We have more messages if we filled the batch (didn't run out early)
        var hasMore = allMessages.Count >= command.BatchSize && emptyBatches < EmptyBatchThreshold;

        return Result.Success(new PeekDlqBatchResult(messages,
            allMessages.Count, skipped, lastSequenceNumber, hasMore, totalDeadLetterCount));
    }

    private static async Task<long> GetDeadLetterCountAsync(
        IServiceBusClientFactory clientFactory,
        string fullyQualifiedNamespace,
        EntityTarget target)
    {
        try
        {
            var adminClient = clientFactory.CreateAdministrationClient(fullyQualifiedNamespace);

            if (target.IsQueueMode)
            {
                var props = await adminClient.GetQueueRuntimePropertiesAsync(target.Queue!);
                return props.Value.DeadLetterMessageCount;
            }

            var subProps = await adminClient.GetSubscriptionRuntimePropertiesAsync(target.Topic!, target.Subscription!);
            return subProps.Value.DeadLetterMessageCount;
        }
        catch
        {
            // Admin API may not be available in all environments (e.g., emulator, unit tests)
            return 0;
        }
    }
}
