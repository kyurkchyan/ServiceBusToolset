using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed class DlqResubmitSession(ReactiveMessageCache<ServiceBusReceivedMessage, long> cache,
                                       IObservable<DlqCategorySnapshot> categoryStream,
                                       ResubmitTracker resubmitTracker)
    : DlqScanSession(cache, categoryStream)
{
    public ResubmitTracker ResubmitTracker { get; } = resubmitTracker;

    protected override bool MatchesFilter(
        ServiceBusReceivedMessage message,
        IReadOnlySet<DlqCategoryKey> categoryKeys,
        DateTimeOffset? beforeTime)
    {
        if (ResubmitTracker.WasResubmitted(message.MessageId))
        {
            return false;
        }

        return base.MatchesFilter(message, categoryKeys, beforeTime);
    }
}
