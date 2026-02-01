namespace ServiceBusToolset.Application.Common.ServiceBus.Models;

/// <summary>
/// Represents a Service Bus entity target - either a queue or a topic/subscription pair.
/// </summary>
public sealed record EntityTarget
{
    public string? Queue { get; }
    public string? Topic { get; }
    public string? Subscription { get; }

    private EntityTarget(string? queue, string? topic, string? subscription)
    {
        Queue = queue;
        Topic = topic;
        Subscription = subscription;
    }

    public bool IsQueueMode => !string.IsNullOrEmpty(Queue);
    public bool IsSubscriptionMode => !string.IsNullOrEmpty(Topic) && !string.IsNullOrEmpty(Subscription);

    public static EntityTarget ForQueue(string queue) => new(queue, null, null);

    public static EntityTarget ForSubscription(string topic, string subscription) => new(null, topic, subscription);

    public string GetDescription()
    {
        if (IsQueueMode)
        {
            return $"queue '{Queue}'";
        }

        return $"topic '{Topic}' subscription '{Subscription}'";
    }
}
