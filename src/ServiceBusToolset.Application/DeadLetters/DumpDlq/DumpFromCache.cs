using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed record DumpFromCacheCommand(IReadOnlyList<ServiceBusReceivedMessage> MessagesToDump,
                                          string OutputFilePath) : ICommand<Result<DlqDumpResult>>;

public sealed class DumpFromCacheCommandHandler
    : ICommandHandler<DumpFromCacheCommand, Result<DlqDumpResult>>
{
    public async ValueTask<Result<DlqDumpResult>> Handle(
        DumpFromCacheCommand command,
        CancellationToken cancellationToken)
    {
        if (command.MessagesToDump.Count == 0)
        {
            return Result.Success(new DlqDumpResult(0, command.OutputFilePath));
        }

        var dumpedMessages = MessageSerializer.ToDtoList(command.MessagesToDump);
        await MessageSerializer.WriteJsonAsync(command.OutputFilePath, dumpedMessages, cancellationToken);

        return Result.Success(new DlqDumpResult(dumpedMessages.Count, command.OutputFilePath));
    }
}
