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
    public async ValueTask<Result<PeekDlqBatchResult>> Handle(
        PeekDlqBatchCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        // Get total DLQ count — use known count if provided, otherwise query admin API
        var totalDeadLetterCount = command.KnownDeadLetterCount
                                   ?? await GetDeadLetterCountAsync(clientFactory, command.FullyQualifiedNamespace, command.Target);

        // Peek a batch of messages
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        IReadOnlyList<ServiceBusReceivedMessage> rawMessages = command.FromSequenceNumber.HasValue
                                                                   ? await receiver.PeekMessagesAsync(command.BatchSize, command.FromSequenceNumber.Value + 1, cancellationToken)
                                                                   : await receiver.PeekMessagesAsync(command.BatchSize, cancellationToken:cancellationToken);

        if (rawMessages.Count == 0)
        {
            return Result.Success(new PeekDlqBatchResult([],
                                                         0,
                                                         0,
                                                         command.FromSequenceNumber,
                                                         false,
                                                         totalDeadLetterCount));
        }

        // Extract operation IDs
        List<PeekedMessage> messages = [];
        var skipped = 0;
        HashSet<string> seenOperationIds = [];

        foreach (var message in rawMessages)
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

        var lastSequenceNumber = rawMessages[^1].SequenceNumber;
        var hasMore = rawMessages.Count >= command.BatchSize;

        return Result.Success(new PeekDlqBatchResult(messages,
                                                     rawMessages.Count,
                                                     skipped,
                                                     lastSequenceNumber,
                                                     hasMore,
                                                     totalDeadLetterCount));
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
