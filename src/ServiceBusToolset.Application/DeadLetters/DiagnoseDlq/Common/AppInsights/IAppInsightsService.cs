using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;

public interface IAppInsightsService
{
    void Initialize(string appInsightsResourceId);

    Task<Dictionary<string, DiagnosticResult>> DiagnoseBatchAsync(
        IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)> operations,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken);
}
