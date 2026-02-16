namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class CategoryMerger
{
    private const string Wildcard = "*";
    private const double MergeThreshold = 0.5;

    public static CategoryMergeResult Merge(IReadOnlyList<DlqCategory> categories)
    {
        if (categories.Count == 0)
        {
            return new CategoryMergeResult([], new Dictionary<DlqCategoryKey, IReadOnlySet<DlqCategoryKey>>());
        }

        var tokenized = categories
                        .Select(c => new TokenizedCategory(c.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                                                           c.DeadLetterReason.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                                                           c))
                        .OrderByDescending(t => t.Original.Count)
                        .ThenByDescending(t => t.LabelTokens.Length)
                        .ToList();

        var templates = new List<TemplateGroup>();

        foreach (var cat in tokenized)
        {
            var bestMatch = -1;
            var bestScore = 0.0;

            for (var i = 0; i < templates.Count; i++)
            {
                var t = templates[i];
                var labelLcs = ComputeLcs(t.LabelFrame, cat.LabelTokens);
                var reasonLcs = ComputeLcs(t.ReasonFrame, cat.ReasonTokens);

                var labelScore = Score(labelLcs.Length, t.LabelFrame.Length, cat.LabelTokens.Length);
                var reasonScore = Score(reasonLcs.Length, t.ReasonFrame.Length, cat.ReasonTokens.Length);

                if (labelScore >= MergeThreshold && reasonScore >= MergeThreshold)
                {
                    var combinedScore = labelScore + reasonScore;
                    if (combinedScore > bestScore)
                    {
                        bestScore = combinedScore;
                        bestMatch = i;
                    }
                }
            }

            if (bestMatch >= 0)
            {
                var t = templates[bestMatch];
                t.LabelFrame = ComputeLcs(t.LabelFrame, cat.LabelTokens);
                t.ReasonFrame = ComputeLcs(t.ReasonFrame, cat.ReasonTokens);
                t.Members.Add(cat);
            }
            else
            {
                templates.Add(new TemplateGroup
                {
                    LabelFrame = cat.LabelTokens,
                    ReasonFrame = cat.ReasonTokens,
                    Members = [cat]
                });
            }
        }

        var mergedCategories = new List<DlqCategory>();
        var mergeMap = new Dictionary<DlqCategoryKey, IReadOnlySet<DlqCategoryKey>>();

        foreach (var t in templates)
        {
            if (t.Members.Count == 1)
            {
                var single = t.Members[0].Original;
                var key = new DlqCategoryKey(single.Label, single.DeadLetterReason);
                mergedCategories.Add(single);
                mergeMap[key] = new HashSet<DlqCategoryKey> { key };
                continue;
            }

            var totalFrameTokens = t.LabelFrame.Length + t.ReasonFrame.Length;
            if (totalFrameTokens < 1)
            {
                // Safety: frame too small, dissolve into individual categories
                foreach (var member in t.Members)
                {
                    var orig = member.Original;
                    var key = new DlqCategoryKey(orig.Label, orig.DeadLetterReason);
                    mergedCategories.Add(orig);
                    mergeMap[key] = new HashSet<DlqCategoryKey> { key };
                }

                continue;
            }

            var labelTemplate = RenderTemplate(t.LabelFrame, t.Members.Select(m => m.LabelTokens).ToList());
            var reasonTemplate = RenderTemplate(t.ReasonFrame, t.Members.Select(m => m.ReasonTokens).ToList());

            var totalCount = t.Members.Sum(m => m.Original.Count);
            var mergedKey = new DlqCategoryKey(labelTemplate, reasonTemplate);
            mergedCategories.Add(new DlqCategory(labelTemplate, reasonTemplate, totalCount));

            var originals = new HashSet<DlqCategoryKey>();
            foreach (var member in t.Members)
            {
                originals.Add(new DlqCategoryKey(member.Original.Label, member.Original.DeadLetterReason));
            }

            mergeMap[mergedKey] = originals;
        }

        mergedCategories.Sort((a, b) => b.Count.CompareTo(a.Count));

        return new CategoryMergeResult(mergedCategories, mergeMap);
    }

    private static string[] ComputeLcs(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (var i = 1; i <= m; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                if (string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal))
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }

        // Backtrack to find the LCS
        var lcs = new List<string>();
        int x = m, y = n;
        while (x > 0 && y > 0)
        {
            if (string.Equals(a[x - 1], b[y - 1], StringComparison.Ordinal))
            {
                lcs.Add(a[x - 1]);
                x--;
                y--;
            }
            else if (dp[x - 1, y] >= dp[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        lcs.Reverse();
        return lcs.ToArray();
    }

    private static double Score(int lcsLen, int frameLen, int tokensLen)
    {
        var maxLen = Math.Max(frameLen, tokensLen);
        if (maxLen == 0)
        {
            return 1.0; // Both empty — perfect match
        }

        return (double)lcsLen / maxLen;
    }

    private static string RenderTemplate(string[] frame, List<string[]> memberTokenSets)
    {
        if (frame.Length == 0)
        {
            return memberTokenSets.Any(m => m.Length > 0) ? Wildcard : string.Empty;
        }

        var gapHasContent = new bool[frame.Length + 1];

        foreach (var memberTokens in memberTokenSets)
        {
            var positions = AlignFrameToTokens(frame, memberTokens);
            if (positions == null)
            {
                continue;
            }

            if (positions[0] > 0)
            {
                gapHasContent[0] = true;
            }

            for (var i = 1; i < frame.Length; i++)
            {
                if (positions[i] - positions[i - 1] > 1)
                {
                    gapHasContent[i] = true;
                }
            }

            if (positions[^1] < memberTokens.Length - 1)
            {
                gapHasContent[frame.Length] = true;
            }
        }

        var result = new List<string>();

        if (gapHasContent[0])
        {
            result.Add(Wildcard);
        }

        for (var i = 0; i < frame.Length; i++)
        {
            result.Add(frame[i]);
            if (gapHasContent[i + 1])
            {
                result.Add(Wildcard);
            }
        }

        return string.Join(' ', result);
    }

    private static int[]? AlignFrameToTokens(string[] frame, string[] tokens)
    {
        var positions = new int[frame.Length];
        var tokenIdx = 0;

        for (var i = 0; i < frame.Length; i++)
        {
            var found = false;
            while (tokenIdx < tokens.Length)
            {
                if (string.Equals(frame[i], tokens[tokenIdx], StringComparison.Ordinal))
                {
                    positions[i] = tokenIdx;
                    tokenIdx++;
                    found = true;
                    break;
                }

                tokenIdx++;
            }

            if (!found)
            {
                return null;
            }
        }

        return positions;
    }

    private sealed record TokenizedCategory(string[] LabelTokens, string[] ReasonTokens, DlqCategory Original);

    private sealed class TemplateGroup
    {
        public string[] LabelFrame { get; set; } = [];
        public string[] ReasonFrame { get; set; } = [];
        public List<TokenizedCategory> Members { get; set; } = [];
    }
}
