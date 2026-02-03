using ServiceBusToolset.Application.Common.ServiceBus.Helpers;

namespace ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions.Models;

public record SubscriptionStatistics(string TopicName,
                                     string SubscriptionName,
                                     long ActiveMessageCount,
                                     long DeadLetterMessageCount,
                                     long ScheduledMessageCount,
                                     DateTimeOffset UpdatedAt) : IHasComparableCounts<SubscriptionStatistics>
{
    public bool HasSameCountsAs(SubscriptionStatistics other) =>
        TopicName == other.TopicName &&
        SubscriptionName == other.SubscriptionName &&
        ActiveMessageCount == other.ActiveMessageCount &&
        DeadLetterMessageCount == other.DeadLetterMessageCount &&
        ScheduledMessageCount == other.ScheduledMessageCount;

    public void AddToHashCode(ref HashCode hash)
    {
        hash.Add(TopicName);
        hash.Add(SubscriptionName);
        hash.Add(ActiveMessageCount);
        hash.Add(DeadLetterMessageCount);
        hash.Add(ScheduledMessageCount);
    }
}
