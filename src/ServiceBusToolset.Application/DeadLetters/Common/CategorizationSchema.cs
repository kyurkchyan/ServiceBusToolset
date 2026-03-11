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

    public CategorizationSchema(IReadOnlyList<CategoryPropertyRef> properties)
    {
        if (properties.Count == 0)
        {
            throw new ArgumentException("At least one property reference is required.", nameof(properties));
        }

        Properties = [..properties];
        UsesBodyProperties = properties.Any(p => p.Source == PropertySource.Body);
    }

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
