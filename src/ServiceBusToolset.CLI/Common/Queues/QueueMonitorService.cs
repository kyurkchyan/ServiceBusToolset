using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

namespace ServiceBusToolset.CLI.Common.Queues;

public class QueueMonitorService(IServiceBusClientFactory clientFactory) : IQueueMonitorService
{
    public IObservable<IReadOnlyList<QueueStatistics>> ObserveQueues(
        string fullyQualifiedNamespace,
        string? queueFilter,
        TimeSpan refreshInterval,
        CancellationToken cancellationToken)
    {
        var adminClient = clientFactory.CreateAdministrationClient(fullyQualifiedNamespace);
        var filterPredicate = CreateFilterPredicate(queueFilter);

        return Observable
               .Timer(TimeSpan.Zero, refreshInterval)
               .TakeUntil(Observable.Create<long>(observer =>
               {
                   cancellationToken.Register(() => observer.OnNext(0));
                   return () => { };
               }))
               .SelectMany(_ => Observable.FromAsync(ct => FetchQueueStatisticsAsync(adminClient, filterPredicate, ct)))
               .DistinctUntilChanged(new QueueStatisticsListComparer());
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

        // Check if filter contains wildcards
        if (filter.Contains('*') || filter.Contains('?'))
        {
            // Convert wildcard pattern to regex
            var regexPattern = "^" + Regex.Escape(filter)
                                          .Replace("\\*", ".*")
                                          .Replace("\\?", ".") + "$";
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return name => regex.IsMatch(name);
        }

        // Default to contains match
        return name => name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private class QueueStatisticsListComparer : IEqualityComparer<IReadOnlyList<QueueStatistics>>
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
