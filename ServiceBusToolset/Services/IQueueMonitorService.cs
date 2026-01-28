using ServiceBusToolset.Models;

namespace ServiceBusToolset.Services;

public interface IQueueMonitorService
{
    IObservable<IReadOnlyList<QueueStatistics>> ObserveQueues(
        string fullyQualifiedNamespace,
        string? queueFilter,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken);
}
