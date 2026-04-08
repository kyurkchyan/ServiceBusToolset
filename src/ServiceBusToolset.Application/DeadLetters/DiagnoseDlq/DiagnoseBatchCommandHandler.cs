using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed class DiagnoseBatchCommandHandler(IAppInsightsService appInsightsService)
    : ICommandHandler<DiagnoseBatchCommand, Result<IReadOnlyList<DiagnosticResult>>>
{
    public async ValueTask<Result<IReadOnlyList<DiagnosticResult>>> Handle(
        DiagnoseBatchCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Operations.Count == 0)
        {
            return Result.Success<IReadOnlyList<DiagnosticResult>>([]);
        }

        appInsightsService.Initialize(command.AppInsightsResourceId);

        var operations = command.Operations
                                .Select(op => (op.OperationId, op.EnqueuedTime))
                                .ToList();

        var diagnosticResults = await appInsightsService.DiagnoseBatchAsync(operations,
                                                                            null,
                                                                            cancellationToken);

        var results = new List<DiagnosticResult>();
        // Deduplicate by OperationId — take the first occurrence if duplicates exist
        var operationsById = command.Operations
                                    .DistinctBy(op => op.OperationId)
                                    .ToDictionary(op => op.OperationId);

        foreach (var (operationId, result) in diagnosticResults)
        {
            if (operationsById.TryGetValue(operationId, out var operation))
            {
                result.MessageId = operation.MessageId;
                result.Subject = operation.Subject;
                result.DeadLetterReason = operation.DeadLetterReason;
                results.Add(result);
            }
        }

        return Result.Success<IReadOnlyList<DiagnosticResult>>(results);
    }
}
