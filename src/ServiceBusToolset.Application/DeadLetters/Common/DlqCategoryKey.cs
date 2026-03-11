using System.Collections.Immutable;
using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqCategoryKey : IEquatable<DlqCategoryKey>
{
    public ImmutableArray<string> Values { get; }

    public DlqCategoryKey(ImmutableArray<string> values)
    {
        Values = values;
    }

    public DlqCategoryKey(params string[] values) : this(values.ToImmutableArray()) { }

    public string Label => Values.Length > 0 ? Values[0] : "(none)";
    public string DeadLetterReason => Values.Length > 1 ? Values[1] : "(none)";

    public static DlqCategoryKey FromMessage(string? subject, string? deadLetterReason)
        => new(subject ?? "(none)", deadLetterReason ?? "(none)");

    public static DlqCategoryKey FromMessage(
        ServiceBusReceivedMessage message,
        CategorizationSchema schema,
        CategoryPropertyResolver resolver)
    {
        var values = ImmutableArray.CreateBuilder<string>(schema.DimensionCount);
        foreach (var prop in schema.Properties)
        {
            values.Add(resolver.ResolveProperty(message, prop));
        }

        return new DlqCategoryKey(values.MoveToImmutable());
    }

    public bool Equals(DlqCategoryKey? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Values.Length != other.Values.Length)
        {
            return false;
        }

        for (var i = 0; i < Values.Length; i++)
        {
            if (!string.Equals(Values[i], other.Values[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DlqCategoryKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
        => string.Join(" | ", Values);
}
