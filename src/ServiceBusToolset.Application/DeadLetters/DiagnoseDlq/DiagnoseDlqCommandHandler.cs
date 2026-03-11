using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed class DiagnoseDlqCommandHandler(IServiceBusClientFactory clientFactory,
                                              IAppInsightsService appInsightsService) : ICommandHandler<DiagnoseDlqCommand, Result<DiagnoseDlqResult>>
{
    public async ValueTask<Result<DiagnoseDlqResult>> Handle(
        DiagnoseDlqCommand command,
        CancellationToken cancellationToken)
    {
        // Initialize Application Insights connection
        appInsightsService.Initialize(command.AppInsightsResourceId);

        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

        // Peek messages
        var messages = await MessageOperations.PeekAsync(receiver,
                                                         command.MaxMessages,
                                                         progress:command.Progress,
                                                         cancellationToken:cancellationToken);

        // Apply time filter
        var filteredMessages = MessageFilters.FilterByEnqueueTime(messages, command.BeforeTime).ToList();

        // Apply category filter
        if (command.CategoryFilter is { Count: > 0 })
        {
            filteredMessages = DlqMessageService.FilterByCategories(filteredMessages, command.CategoryFilter, command.Schema).ToList();
        }

        // Limit to max messages
        filteredMessages = filteredMessages.Take(command.MaxMessages).ToList();

        if (filteredMessages.Count == 0)
        {
            return Result.Success(new DiagnoseDlqResult([],
                                                        0,
                                                        0,
                                                        0));
        }

        // Diagnose messages
        var (results, skipped) = await MessageDiagnostics.DiagnoseMessagesAsync(appInsightsService,
                                                                                filteredMessages,
                                                                                command.BatchProgress,
                                                                                cancellationToken);

        var resultsWithTelemetry = results
            .Count(r => r.Exceptions.Count > 0 || r.Traces.Count > 0 || r.FailedDependencies.Count > 0);

        return Result.Success(new DiagnoseDlqResult(results,
                                                    filteredMessages.Count,
                                                    skipped,
                                                    resultsWithTelemetry));
    }
}
