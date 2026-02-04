using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.Tests.Common.Builders;

/// <summary>
/// Builder for creating EntityTarget instances for testing.
/// </summary>
public static class EntityTargetBuilder
{
    /// <summary>
    /// Creates a queue target with the specified name.
    /// </summary>
    public static EntityTarget Queue(string queueName = "test-queue")
        => EntityTarget.ForQueue(queueName);

    /// <summary>
    /// Creates a subscription target with the specified topic and subscription names.
    /// </summary>
    public static EntityTarget Subscription(string topicName = "test-topic", string subscriptionName = "test-subscription")
        => EntityTarget.ForSubscription(topicName, subscriptionName);
}
