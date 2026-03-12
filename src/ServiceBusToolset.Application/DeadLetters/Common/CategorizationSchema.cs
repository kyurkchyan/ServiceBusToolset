namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class CategorizationSchema
{
    public static readonly CategorizationSchema Default = new([
        new CategoryPropertyRef(PropertySource.System, "Subject"),
        new CategoryPropertyRef(PropertySource.System, "DeadLetterReason")
    ]);

    public IReadOnlyList<CategoryPropertyRef> Properties { get; }
    public int DimensionCount => Properties.Count;
    public bool UsesBodyProperties { get; }

    /// <summary>
    /// Initializes a CategorizationSchema with the specified property references.
    /// </summary>
    /// <param name="properties">The property references that define the schema's categorization dimensions; must contain at least one entry.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="properties"/> is empty.</exception>
    /// <remarks>Sets the <see cref="Properties"/> collection and determines <see cref="UsesBodyProperties"/> based on whether any reference targets the message body.</remarks>
    public CategorizationSchema(IReadOnlyList<CategoryPropertyRef> properties)
    {
        if (properties.Count == 0)
        {
            throw new ArgumentException("At least one property reference is required.", nameof(properties));
        }

        if (properties.Any(p => p is null))
        {
            throw new ArgumentException("Property references cannot contain null elements.", nameof(properties));
        }

        Properties = [..properties];
        UsesBodyProperties = properties.Any(p => p.Source == PropertySource.Body);
    }

    /// <summary>
    /// Parses an enumerable of property reference strings into a CategorizationSchema.
    /// </summary>
    /// <param name="references">An enumerable of property reference strings to parse; if null or empty, the <see cref="Default"/> schema is returned.</param>
    /// <returns>A CategorizationSchema constructed from the parsed references, or <see cref="Default"/> when <paramref name="references"/> is null or contains no elements.</returns>
    public static CategorizationSchema Parse(IEnumerable<string>? references)
    {
        if (references == null)
        {
            return Default;
        }

        var list = references.ToList();
        if (list.Count == 0)
        {
            return Default;
        }

        var props = list.Select(CategoryPropertyRef.Parse).ToList();
        return new CategorizationSchema(props);
    }
}
