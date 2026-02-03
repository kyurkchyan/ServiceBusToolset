using ServiceBusToolset.Application.Common.ServiceBus.Helpers;

namespace ServiceBusToolset.Application.Queues.MonitorQueues.Models;

public record QueueStatistics(string Name,
                              long ActiveMessageCount,
                              long DeadLetterMessageCount,
                              long ScheduledMessageCount,
                              DateTimeOffset UpdatedAt) : IHasComparableCounts<QueueStatistics>
{
    public bool HasSameCountsAs(QueueStatistics other) =>
        Name == other.Name &&
        ActiveMessageCount == other.ActiveMessageCount &&
        DeadLetterMessageCount == other.DeadLetterMessageCount &&
        ScheduledMessageCount == other.ScheduledMessageCount;

    public void AddToHashCode(ref HashCode hash)
    {
        hash.Add(Name);
        hash.Add(ActiveMessageCount);
        hash.Add(DeadLetterMessageCount);
        hash.Add(ScheduledMessageCount);
    }
}
