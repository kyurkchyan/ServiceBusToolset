namespace ServiceBusToolset.Application.DeadLetters.Common;

public enum PropertySource { System, Body }

public sealed record CategoryPropertyRef(PropertySource Source, string PropertyPath)
{
    public string DisplayName => Source == PropertySource.System ? $"#{PropertyPath}" : $"${PropertyPath}";

    /// <summary>
    /// Parses a textual property reference into a <see cref="CategoryPropertyRef"/>.
    /// </summary>
    /// <param name="reference">A string starting with '#' for system properties or '$' for body properties, followed by the property path (e.g. "#EnqueuedTimeUtc" or "$.user.id").</param>
    /// <returns>A <see cref="CategoryPropertyRef"/> with <see cref="CategoryPropertyRef.Source"/> set from the leading prefix and <see cref="CategoryPropertyRef.PropertyPath"/> set to the remainder of the string.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reference"/> is null, empty, shorter than two characters, has an empty property path, or does not start with '#' or '$'.</exception>
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
