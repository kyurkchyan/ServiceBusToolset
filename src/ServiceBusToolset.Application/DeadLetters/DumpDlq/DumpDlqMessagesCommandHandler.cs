using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed class DumpDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory,
                                                  DlqMessageService messageService) : ICommandHandler<DumpDlqMessagesCommand, Result<DlqDumpResult>>
{
    public async ValueTask<Result<DlqDumpResult>> Handle(
        DumpDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var allMessages = await DlqMessageService.PeekAllMessagesAsync(client,
                                                                       command.Target,
                                                                       command.Progress,
                                                                       cancellationToken);

        var filtered = DlqMessageService.FilterByTime(allMessages, command.BeforeTime);

        if (command.CategoryFilter is { Count: > 0 })
        {
            filtered = DlqMessageService.FilterByCategories(filtered, command.CategoryFilter);
        }

        if (filtered.Count == 0)
        {
            return Result.Success(new DlqDumpResult(0, command.OutputFilePath));
        }

        var dumpedMessages = DlqMessageService.ConvertToDto(filtered);
        await messageService.WriteJsonAsync(command.OutputFilePath, dumpedMessages, cancellationToken);

        return Result.Success(new DlqDumpResult(dumpedMessages.Count, command.OutputFilePath));
    }
}
