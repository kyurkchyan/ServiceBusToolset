namespace ServiceBusToolset.Application.DeadLetters.Common;

/// <summary>
/// Static utility for parsing user selection input for DLQ category selection.
/// </summary>
public static class CategorySelectionParser
{
    /// <summary>
    /// Parses user input for category selection.
    /// Supports: comma-separated numbers (1,2,3), ranges (1-5), "all"/"a", "q"/"quit"
    /// </summary>
    /// <param name="input">The user input string</param>
    /// <param name="maxIndex">The maximum valid index (count of categories)</param>
    /// <returns>List of 0-based indices, or null if user cancelled</returns>
    public static List<int>? Parse(string? input, int maxIndex)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().ToLowerInvariant();

        switch (trimmed)
        {
            case "q":
            case "quit":
                return null;
            case "all":
            case "a":
                return Enumerable.Range(0, maxIndex).ToList();
        }

        var indices = new List<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', 2);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        var idx = i - 1;
                        if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                        {
                            indices.Add(idx);
                        }
                    }
                }
            }
            else if (int.TryParse(part, out var num))
            {
                var idx = num - 1;
                if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                {
                    indices.Add(idx);
                }
            }
        }

        return indices;
    }
}
