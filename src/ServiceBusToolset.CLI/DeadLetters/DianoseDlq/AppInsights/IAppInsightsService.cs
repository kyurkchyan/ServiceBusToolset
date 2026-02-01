namespace ServiceBusToolset.CLI.DeadLetters.DianoseDlq.AppInsights;

public interface IAppInsightsService
{
    void Initialize(string appInsightsResourceId);

    Task<Dictionary<string, DiagnosticResult>> DiagnoseBatchAsync(
        IReadOnlyList<(string OperationId, DateTimeOffset EnqueuedTime)> operations,
        Action<int, int>? onProgress,
        CancellationToken cancellationToken);
}
