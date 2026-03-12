using System.Collections.Immutable;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqCategory : IEquatable<DlqCategory>
{
    public ImmutableArray<string> Values { get; }
    public int Count { get; }

    public string Label => Values.Length > 0 ? Values[0] : "(none)";
    public string DeadLetterReason => Values.Length > 1 ? Values[1] : "(none)";

    /// <summary>
    /// Initializes a DlqCategory with the specified category values and associated count.
    /// </summary>
    /// <param name="values">Category-related string values; element 0 is the label and element 1 (if present) is the dead-letter reason.</param>
    /// <param name="count">The count associated with the category.</param>
    public DlqCategory(ImmutableArray<string> values, int count)
    {
        Values = values;
        Count = count;
    }

    /// <summary>
    /// Creates a DlqCategory using a label and a dead-letter reason with an associated count.
    /// </summary>
    /// <param name="label">The primary category label (stored as the first value).</param>
    /// <param name="deadLetterReason">The dead-letter reason (stored as the second value).</param>
    /// <param name="count">The number of occurrences for this category.</param>
    public DlqCategory(string label, string deadLetterReason, int count)
        : this([label, deadLetterReason], count)
    {
    }

    /// <summary>
    /// Convert this category's stored values into a DlqCategoryKey.
    /// </summary>
    /// <returns>A DlqCategoryKey constructed from the current Values.</returns>
    public DlqCategoryKey ToKey() => new(Values);

    /// <summary>
    /// Creates a DlqCategory from a DlqCategoryKey and an associated count.
    /// </summary>
    /// <param name="key">The DlqCategoryKey whose Values will be used for the category.</param>
    /// <param name="count">The count to assign to the created category.</param>
    /// <returns>A DlqCategory whose Values are taken from <c>key.Values</c> and whose Count equals <paramref name="count"/>.</returns>
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
