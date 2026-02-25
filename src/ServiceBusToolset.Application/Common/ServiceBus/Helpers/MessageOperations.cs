using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.Common.ServiceBus.Helpers;

/// <summary>
/// Provides common batch operations for Service Bus messages.
/// </summary>
public static class MessageOperations
{
    /// <summary>
    /// Default batch size for peeking messages.
    /// </summary>
    public const int DefaultBatchSize = 100;

    /// <summary>
    /// Default number of consecutive empty batches before stopping.
    /// </summary>
    public const int DefaultEmptyBatchThreshold = 3;

    /// <summary>
    /// Peeks all available messages from the receiver.
    /// </summary>
    public static async Task<List<ServiceBusReceivedMessage>> PeekAllAsync(
        ServiceBusReceiver receiver,
        int batchSize = DefaultBatchSize,
        int emptyBatchThreshold = DefaultEmptyBatchThreshold,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < emptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(batchSize, cancellationToken: cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            allMessages.AddRange(messages);

            progress?.Report(allMessages.Count);
        }

        return allMessages;
    }

    /// <summary>
    /// Peeks messages up to a maximum count from the receiver.
    /// </summary>
    public static async Task<List<ServiceBusReceivedMessage>> PeekAsync(
        ServiceBusReceiver receiver,
        int maxMessages,
        int batchSize = DefaultBatchSize,
        int emptyBatchThreshold = DefaultEmptyBatchThreshold,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested &&
               emptyBatches < emptyBatchThreshold &&
               allMessages.Count < maxMessages)
        {
            var remaining = maxMessages - allMessages.Count;
            var currentBatchSize = Math.Min(batchSize, remaining);

            var messages = await receiver.PeekMessagesAsync(currentBatchSize, cancellationToken: cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            allMessages.AddRange(messages);

            progress?.Report(allMessages.Count);
        }

        return allMessages;
    }

    /// <summary>
    /// Counts messages that match a time filter without loading all messages into memory.
    /// </summary>
    public static async Task<FilteredMessageCount> CountWithTimeFilterAsync(
        ServiceBusReceiver receiver,
        DateTimeOffset beforeTime,
        int batchSize = DefaultBatchSize,
        int emptyBatchThreshold = DefaultEmptyBatchThreshold,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalCount = 0;
        var filteredCount = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < emptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(batchSize, cancellationToken: cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            totalCount += messages.Count;
            filteredCount += messages.Count(m => m.EnqueuedTime < beforeTime);

            progress?.Report(totalCount);
        }

        return new FilteredMessageCount(filteredCount, totalCount);
    }
}
