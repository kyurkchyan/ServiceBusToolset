using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed class DumpDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<DumpDlqMessagesCommand, Result<DlqDumpResult>>
{
    /// <summary>
    /// Executes the dump of dead-letter queue messages described by the command and writes the filtered messages as JSON to the specified output file.
    /// </summary>
    /// <param name="command">Command containing source namespace and target queue/subscription, filtering options (enqueue time, category filter, schema), progress reporter, and the output file path.</param>
    /// <param name="cancellationToken">Token to observe while performing I/O and long-running operations.</param>
    /// <returns>A Result containing a DlqDumpResult with the number of messages written and the output file path.</returns>
    public async ValueTask<Result<DlqDumpResult>> Handle(
        DumpDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var allMessages = await DlqMessageService.PeekAllMessagesAsync(client,
                                                                       command.Target,
                                                                       command.Progress,
                                                                       cancellationToken);

        var filtered = MessageFilters.FilterByEnqueueTime(allMessages, command.BeforeTime);

        if (command.CategoryFilter is { Count: > 0 })
        {
            filtered = DlqMessageService.FilterByCategories(filtered, command.CategoryFilter, command.Schema);
        }

        if (filtered.Count == 0)
        {
            return Result.Success(new DlqDumpResult(0, command.OutputFilePath));
        }

        var dumpedMessages = MessageSerializer.ToDtoList(filtered);
        await MessageSerializer.WriteJsonAsync(command.OutputFilePath, dumpedMessages, cancellationToken);

        return Result.Success(new DlqDumpResult(dumpedMessages.Count, command.OutputFilePath));
    }
}
