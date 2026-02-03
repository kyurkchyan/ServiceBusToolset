using System.Reactive.Linq;
using Ardalis.Result;
using Azure.Messaging.ServiceBus.Administration;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Queues.MonitorQueues.Models;

namespace ServiceBusToolset.Application.Queues.MonitorQueues;

public sealed class MonitorQueuesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<MonitorQueuesCommand, Result<MonitorQueuesResult>>
{
    public ValueTask<Result<MonitorQueuesResult>> Handle(
        MonitorQueuesCommand command,
        CancellationToken cancellationToken)
    {
        var adminClient = clientFactory.CreateAdministrationClient(command.FullyQualifiedNamespace);
        var filterPredicate = WildcardFilterHelper.CreateFilterPredicate(command.QueueFilter);

        var observable = Observable
                         .Timer(TimeSpan.Zero, command.RefreshInterval)
                         .TakeUntil(Observable.Create<long>(observer =>
                         {
                             command.CancellationToken.Register(() => observer.OnNext(0));
                             return () => { };
                         }))
                         .SelectMany(_ => Observable.FromAsync(ct =>
                                                                   FetchQueueStatisticsAsync(adminClient,
                                                                                             filterPredicate,
                                                                                             ct)))
                         .DistinctUntilChanged(new StatisticsListComparer<QueueStatistics>());

        return ValueTask.FromResult(Result.Success(new MonitorQueuesResult(observable)));
    }

    private static async Task<IReadOnlyList<QueueStatistics>> FetchQueueStatisticsAsync(
        ServiceBusAdministrationClient adminClient,
        Func<string, bool> filterPredicate,
        CancellationToken cancellationToken)
    {
        var statistics = new List<QueueStatistics>();
        var now = DateTimeOffset.UtcNow;

        await foreach (var queue in adminClient.GetQueuesRuntimePropertiesAsync(cancellationToken))
        {
            if (!filterPredicate(queue.Name))
            {
                continue;
            }

            statistics.Add(new QueueStatistics(queue.Name,
                                               queue.ActiveMessageCount,
                                               queue.DeadLetterMessageCount,
                                               queue.ScheduledMessageCount,
                                               now));
        }

        return statistics.OrderBy(q => q.Name).ToList();
    }
}
