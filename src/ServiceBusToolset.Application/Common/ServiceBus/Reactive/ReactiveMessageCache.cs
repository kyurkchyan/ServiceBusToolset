using DynamicData;

namespace ServiceBusToolset.Application.Common.ServiceBus.Reactive;

public sealed class ReactiveMessageCache<TMessage, TKey>(Func<TMessage, TKey> keySelector) : IDisposable
    where TMessage : notnull
    where TKey : notnull
{
    private readonly SourceCache<TMessage, TKey> _cache = new(keySelector);
    private bool _disposed;

    public bool IsComplete { get; private set; }

    public int Count => _cache.Count;

    public IObservable<int> CountChanged => _cache.CountChanged;

    public void AddOrUpdate(IEnumerable<TMessage> items)
    {
        _cache.Edit(updater =>
        {
            foreach (var item in items)
            {
                updater.AddOrUpdate(item);
            }
        });
    }

    public void MarkComplete()
    {
        IsComplete = true;
    }

    public IObservable<IChangeSet<TMessage, TKey>> Connect() => _cache.Connect();

    public IReadOnlyList<TMessage> Snapshot() => _cache.Items.ToList();

    public TMessage? Lookup(TKey key)
    {
        var optional = _cache.Lookup(key);
        return optional.HasValue ? optional.Value : default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();
    }
}
