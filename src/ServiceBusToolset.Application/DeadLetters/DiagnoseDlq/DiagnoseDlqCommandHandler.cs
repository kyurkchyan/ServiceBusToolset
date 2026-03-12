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
    /// <summary>
    /// Runs diagnostics against messages in a dead-letter queue using the filters and limits provided by the command and returns aggregated diagnostic outcomes.
    /// </summary>
    /// <param name="command">Request specifying the target dead-letter queue/topic, enqueue time cutoff, category filter, schema, maximums, and progress callbacks for peek and batch operations.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// A Result containing a DiagnoseDlqResult with:
    /// - diagnostic results for each analyzed message,
    /// - the total number of messages considered after filtering,
    /// - the count of messages skipped during diagnosis,
    /// - the number of results that reported telemetry (exceptions, traces, or failed dependencies).
    /// When no messages match the provided filters, the result contains an empty results list and zero counts.
    /// </returns>
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
