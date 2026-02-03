namespace ServiceBusToolset.Application.Common.ServiceBus.Helpers;

public sealed class StatisticsListComparer<T> : IEqualityComparer<IReadOnlyList<T>>
    where T : IHasComparableCounts<T>
{
    public bool Equals(IReadOnlyList<T>? x, IReadOnlyList<T>? y)
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

    public int GetHashCode(IReadOnlyList<T> obj)
    {
        var hash = new HashCode();
        foreach (var stat in obj)
        {
            stat.AddToHashCode(ref hash);
        }

        return hash.ToHashCode();
    }
}

public interface IHasComparableCounts<in T>
{
    bool HasSameCountsAs(T other);
    void AddToHashCode(ref HashCode hash);
}
