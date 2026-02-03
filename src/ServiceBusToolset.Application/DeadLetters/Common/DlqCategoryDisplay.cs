namespace ServiceBusToolset.Application.DeadLetters.Common;

/// <summary>
/// Static helper for displaying DLQ category tables.
/// </summary>
public static class DlqCategoryDisplay
{
    /// <summary>
    /// Generates table data for displaying DLQ categories.
    /// </summary>
    /// <param name="categories">The categories to display</param>
    /// <returns>Tuple of headers and rows for the table</returns>
    public static (string[] Headers, IEnumerable<string[]> Rows) GenerateTableData(
        IEnumerable<DlqCategory> categories)
    {
        var headers = new[]
        {
            "#",
            "Label",
            "DeadLetterReason",
            "Count"
        };

        var rows = categories.Select((cat, index) => new[]
        {
            (index + 1).ToString(),
            cat.Label.ReplaceLineEndings(" "),
            cat.DeadLetterReason.ReplaceLineEndings(" "),
            cat.Count.ToString()
        });

        return (headers, rows);
    }

    /// <summary>
    /// Displays a category table using the provided output actions.
    /// </summary>
    /// <param name="categories">The categories to display</param>
    /// <param name="totalCount">The total message count</param>
    /// <param name="writeLine">Action to write a line of text</param>
    /// <param name="writeTable">Action to write a table (headers, rows)</param>
    public static void DisplayTable(
        IEnumerable<DlqCategory> categories,
        int totalCount,
        Action<string> writeLine,
        Action<IEnumerable<string>, IEnumerable<string[]>> writeTable)
    {
        writeLine("");
        writeLine("Dead Letter Summary:");

        var (headers, rows) = GenerateTableData(categories);
        writeTable(headers, rows);
        writeLine($"Total: {totalCount} messages");
    }
}
