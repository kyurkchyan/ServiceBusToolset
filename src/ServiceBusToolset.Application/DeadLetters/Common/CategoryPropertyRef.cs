namespace ServiceBusToolset.Application.DeadLetters.Common;

public enum PropertySource { System, Body }

public sealed record CategoryPropertyRef(PropertySource Source, string PropertyPath)
{
    public string DisplayName => Source == PropertySource.System ? $"#{PropertyPath}" : $"${PropertyPath}";

    public static CategoryPropertyRef Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException("Property reference cannot be empty.", nameof(reference));
        }

        var trimmed = reference.Trim();

        if (trimmed.Length < 2)
        {
            throw new ArgumentException($"Invalid property reference '{trimmed}'. Must start with '#' (system) or '$' (body) followed by a property name.", nameof(reference));
        }

        var prefix = trimmed[0];
        var path = trimmed[1..];

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"Invalid property reference '{trimmed}'. Property name cannot be empty.", nameof(reference));
        }

        return prefix switch
        {
            '#' => new CategoryPropertyRef(PropertySource.System, path),
            '$' => new CategoryPropertyRef(PropertySource.Body, path),
            _ => throw new ArgumentException($"Invalid property reference '{trimmed}'. Must start with '#' (system) or '$' (body).",
                                             nameof(reference))
        };
    }
}
