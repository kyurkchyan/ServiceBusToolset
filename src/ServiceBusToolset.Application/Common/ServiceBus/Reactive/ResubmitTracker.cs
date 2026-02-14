namespace ServiceBusToolset.Application.Common.ServiceBus.Reactive;

public sealed class ResubmitTracker
{
    private readonly HashSet<string> _resubmittedIds = [];
    private readonly object _lock = new();

    public void MarkResubmitted(string messageId)
    {
        lock (_lock)
        {
            _resubmittedIds.Add(messageId);
        }
    }

    public void MarkResubmitted(IEnumerable<string> messageIds)
    {
        lock (_lock)
        {
            foreach (var id in messageIds)
            {
                _resubmittedIds.Add(id);
            }
        }
    }

    public bool WasResubmitted(string messageId)
    {
        lock (_lock)
        {
            return _resubmittedIds.Contains(messageId);
        }
    }
}
