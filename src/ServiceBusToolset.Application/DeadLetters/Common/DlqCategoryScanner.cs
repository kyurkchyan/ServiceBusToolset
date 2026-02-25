using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class DlqCategoryScanner
{
    public static DlqCategorySnapshot BuildCategorySnapshot(
        ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
        bool mergeSimilar = false)
    {
        var snapshot = cache.Snapshot();
        var categoryCounts = new Dictionary<DlqCategoryKey, int>();

        foreach (var msg in snapshot)
        {
            var key = DlqCategoryKey.FromMessage(msg.Subject, msg.DeadLetterReason);
            categoryCounts[key] = categoryCounts.GetValueOrDefault(key, 0) + 1;
        }

        var categories = categoryCounts
                         .OrderByDescending(kvp => kvp.Value)
                         .Select(kvp => new DlqCategory(kvp.Key.Label, kvp.Key.DeadLetterReason, kvp.Value))
                         .ToList();

        if (!mergeSimilar)
        {
            return new DlqCategorySnapshot(categories, snapshot.Count, cache.IsComplete);
        }

        var mergeResult = CategoryMerger.Merge(categories);
        return new DlqCategorySnapshot(mergeResult.MergedCategories,
                                       snapshot.Count,
                                       cache.IsComplete,
                                       mergeResult);
    }

    public static async Task FeedCacheAsync(
        IServiceBusClientFactory clientFactory,
        string fullyQualifiedNamespace,
        EntityTarget target,
        ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
        DlqScanSession session,
        Func<ServiceBusReceivedMessage, bool>? messageFilter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                var adminClient = clientFactory.CreateAdministrationClient(fullyQualifiedNamespace);
                session.TotalDlqCount = target.IsQueueMode
                                            ? (await adminClient.GetQueueRuntimePropertiesAsync(target.Queue!, cancellationToken)).Value.DeadLetterMessageCount
                                            : (await adminClient.GetSubscriptionRuntimePropertiesAsync(target.Topic!, target.Subscription!, cancellationToken)).Value.DeadLetterMessageCount;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best-effort: total count is optional for the scanning UX
            }

            await using var client = clientFactory.CreateClient(fullyQualifiedNamespace);
            await using var receiver = ReceiverFactory.CreateDlqReceiver(client, target);

            long? fromSequenceNumber = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<ServiceBusReceivedMessage> messages;

                if (fromSequenceNumber.HasValue)
                {
                    messages = await receiver.PeekMessagesAsync(MessageOperations.DefaultBatchSize,
                                                                fromSequenceNumber.Value,
                                                                cancellationToken);
                }
                else
                {
                    messages = await receiver.PeekMessagesAsync(MessageOperations.DefaultBatchSize,
                                                                cancellationToken: cancellationToken);
                }

                if (messages.Count == 0)
                {
                    break;
                }

                var toAdd = messageFilter != null
                                ? messages.Where(messageFilter).ToList()
                                : messages;

                if (toAdd.Count > 0)
                {
                    cache.AddOrUpdate(toAdd);
                }

                fromSequenceNumber = messages[^1].SequenceNumber + 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.Error = ex;
        }
        finally
        {
            cache.MarkComplete();
            session.ScanCompletion.TrySetResult();
        }
    }
}
