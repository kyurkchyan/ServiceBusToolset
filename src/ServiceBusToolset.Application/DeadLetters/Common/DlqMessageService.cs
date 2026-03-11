using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqMessageService(IServiceBusClientFactory clientFactory)
{
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
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client, target);
        return await MessageOperations.CountWithTimeFilterAsync(receiver,
                                                                beforeTime,
                                                                progress:progress,
                                                                cancellationToken:cancellationToken);
    }

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
