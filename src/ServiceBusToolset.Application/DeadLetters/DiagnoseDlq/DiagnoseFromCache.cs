using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed record DiagnoseFromCacheCommand(string AppInsightsResourceId,
                                              IReadOnlyList<ServiceBusReceivedMessage> MessagesToDiagnose,
                                              IProgress<(int Current, int Total)>? BatchProgress = null) : ICommand<Result<DiagnoseDlqResult>>;

public sealed class DiagnoseFromCacheCommandHandler(IAppInsightsService appInsightsService)
    : ICommandHandler<DiagnoseFromCacheCommand, Result<DiagnoseDlqResult>>
{
    public async ValueTask<Result<DiagnoseDlqResult>> Handle(
        DiagnoseFromCacheCommand command,
        CancellationToken cancellationToken)
    {
        appInsightsService.Initialize(command.AppInsightsResourceId);

        var messages = command.MessagesToDiagnose.ToList();

        if (messages.Count == 0)
        {
            return Result.Success(new DiagnoseDlqResult([],
                                                        0,
                                                        0,
                                                        0));
        }

        var (results, skipped) = await MessageDiagnostics.DiagnoseMessagesAsync(appInsightsService,
                                                                                messages,
                                                                                command.BatchProgress,
                                                                                cancellationToken);

        var resultsWithTelemetry = results
            .Count(r => r.Exceptions.Count > 0 || r.Traces.Count > 0 || r.FailedDependencies.Count > 0);

        return Result.Success(new DiagnoseDlqResult(results,
                                                    messages.Count,
                                                    skipped,
                                                    resultsWithTelemetry));
    }
}
