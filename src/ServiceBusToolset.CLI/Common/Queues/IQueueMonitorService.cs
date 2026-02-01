namespace ServiceBusToolset.CLI.Common.Queues;

public interface IQueueMonitorService
{
    IObservable<IReadOnlyList<QueueStatistics>> ObserveQueues(
        string fullyQualifiedNamespace,
        string? queueFilter,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken);
}
