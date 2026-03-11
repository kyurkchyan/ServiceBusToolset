namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class DlqCategoryDisplay
{
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
