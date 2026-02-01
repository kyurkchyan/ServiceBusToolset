using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.Common.ServiceBus.Helpers;

/// <summary>
/// Provides common filtering operations for Service Bus messages.
/// </summary>
public static class MessageFilters
{
    /// <summary>
    /// Filters messages to only include those enqueued before the specified time.
    /// </summary>
    public static IReadOnlyList<ServiceBusReceivedMessage> FilterByEnqueueTime(
        IEnumerable<ServiceBusReceivedMessage> messages,
        DateTimeOffset? beforeTime)
    {
        if (!beforeTime.HasValue)
        {
            return messages.ToList();
        }

        return messages.Where(m => m.EnqueuedTime < beforeTime.Value).ToList();
    }
}
