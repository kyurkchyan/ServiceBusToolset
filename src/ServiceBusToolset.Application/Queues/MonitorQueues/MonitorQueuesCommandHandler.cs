using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Ardalis.Result;
using Azure.Messaging.ServiceBus.Administration;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
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
        var filterPredicate = CreateFilterPredicate(command.QueueFilter);

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
                         .DistinctUntilChanged(new QueueStatisticsListComparer());

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

    private static Func<string, bool> CreateFilterPredicate(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return _ => true;
        }

        if (filter.Contains('*') || filter.Contains('?'))
        {
            var regexPattern = "^" + Regex.Escape(filter)
                                          .Replace("\\*", ".*")
                                          .Replace("\\?", ".") + "$";
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return name => regex.IsMatch(name);
        }

        return name => name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class QueueStatisticsListComparer : IEqualityComparer<IReadOnlyList<QueueStatistics>>
    {
        public bool Equals(IReadOnlyList<QueueStatistics>? x, IReadOnlyList<QueueStatistics>? y)
        {
            if (x is null && y is null)
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            return !x.Where((t, i) => !t.HasSameCountsAs(y[i])).Any();
        }

        public int GetHashCode(IReadOnlyList<QueueStatistics> obj)
        {
            var hash = new HashCode();
            foreach (var stat in obj)
            {
                hash.Add(stat.Name);
                hash.Add(stat.ActiveMessageCount);
                hash.Add(stat.DeadLetterMessageCount);
                hash.Add(stat.ScheduledMessageCount);
            }

            return hash.ToHashCode();
        }
    }
}
