namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class DlqCategoryDisplay
{
    /// <summary>
    /// Generate table headers and rows describing the provided dead-letter categories according to a categorization schema.
    /// </summary>
    /// <param name="categories">The sequence of DLQ categories to include as table rows.</param>
    /// <param name="schema">Optional schema that defines which category properties appear as columns; uses <c>CategorizationSchema.Default</c> when null.</param>
    /// <returns>
    /// A tuple where:
    /// - <c>Headers</c> is an array of column names (starts with "#" and the schema's property display names, ends with "Count"),
    /// - <c>Rows</c> is an enumerable of string arrays, each representing a table row with the row index, property values (or "(none)"), and the category count.
    /// </returns>
    public static (string[] Headers, IEnumerable<string[]> Rows) GenerateTableData(
        IEnumerable<DlqCategory> categories,
        CategorizationSchema? schema = null)
    {
        var effectiveSchema = schema ?? CategorizationSchema.Default;

        var headers = new List<string> { "#" };
        headers.AddRange(effectiveSchema.Properties.Select(p => p.DisplayName));
        headers.Add("Count");

        var rows = categories.Select((cat, index) =>
        {
            var row = new List<string> { (index + 1).ToString() };
            for (var i = 0; i < effectiveSchema.DimensionCount; i++)
            {
                var value = i < cat.Values.Length ? cat.Values[i] : "(none)";
                row.Add(value.ReplaceLineEndings(" "));
            }

            row.Add(cat.Count.ToString());
            return row.ToArray();
        });

        return (headers.ToArray(), rows);
    }

    /// <summary>
    /// Render a dead-letter category summary table using the provided output actions.
    /// </summary>
    /// <param name="categories">Sequence of dead-letter categories to include in the table.</param>
    /// <param name="totalCount">Total number of messages represented by the categories.</param>
    /// <param name="writeLine">Action used to write single lines of text (e.g., headings and totals).</param>
    /// <param name="writeTable">Action used to render the table; receives the headers and the rows.</param>
    /// <param name="schema">Optional categorization schema that defines table columns; defaults to <see cref="CategorizationSchema.Default"/> when null.</param>
    public static void DisplayTable(
        IEnumerable<DlqCategory> categories,
        int totalCount,
        Action<string> writeLine,
        Action<IEnumerable<string>, IEnumerable<string[]>> writeTable,
        CategorizationSchema? schema = null)
    {
        writeLine("");
        writeLine("Dead Letter Summary:");

        var (headers, rows) = GenerateTableData(categories, schema);
        writeTable(headers, rows);
        writeLine($"Total: {totalCount} messages");
    }
}
