using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public class DlqScanSession(ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
                            IObservable<DlqCategorySnapshot> categoryStream,
                            CategorizationSchema? schema = null,
                            CategoryPropertyResolver? resolver = null)
    : IDisposable
{
    private readonly CancellationTokenSource _scanCts = new();

    public ReactiveMessageCache<ServiceBusReceivedMessage, long> Cache { get; } = cache;
    public IObservable<DlqCategorySnapshot> CategoryStream { get; } = categoryStream;
    public CategorizationSchema Schema { get; } = schema ?? CategorizationSchema.Default;
    public CategoryPropertyResolver Resolver { get; } = resolver ?? new CategoryPropertyResolver();
    public TaskCompletionSource ScanCompletion { get; } = new();
    public long? TotalDlqCount { get; set; }
    public Exception? Error { get; set; }

    public CancellationToken ScanCancellationToken => _scanCts.Token;

    public void StopScanning() => _scanCts.Cancel();

    public IReadOnlyList<ServiceBusReceivedMessage> SnapshotForCategories(
        IReadOnlySet<DlqCategoryKey> categoryKeys,
        DateTimeOffset? beforeTime = null)
    {
        var snapshot = Cache.Snapshot();

        return snapshot
               .Where(m => MatchesFilter(m, categoryKeys, beforeTime))
               .ToList();
    }

    /// <summary>
    /// Determines whether the given dead-letter message belongs to one of the specified categories and, if a cutoff is provided, was enqueued before that cutoff.
    /// </summary>
    /// <param name="message">The dead-letter Service Bus message to evaluate.</param>
    /// <param name="categoryKeys">The set of category keys to match the message against.</param>
    /// <param name="beforeTime">Optional cutoff time; when provided only messages with <c>EnqueuedTime</c> earlier than this value match.</param>
    /// <returns><c>true</c> if the message's category is contained in <paramref name="categoryKeys"/> and, when <paramref name="beforeTime"/> is provided, the message was enqueued earlier than that time; <c>false</c> otherwise.</returns>
    protected virtual bool MatchesFilter(
        ServiceBusReceivedMessage message,
        IReadOnlySet<DlqCategoryKey> categoryKeys,
        DateTimeOffset? beforeTime)
    {
        var key = DlqCategoryKey.FromMessage(message, Schema, Resolver);
        if (!categoryKeys.Contains(key))
        {
            return false;
        }

        if (beforeTime.HasValue && message.EnqueuedTime >= beforeTime.Value)
        {
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanCts.Cancel();
            _scanCts.Dispose();
            Cache.Dispose();
        }
    }
}
