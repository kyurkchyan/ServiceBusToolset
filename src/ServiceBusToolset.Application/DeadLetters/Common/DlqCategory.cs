using System.Collections.Immutable;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqCategory : IEquatable<DlqCategory>
{
    public ImmutableArray<string> Values { get; }
    public int Count { get; }

    public string Label => Values.Length > 0 ? Values[0] : "(none)";
    public string DeadLetterReason => Values.Length > 1 ? Values[1] : "(none)";

    public DlqCategory(ImmutableArray<string> values, int count)
    {
        Values = values;
        Count = count;
    }

    public DlqCategory(string label, string deadLetterReason, int count)
        : this([label, deadLetterReason], count)
    {
    }

    public DlqCategoryKey ToKey() => new(Values);

    public static DlqCategory FromKey(DlqCategoryKey key, int count) => new(key.Values, count);

    public bool Equals(DlqCategory? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Count == other.Count && Values.SequenceEqual(other.Values);
    }

    public override bool Equals(object? obj) => Equals(obj as DlqCategory);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        foreach (var value in Values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
