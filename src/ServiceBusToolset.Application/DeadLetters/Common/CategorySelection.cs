namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record CategorySelection(IReadOnlyList<DlqCategory> Categories,
                                       HashSet<DlqCategoryKey> SelectedKeys,
                                       int SelectedCount,
                                       int SelectedCategoryCount)
{
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
