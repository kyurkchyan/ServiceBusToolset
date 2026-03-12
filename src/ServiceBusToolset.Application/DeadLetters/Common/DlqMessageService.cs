using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqMessageService(IServiceBusClientFactory clientFactory)
{
    /// <summary>
    /// Retrieves the number of dead-letter messages for the specified Service Bus entity.
    /// </summary>
    /// <param name="fullyQualifiedNamespace">The Service Bus fully qualified namespace that hosts the entity.</param>
    /// <param name="target">Specifies the target entity; if <see cref="EntityTarget.IsQueueMode"/> is true the queue indicated by <see cref="EntityTarget.Queue"/> is used, otherwise the topic and subscription indicated by <see cref="EntityTarget.Topic"/> and <see cref="EntityTarget.Subscription"/> are used.</param>
    /// <returns>The dead-letter message count for the selected queue or subscription.</returns>
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

    /// <summary>
    /// Counts dead-letter messages in the specified target that were enqueued before the provided time.
    /// </summary>
    /// <param name="target">The queue or topic/subscription to inspect.</param>
    /// <param name="beforeTime">Only messages enqueued before this time are counted.</param>
    /// <param name="progress">Optional progress reporter that receives the number of messages processed.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A <see cref="FilteredMessageCount"/> containing the counts of messages that match the time filter.</returns>
    public static async Task<FilteredMessageCount> CountMessagesWithFilterAsync(
        ServiceBusClient client,
        EntityTarget target,
        DateTimeOffset beforeTime,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, target);
        return await MessageOperations.CountWithTimeFilterAsync(receiver,
                                                                beforeTime,
                                                                progress:progress,
                                                                cancellationToken:cancellationToken);
    }

    /// <summary>
    /// Retrieves all messages from the dead-letter queue for the specified entity target.
    /// </summary>
    /// <param name="client">The Service Bus client used to create a dead-letter receiver.</param>
    /// <param name="target">The queue or topic/subscription target whose dead-letter queue will be peeked.</param>
    /// <param name="progress">Optional progress reporter that receives the number of messages processed so far.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A list containing all messages currently in the target's dead-letter queue.</returns>
    public static async Task<List<ServiceBusReceivedMessage>> PeekAllMessagesAsync(
        ServiceBusClient client,
        EntityTarget target,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, target);
        return await MessageOperations.PeekAllAsync(receiver,
                                                    progress:progress,
                                                    cancellationToken:cancellationToken);
    }

    /// <summary>
    /// Filters a sequence of dead-letter messages to those whose category key is contained in the provided set.
    /// </summary>
    /// <param name="messages">The dead-letter messages to filter.</param>
    /// <param name="categories">The set of category keys to retain.</param>
    /// <param name="schema">Optional categorization schema to derive category keys; when null, <see cref="CategorizationSchema.Default"/> is used.</param>
    /// <param name="resolver">Optional property resolver used when deriving category keys; when null, a new <see cref="CategoryPropertyResolver"/> is used.</param>
    /// <returns>A list of messages whose derived <see cref="DlqCategoryKey"/> is present in <paramref name="categories"/>.</returns>
    public static IReadOnlyList<ServiceBusReceivedMessage> FilterByCategories(
        IEnumerable<ServiceBusReceivedMessage> messages,
        IReadOnlySet<DlqCategoryKey> categories,
        CategorizationSchema? schema = null,
        CategoryPropertyResolver? resolver = null)
    {
        var effectiveSchema = schema ?? CategorizationSchema.Default;
        var effectiveResolver = resolver ?? new CategoryPropertyResolver();

        return messages
               .Where(m => categories.Contains(DlqCategoryKey.FromMessage(m, effectiveSchema, effectiveResolver)))
               .ToList();
    }
}
