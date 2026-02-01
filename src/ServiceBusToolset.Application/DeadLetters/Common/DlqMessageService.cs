using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;
using ServiceBusMessage = ServiceBusToolset.Application.Common.ServiceBus.Models.ServiceBusMessage;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqMessageService(IServiceBusClientFactory clientFactory)
{
    private const int MaxBatchSize = 100;
    private const int EmptyBatchThreshold = 3;

    public async Task<long> GetMessageCountAsync(
        string fullyQualifiedNamespace,
        EntityTarget target,
        CancellationToken cancellationToken)
    {
        var adminClient = clientFactory.CreateAdministrationClient(fullyQualifiedNamespace);

        if (target.IsQueueMode)
        {
            var props = await adminClient.GetQueueRuntimePropertiesAsync(target.Queue!, cancellationToken);
            return props.Value.DeadLetterMessageCount;
        }

        var subProps = await adminClient.GetSubscriptionRuntimePropertiesAsync(target.Topic!, target.Subscription!, cancellationToken);
        return subProps.Value.DeadLetterMessageCount;
    }

    public static async Task<FilteredMessageCount> CountMessagesWithFilterAsync(
        ServiceBusClient client,
        EntityTarget target,
        DateTimeOffset beforeTime,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client, target);

        var count = 0;
        var filteredCount = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken:cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;
            count += messages.Count;
            filteredCount += messages.Count(m => m.EnqueuedTime < beforeTime);

            progress?.Report(count);
        }

        return new FilteredMessageCount(filteredCount, count);
    }

    public static async Task<List<DlqCategory>> AnalyzeCategoriesAsync(
        ServiceBusClient client,
        EntityTarget target,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client, target);

        var categoryCounts = new Dictionary<DlqCategoryKey, int>();
        var totalPeeked = 0;
        long? fromSequenceNumber = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages;

            if (fromSequenceNumber.HasValue)
            {
                messages = await receiver.PeekMessagesAsync(MaxBatchSize, fromSequenceNumber.Value, cancellationToken);
            }
            else
            {
                messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken:cancellationToken);
            }

            if (messages.Count == 0)
            {
                break;
            }

            foreach (var msg in messages)
            {
                var key = DlqCategoryKey.FromMessage(msg.Subject, msg.DeadLetterReason);
                categoryCounts[key] = categoryCounts.GetValueOrDefault(key, 0) + 1;
            }

            totalPeeked += messages.Count;
            fromSequenceNumber = messages[^1].SequenceNumber + 1;

            progress?.Report(totalPeeked);
        }

        return categoryCounts
               .OrderByDescending(kvp => kvp.Value)
               .Select(kvp => new DlqCategory(kvp.Key.Label, kvp.Key.DeadLetterReason, kvp.Value))
               .ToList();
    }

    public static async Task<List<ServiceBusReceivedMessage>> PeekAllMessagesAsync(
        ServiceBusClient client,
        EntityTarget target,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client, target);

        var allMessages = new List<ServiceBusReceivedMessage>();
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken:cancellationToken);

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

    public static IReadOnlyList<ServiceBusReceivedMessage> FilterByTime(
        IEnumerable<ServiceBusReceivedMessage> messages,
        DateTimeOffset? beforeTime)
        => MessageFilters.FilterByEnqueueTime(messages, beforeTime);

    public static IReadOnlyList<ServiceBusReceivedMessage> FilterByCategories(
        IEnumerable<ServiceBusReceivedMessage> messages,
        IReadOnlySet<DlqCategoryKey> categories)
    {
        return messages
               .Where(m => categories.Contains(DlqCategoryKey.FromMessage(m.Subject, m.DeadLetterReason)))
               .ToList();
    }

    public static List<ServiceBusMessage> ConvertToDto(IEnumerable<ServiceBusReceivedMessage> messages)
        => MessageSerializer.ToDtoList(messages);

    public Task WriteJsonAsync(string filePath, List<ServiceBusMessage> messages, CancellationToken cancellationToken)
        => MessageSerializer.WriteJsonAsync(filePath, messages, cancellationToken);

    private static ServiceBusReceiver CreateDlqReceiver(ServiceBusClient client, EntityTarget target)
    {
        var options = new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        };

        if (target.IsQueueMode)
        {
            return client.CreateReceiver(target.Queue!, options);
        }

        return client.CreateReceiver(target.Topic!, target.Subscription!, options);
    }
}
