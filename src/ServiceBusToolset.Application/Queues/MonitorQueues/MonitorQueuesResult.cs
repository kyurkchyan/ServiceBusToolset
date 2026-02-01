using ServiceBusToolset.Application.Queues.MonitorQueues.Models;

namespace ServiceBusToolset.Application.Queues.MonitorQueues;

public sealed record MonitorQueuesResult(IObservable<IReadOnlyList<QueueStatistics>> QueueStatistics);
