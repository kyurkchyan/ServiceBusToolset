using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record DlqCategorySnapshot(IReadOnlyList<DlqCategory> Categories,
                                         int TotalMessageCount,
                                         bool IsComplete);

public sealed class DlqResubmitSession(ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
                                       IObservable<DlqCategorySnapshot> categoryStream,
                                       ResubmitTracker resubmitTracker)
    : IDisposable
{
    private readonly CancellationTokenSource _scanCts = new();

    public ReactiveMessageCache<ServiceBusReceivedMessage, long> Cache { get; } = cache;
    public IObservable<DlqCategorySnapshot> CategoryStream { get; } = categoryStream;
    public ResubmitTracker ResubmitTracker { get; } = resubmitTracker;
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
               .Where(m =>
               {
                   if (ResubmitTracker.WasResubmitted(m.MessageId))
                   {
                       return false;
                   }

                   var key = DlqCategoryKey.FromMessage(m.Subject, m.DeadLetterReason);
                   if (!categoryKeys.Contains(key))
                   {
                       return false;
                   }

                   if (beforeTime.HasValue && m.EnqueuedTime >= beforeTime.Value)
                   {
                       return false;
                   }

                   return true;
               })
               .ToList();
    }

    public void Dispose()
    {
        _scanCts.Cancel();
        _scanCts.Dispose();
        Cache.Dispose();
    }
}
