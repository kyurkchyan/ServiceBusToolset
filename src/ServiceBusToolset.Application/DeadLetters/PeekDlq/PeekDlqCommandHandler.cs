using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common;

namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed class PeekDlqCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<PeekDlqCommand, Result<PeekDlqResult>>
{
    public async ValueTask<Result<PeekDlqResult>> Handle(
        PeekDlqCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        var rawMessages = await MessageOperations.PeekAsync(receiver,
            command.MaxMessages,
            cancellationToken: cancellationToken);

        var filtered = MessageFilters.FilterByEnqueueTime(rawMessages, command.BeforeTime).ToList();

        var messages = new List<PeekedMessage>();
        var skipped = 0;
        var seenOperationIds = new HashSet<string>();

        foreach (var message in filtered)
        {
            var operationId = MessageDiagnostics.ExtractOperationId(message);
            if (string.IsNullOrEmpty(operationId))
            {
                skipped++;
                continue;
            }

            if (seenOperationIds.Add(operationId))
            {
                messages.Add(new PeekedMessage(
                    message.MessageId,
                    message.Subject,
                    operationId,
                    message.EnqueuedTime,
                    message.DeadLetterReason));
            }
        }

        return Result.Success(new PeekDlqResult(messages, filtered.Count, skipped));
    }
}
