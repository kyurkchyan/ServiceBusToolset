namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record CategoryMergeResult(IReadOnlyList<DlqCategory> MergedCategories,
                                         IReadOnlyDictionary<DlqCategoryKey, IReadOnlySet<DlqCategoryKey>> MergeMap)
{
    /// <summary>
    /// Expands merged category keys back to all original keys they represent,
    /// enabling exact-match filtering downstream.
    /// </summary>
    public HashSet<DlqCategoryKey> ExpandKeys(IReadOnlySet<DlqCategoryKey> mergedKeys)
    {
        var expanded = new HashSet<DlqCategoryKey>();

        foreach (var mergedKey in mergedKeys)
        {
            if (MergeMap.TryGetValue(mergedKey, out var originals))
            {
                foreach (var original in originals)
                {
                    expanded.Add(original);
                }
            }
            else
            {
                expanded.Add(mergedKey);
            }
        }

        return expanded;
    }
}
