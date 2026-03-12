using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class DlqCategoryScanner
{
    /// <summary>
    /// Build a snapshot of dead-letter categories from the provided message cache.
    /// </summary>
    /// <param name="cache">Reactive cache of DLQ messages to analyze.</param>
    /// <param name="mergeSimilar">If true, merge similar categories into combined entries before returning.</param>
    /// <param name="schema">Optional categorization schema to use; when null, <see cref="CategorizationSchema.Default"/> is used.</param>
    /// <param name="resolver">Optional resolver for category properties; when null, a new <see cref="CategoryPropertyResolver"/> is used.</param>
    /// <returns>
    /// A <see cref="DlqCategorySnapshot"/> containing ordered category entries, the total message count observed in the snapshot, whether the cache is complete, and the effective categorization schema. 
    /// If <paramref name="mergeSimilar"/> is true, the snapshot includes merged category results.
    /// </returns>
    public static DlqCategorySnapshot BuildCategorySnapshot(
        ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
        bool mergeSimilar = false,
        CategorizationSchema? schema = null,
        CategoryPropertyResolver? resolver = null)
    {
        var effectiveSchema = schema ?? CategorizationSchema.Default;
        var effectiveResolver = resolver ?? new CategoryPropertyResolver();

        var snapshot = cache.Snapshot();
        var categoryCounts = new Dictionary<DlqCategoryKey, int>();

        foreach (var msg in snapshot)
        {
            var key = DlqCategoryKey.FromMessage(msg, effectiveSchema, effectiveResolver);
            categoryCounts[key] = categoryCounts.GetValueOrDefault(key, 0) + 1;
        }

        var categories = categoryCounts
                         .OrderByDescending(kvp => kvp.Value)
                         .Select(kvp => DlqCategory.FromKey(kvp.Key, kvp.Value))
                         .ToList();

        if (!mergeSimilar)
        {
            return new DlqCategorySnapshot(categories,
                                           snapshot.Count,
                                           cache.IsComplete,
                                           Schema:effectiveSchema);
        }

        var mergeResult = CategoryMerger.Merge(categories, effectiveSchema);
        return new DlqCategorySnapshot(mergeResult.MergedCategories,
                                       snapshot.Count,
                                       cache.IsComplete,
                                       mergeResult,
                                       effectiveSchema);
    }

    /// <summary>
    /// Populates the provided DLQ message cache by peeking dead-letter messages from the specified Service Bus target and updates the scan session with progress and errors.
    /// </summary>
    /// <param name="session">Session object used to report progress, total DLQ count (if available), and any error encountered during the scan.</param>
    /// <param name="messageFilter">Optional predicate to filter messages before they are added to the cache; if null all peeked messages are cached.</param>
    /// <param name="cancellationToken">Token to cancel the scanning operation.</param>
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
                                                                cancellationToken:cancellationToken);
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
