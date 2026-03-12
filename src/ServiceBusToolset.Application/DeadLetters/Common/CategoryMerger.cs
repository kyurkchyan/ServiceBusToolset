using System.Collections.Immutable;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class CategoryMerger
{
    private const string Wildcard = "*";
    private const double MergeThreshold = 0.5;

    /// <summary>
    /// Groups and consolidates similar dead-letter categories into merged templates across configured dimensions.
    /// </summary>
    /// <param name="categories">The list of categories to merge; each entry's counts contribute to merged totals.</param>
    /// <param name="schema">Optional categorization schema that determines the number of dimensions to consider; defaults to the standard schema if null.</param>
    /// <returns>
    /// A CategoryMergeResult containing:
    /// - Merged categories sorted by descending Count, and
    /// - A map from each merged category key to the set of original category keys that were combined into it.
    /// If the input list is empty, both collections will be empty.
    /// </returns>
    public static CategoryMergeResult Merge(IReadOnlyList<DlqCategory> categories,
                                            CategorizationSchema? schema = null)
    {
        if (categories.Count == 0)
        {
            return new CategoryMergeResult([], new Dictionary<DlqCategoryKey, IReadOnlySet<DlqCategoryKey>>());
        }

        var dimensionCount = (schema ?? CategorizationSchema.Default).DimensionCount;

        var tokenized = categories
                        .Select(c => new TokenizedCategory(TokenizeDimensions(c, dimensionCount),
                                                           c))
                        .OrderByDescending(t => t.Original.Count)
                        .ThenByDescending(t => t.DimensionTokens.Sum(d => d.Length))
                        .ToList();

        var templates = new List<TemplateGroup>();

        foreach (var cat in tokenized)
        {
            var bestMatch = -1;
            var bestScore = 0.0;

            for (var i = 0; i < templates.Count; i++)
            {
                var t = templates[i];

                if (t.DimensionFrames.Length != cat.DimensionTokens.Length)
                {
                    continue;
                }

                var allAboveThreshold = true;
                var combinedScore = 0.0;

                for (var d = 0; d < dimensionCount; d++)
                {
                    var lcs = ComputeLcs(t.DimensionFrames[d], cat.DimensionTokens[d]);
                    var score = Score(lcs.Length, t.DimensionFrames[d].Length, cat.DimensionTokens[d].Length);

                    if (score < MergeThreshold)
                    {
                        allAboveThreshold = false;
                        break;
                    }

                    combinedScore += score;
                }

                if (allAboveThreshold && combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestMatch = i;
                }
            }

            if (bestMatch >= 0)
            {
                var t = templates[bestMatch];
                for (var d = 0; d < dimensionCount; d++)
                {
                    t.DimensionFrames[d] = ComputeLcs(t.DimensionFrames[d], cat.DimensionTokens[d]);
                }

                t.Members.Add(cat);
            }
            else
            {
                templates.Add(new TemplateGroup
                {
                    DimensionFrames = cat.DimensionTokens.Select(d => d.ToArray()).ToArray(),
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
                var key = single.ToKey();
                mergedCategories.Add(single);
                mergeMap[key] = new HashSet<DlqCategoryKey> { key };
                continue;
            }

            var totalFrameTokens = t.DimensionFrames.Sum(f => f.Length);
            if (totalFrameTokens < 1)
            {
                foreach (var member in t.Members)
                {
                    var orig = member.Original;
                    var key = orig.ToKey();
                    mergedCategories.Add(orig);
                    mergeMap[key] = new HashSet<DlqCategoryKey> { key };
                }

                continue;
            }

            var mergedValues = ImmutableArray.CreateBuilder<string>(dimensionCount);
            for (var d = 0; d < dimensionCount; d++)
            {
                var template = RenderTemplate(t.DimensionFrames[d],
                                              t.Members.Select(m => m.DimensionTokens[d]).ToList());
                mergedValues.Add(template);
            }

            var totalCount = t.Members.Sum(m => m.Original.Count);
            var mergedKey = new DlqCategoryKey(mergedValues.MoveToImmutable());
            mergedCategories.Add(DlqCategory.FromKey(mergedKey, totalCount));

            var originals = new HashSet<DlqCategoryKey>();
            foreach (var member in t.Members)
            {
                originals.Add(member.Original.ToKey());
            }

            mergeMap[mergedKey] = originals;
        }

        mergedCategories.Sort((a, b) => b.Count.CompareTo(a.Count));

        return new CategoryMergeResult(mergedCategories, mergeMap);
    }

    /// <summary>
    /// Split each dimension value of a category into space-separated tokens and return them as a per-dimension token matrix.
    /// </summary>
    /// <param name="category">The category whose dimension values will be tokenized.</param>
    /// <param name="dimensionCount">The number of dimensions to produce tokens for; if a dimension index is out of range for the category, the placeholder "(none)" is tokenized for that dimension.</param>
    /// <returns>An array of length <paramref name="dimensionCount"/> where each element is the string[] of tokens for the corresponding dimension.</returns>
    private static string[][] TokenizeDimensions(DlqCategory category, int dimensionCount)
    {
        var result = new string[dimensionCount][];
        for (var i = 0; i < dimensionCount; i++)
        {
            var value = i < category.Values.Length ? category.Values[i] : "(none)";
            result[i] = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        return result;
    }

    /// <summary>
    /// Computes the longest common subsequence of two token sequences and returns it in original order.
    /// </summary>
    /// <param name="a">First sequence of tokens to compare.</param>
    /// <param name="b">Second sequence of tokens to compare.</param>
    /// <returns>An array containing the tokens of the longest common subsequence in the order they appear in the inputs; empty if no common subsequence exists.</returns>
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

    /// <summary>
    /// Computes a similarity score between a template frame and a token set based on the longest common subsequence length.
    /// </summary>
    /// <param name="lcsLen">Length of the longest common subsequence between the frame and the tokens.</param>
    /// <param name="frameLen">Number of tokens in the template frame.</param>
    /// <param name="tokensLen">Number of tokens in the compared token set.</param>
    /// <returns>`1.0` if both `frameLen` and `tokensLen` are zero; otherwise the LCS length divided by the larger of `frameLen` and `tokensLen`.</returns>
    private static double Score(int lcsLen, int frameLen, int tokensLen)
    {
        var maxLen = Math.Max(frameLen, tokensLen);
        if (maxLen == 0)
        {
            return 1.0;
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

    /// <summary>
    /// Maps each token in a frame to its corresponding index in a target token sequence, preserving order.
    /// </summary>
    /// <param name="frame">Sequence of tokens that must be found in order.</param>
    /// <param name="tokens">Target token sequence to search within.</param>
    /// <returns>
    /// An int array where element i is the index in <paramref name="tokens"/> of <paramref name="frame"/>[i]; returns <c>null</c> if any frame token cannot be found in sequence order.
    /// </returns>
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

    private sealed record TokenizedCategory(string[][] DimensionTokens, DlqCategory Original);

    private sealed class TemplateGroup
    {
        public string[][] DimensionFrames { get; set; } = [];
        public List<TokenizedCategory> Members { get; set; } = [];
    }
}
