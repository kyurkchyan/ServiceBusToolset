namespace ServiceBusToolset.Application.DeadLetters.Common;

/// <summary>
/// Represents a user's selection of DLQ categories for processing.
/// </summary>
/// <param name="Categories">The full list of available categories</param>
/// <param name="SelectedKeys">The set of category keys that were selected</param>
/// <param name="SelectedCount">The total number of messages in selected categories</param>
/// <param name="SelectedCategoryCount">The number of categories selected</param>
public sealed record CategorySelection(IReadOnlyList<DlqCategory> Categories,
                                       HashSet<DlqCategoryKey> SelectedKeys,
                                       int SelectedCount,
                                       int SelectedCategoryCount)
{
    /// <summary>
    /// Builds a CategorySelection from categories and selected indices.
    /// </summary>
    /// <param name="categories">The full list of categories</param>
    /// <param name="selectedIndices">The 0-based indices of selected categories</param>
    /// <returns>A CategorySelection representing the user's choices</returns>
    public static CategorySelection Build(
        IReadOnlyList<DlqCategory> categories,
        IReadOnlyList<int> selectedIndices)
    {
        var selectedKeys = new HashSet<DlqCategoryKey>();
        var totalCount = 0;

        foreach (var idx in selectedIndices)
        {
            var cat = categories[idx];
            selectedKeys.Add(new DlqCategoryKey(cat.Label, cat.DeadLetterReason));
            totalCount += cat.Count;
        }

        return new CategorySelection(categories,
                                     selectedKeys,
                                     totalCount,
                                     selectedIndices.Count);
    }
}
