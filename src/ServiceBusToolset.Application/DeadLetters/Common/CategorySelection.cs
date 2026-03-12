namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record CategorySelection(IReadOnlyList<DlqCategory> Categories,
                                       HashSet<DlqCategoryKey> SelectedKeys,
                                       int SelectedCount,
                                       int SelectedCategoryCount)
{
    /// <summary>
    /// Builds a CategorySelection for the provided categories using the specified selected indices.
    /// </summary>
    /// <param name="categories">The list of DLQ categories to include in the selection.</param>
    /// <param name="selectedIndices">Indices into <paramref name="categories"/> that should be marked selected.</param>
    /// <returns>
    /// A CategorySelection whose <see cref="CategorySelection.Categories"/> is <paramref name="categories"/>,
    /// whose <see cref="CategorySelection.SelectedKeys"/> contains the keys of the selected categories,
    /// whose <see cref="CategorySelection.SelectedCount"/> is the sum of counts for selected categories,
    /// and whose <see cref="CategorySelection.SelectedCategoryCount"/> equals the number of selected indices.
    /// </returns>
    public static CategorySelection Build(
        IReadOnlyList<DlqCategory> categories,
        IReadOnlyList<int> selectedIndices)
    {
        var selectedKeys = new HashSet<DlqCategoryKey>();
        var totalCount = 0;

        foreach (var idx in selectedIndices)
        {
            var cat = categories[idx];
            selectedKeys.Add(cat.ToKey());
            totalCount += cat.Count;
        }

        return new CategorySelection(categories,
                                     selectedKeys,
                                     totalCount,
                                     selectedIndices.Count);
    }
}
