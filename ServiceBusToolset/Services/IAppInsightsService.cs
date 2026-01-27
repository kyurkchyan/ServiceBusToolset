using ServiceBusToolset.Models;

namespace ServiceBusToolset.Services;

public interface IAppInsightsService
{
    void Initialize(string appInsightsResourceId);
    Task<DiagnosticResult> DiagnoseMessageAsync(string operationId, DateTimeOffset enqueuedTime, CancellationToken cancellationToken);
}
