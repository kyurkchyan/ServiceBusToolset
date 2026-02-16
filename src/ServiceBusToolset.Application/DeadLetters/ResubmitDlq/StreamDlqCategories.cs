using System.Reactive.Linq;
using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record StreamDlqCategoriesCommand(string FullyQualifiedNamespace,
                                                EntityTarget Target,
                                                bool MergeSimilar = false) : ICommand<Result<DlqResubmitSession>>;

public sealed class StreamDlqCategoriesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<StreamDlqCategoriesCommand, Result<DlqResubmitSession>>
{
    public ValueTask<Result<DlqResubmitSession>> Handle(
        StreamDlqCategoriesCommand command,
        CancellationToken cancellationToken)
    {
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var tracker = new ResubmitTracker();

        var categoryStream = cache.Connect()
                                  .Sample(TimeSpan.FromSeconds(1))
                                  .Select(_ => BuildCategorySnapshot(cache, command.MergeSimilar))
                                  .StartWith(new DlqCategorySnapshot([], 0, false));

        var session = new DlqResubmitSession(cache, categoryStream, tracker);

        _ = Task.Run(async () =>
                     {
                         using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ScanCancellationToken);
                         await FeedCacheAsync(clientFactory,
                                              command,
                                              cache,
                                              tracker,
                                              session,
                                              linkedCts.Token);
                     },
                     cancellationToken);

        return new ValueTask<Result<DlqResubmitSession>>(Result.Success(session));
    }

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
        StreamDlqCategoriesCommand command,
        ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
        ResubmitTracker tracker,
        DlqResubmitSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                var adminClient = clientFactory.CreateAdministrationClient(command.FullyQualifiedNamespace);
                session.TotalDlqCount = command.Target.IsQueueMode
                                            ? (await adminClient.GetQueueRuntimePropertiesAsync(command.Target.Queue!, cancellationToken)).Value.DeadLetterMessageCount
                                            : (await adminClient.GetSubscriptionRuntimePropertiesAsync(command.Target.Topic!, command.Target.Subscription!, cancellationToken)).Value.DeadLetterMessageCount;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best-effort: total count is optional for the scanning UX
            }

            await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);
            await using var receiver = ReceiverFactory.CreateDlqReceiver(client, command.Target);

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

                var filtered = messages
                               .Where(m => !tracker.WasResubmitted(m.MessageId))
                               .ToList();

                if (filtered.Count > 0)
                {
                    cache.AddOrUpdate(filtered);
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
