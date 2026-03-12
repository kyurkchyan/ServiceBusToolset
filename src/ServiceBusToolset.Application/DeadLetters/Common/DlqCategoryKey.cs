using System.Collections.Immutable;
using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class DlqCategoryKey : IEquatable<DlqCategoryKey>
{
    public ImmutableArray<string> Values { get; }

    /// <summary>
    /// Initializes a DlqCategoryKey with the provided sequence of key parts.
    /// </summary>
    /// <param name="values">The ordered sequence of string segments that make up the composite DLQ category key (e.g., label, dead-letter reason, followed by any additional segments).</param>
    public DlqCategoryKey(ImmutableArray<string> values)
    {
        Values = values;
    }

    /// <summary>
/// Initializes a new DlqCategoryKey from an ordered sequence of key parts.
/// </summary>
/// <param name="values">Ordered key parts that make up the category key; each element is stored as a value in the key.</param>
public DlqCategoryKey(params string[] values) : this(values.ToImmutableArray()) { }

    public string Label => Values.Length > 0 ? Values[0] : "(none)";
    public string DeadLetterReason => Values.Length > 1 ? Values[1] : "(none)";

    /// <summary>
        /// Create a DlqCategoryKey from a message subject and dead-letter reason.
        /// </summary>
        /// <param name="subject">The message subject; if null, the literal "(none)" is used.</param>
        /// <param name="deadLetterReason">The dead-letter reason; if null, the literal "(none)" is used.</param>
        /// <returns>A DlqCategoryKey whose Values contain the subject as the first element and the dead-letter reason as the second.</returns>
        public static DlqCategoryKey FromMessage(string? subject, string? deadLetterReason)
        => new(subject ?? "(none)", deadLetterReason ?? "(none)");

    /// <summary>
    /// Creates a DlqCategoryKey by resolving each property defined in the categorization schema against the provided Service Bus message.
    /// </summary>
    /// <param name="message">The Service Bus message to extract property values from.</param>
    /// <param name="schema">The categorization schema whose ordered Properties define the key dimensions.</param>
    /// <param name="resolver">The resolver used to obtain a string value for each schema property from the message.</param>
    /// <returns>A DlqCategoryKey whose Values are the resolved property values in the same order as schema.Properties.</returns>
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

    /// <summary>
    /// Determines whether this instance represents the same composite key as another DlqCategoryKey.
    /// </summary>
    /// <param name="other">The DlqCategoryKey to compare against.</param>
    /// <returns>`true` if <paramref name="other"/> is non-null, has the same number of values, and every corresponding value is equal using ordinal string comparison; `false` otherwise.</returns>
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

    /// <summary>
/// Determines whether the specified object is equal to this DlqCategoryKey.
/// </summary>
/// <param name="obj">The object to compare with this instance.</param>
/// <returns>`true` if <paramref name="obj"/> is a <see cref="DlqCategoryKey"/> whose Values sequence is equal to this instance's Values using ordinal string comparison; `false` otherwise.</returns>
public override bool Equals(object? obj) => Equals(obj as DlqCategoryKey);

    /// <summary>
    /// Computes a hash code for this key based on the sequence of Values using ordinal string comparison.
    /// </summary>
    /// <returns>An integer hash code representing the sequence of Values.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in Values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
        /// Concatenates the key parts into a single string separated by " | ".
        /// </summary>
        /// <returns>The sequence of Values joined with " | ".</returns>
        public override string ToString()
        => string.Join(" | ", Values);
}
