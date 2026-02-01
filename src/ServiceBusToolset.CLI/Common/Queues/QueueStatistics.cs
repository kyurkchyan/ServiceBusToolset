namespace ServiceBusToolset.CLI.Common.Queues;

public record QueueStatistics(string Name,
                              long ActiveMessageCount,
                              long DeadLetterMessageCount,
                              long ScheduledMessageCount,
                              DateTimeOffset UpdatedAt)
{
    public bool HasSameCountsAs(QueueStatistics other) =>
        Name == other.Name &&
        ActiveMessageCount == other.ActiveMessageCount &&
        DeadLetterMessageCount == other.DeadLetterMessageCount &&
        ScheduledMessageCount == other.ScheduledMessageCount;
}
