using System.Reactive.Linq;
using Ardalis.Result;
using Azure.Messaging.ServiceBus.Administration;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions.Models;

namespace ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions;

public sealed class MonitorSubscriptionsCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<MonitorSubscriptionsCommand, Result<MonitorSubscriptionsResult>>
{
    public ValueTask<Result<MonitorSubscriptionsResult>> Handle(
        MonitorSubscriptionsCommand command,
        CancellationToken cancellationToken)
    {
        var adminClient = clientFactory.CreateAdministrationClient(command.FullyQualifiedNamespace);
        var topicFilterPredicate = WildcardFilterHelper.CreateFilterPredicate(command.TopicFilter);
        var subscriptionFilterPredicate = WildcardFilterHelper.CreateFilterPredicate(command.SubscriptionFilter);

        var observable = Observable
                         .Timer(TimeSpan.Zero, command.RefreshInterval)
                         .TakeUntil(Observable.Create<long>(observer =>
                         {
                             command.CancellationToken.Register(() => observer.OnNext(0));
                             return () => { };
                         }))
                         .SelectMany(_ => Observable.FromAsync(ct =>
                                                                   FetchSubscriptionStatisticsAsync(adminClient,
                                                                                                    topicFilterPredicate,
                                                                                                    subscriptionFilterPredicate,
                                                                                                    ct)))
                         .DistinctUntilChanged(new StatisticsListComparer<SubscriptionStatistics>());

        return ValueTask.FromResult(Result.Success(new MonitorSubscriptionsResult(observable)));
    }

    private static async Task<IReadOnlyList<SubscriptionStatistics>> FetchSubscriptionStatisticsAsync(
        ServiceBusAdministrationClient adminClient,
        Func<string, bool> topicFilterPredicate,
        Func<string, bool> subscriptionFilterPredicate,
        CancellationToken cancellationToken)
    {
        var statistics = new List<SubscriptionStatistics>();
        var now = DateTimeOffset.UtcNow;

        await foreach (var topic in adminClient.GetTopicsAsync(cancellationToken))
        {
            if (!topicFilterPredicate(topic.Name))
            {
                continue;
            }

            await foreach (var subscription in adminClient.GetSubscriptionsRuntimePropertiesAsync(topic.Name, cancellationToken))
            {
                if (!subscriptionFilterPredicate(subscription.SubscriptionName))
                {
                    continue;
                }

                statistics.Add(new SubscriptionStatistics(topic.Name,
                                                          subscription.SubscriptionName,
                                                          subscription.ActiveMessageCount,
                                                          subscription.DeadLetterMessageCount,
                                                          0, // ScheduledMessageCount is not available on subscriptions
                                                          now));
            }
        }

        return statistics
               .OrderBy(s => s.TopicName)
               .ThenBy(s => s.SubscriptionName)
               .ToList();
    }
}
