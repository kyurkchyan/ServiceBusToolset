namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record DlqCategorySnapshot(IReadOnlyList<DlqCategory> Categories,
                                         int TotalMessageCount,
                                         bool IsComplete,
                                         CategoryMergeResult? MergeResult = null);
