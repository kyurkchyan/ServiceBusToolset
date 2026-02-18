using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public class DlqScanSession(ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
                            IObservable<DlqCategorySnapshot> categoryStream)
    : IDisposable
{
    private readonly CancellationTokenSource _scanCts = new();

    public ReactiveMessageCache<ServiceBusReceivedMessage, long> Cache { get; } = cache;
    public IObservable<DlqCategorySnapshot> CategoryStream { get; } = categoryStream;
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

    protected virtual bool MatchesFilter(
        ServiceBusReceivedMessage message,
        IReadOnlySet<DlqCategoryKey> categoryKeys,
        DateTimeOffset? beforeTime)
    {
        var key = DlqCategoryKey.FromMessage(message.Subject, message.DeadLetterReason);
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
