using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed record DlqCategoriesResult(IReadOnlyList<DlqCategory> Categories, int TotalMessageCount);

public sealed record AnalyzeDlqCategoriesCommand(string FullyQualifiedNamespace,
                                                 EntityTarget Target,
                                                 IProgress<int>? Progress = null) : ICommand<Result<DlqCategoriesResult>>;

public sealed class AnalyzeDlqCategoriesHandler(IServiceBusClientFactory clientFactory) : ICommandHandler<AnalyzeDlqCategoriesCommand, Result<DlqCategoriesResult>>
{
    public async ValueTask<Result<DlqCategoriesResult>> Handle(
        AnalyzeDlqCategoriesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var categories = await DlqMessageService.AnalyzeCategoriesAsync(client,
                                                                        command.Target,
                                                                        command.Progress,
                                                                        cancellationToken);

        var totalCount = categories.Sum(c => c.Count);

        return Result.Success(new DlqCategoriesResult(categories, totalCount));
    }
}
