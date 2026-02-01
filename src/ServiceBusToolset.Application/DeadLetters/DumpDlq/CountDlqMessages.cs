using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed record DlqCountResult(long TotalCount, long? FilteredCount, DateTimeOffset? BeforeTime);

public sealed record CountDlqMessagesCommand(string FullyQualifiedNamespace,
                                             EntityTarget Target,
                                             DateTimeOffset? BeforeTime,
                                             IProgress<int>? Progress = null) : ICommand<Result<DlqCountResult>>;

public sealed class CountDlqMessagesHandler(IServiceBusClientFactory clientFactory,
                                            DlqMessageService messageService) : ICommandHandler<CountDlqMessagesCommand, Result<DlqCountResult>>
{
    public async ValueTask<Result<DlqCountResult>> Handle(
        CountDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        if (command.BeforeTime.HasValue)
        {
            return await CountWithFilterAsync(command, cancellationToken);
        }

        return await CountFastAsync(command, cancellationToken);
    }

    private async Task<Result<DlqCountResult>> CountFastAsync(
        CountDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        var count = await messageService.GetMessageCountAsync(command.FullyQualifiedNamespace,
                                                              command.Target,
                                                              cancellationToken);

        return Result.Success(new DlqCountResult(count, null, null));
    }

    private async Task<Result<DlqCountResult>> CountWithFilterAsync(
        CountDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var counts = await DlqMessageService.CountMessagesWithFilterAsync(client,
                                                                          command.Target,
                                                                          command.BeforeTime!.Value,
                                                                          command.Progress,
                                                                          cancellationToken);

        return Result.Success(new DlqCountResult(counts.TotalCount, counts.FilteredCount, command.BeforeTime));
    }
}
