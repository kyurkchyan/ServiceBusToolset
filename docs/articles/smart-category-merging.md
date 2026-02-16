# Smart Category Merging for DLQ Resubmission

## The Problem

When working with Azure Service Bus dead-letter queues (DLQ) interactively, messages are grouped by their `Subject` (label) and `DeadLetterReason` into categories. This works well when error messages are static:

| # | Label                | Dead Letter Reason         | Count |
|---|----------------------|---------------------------|-------|
| 1 | OrderProcessor       | MaxDeliveryCountExceeded   | 47    |
| 2 | PaymentHandler       | TimeoutExceeded            | 23    |

But many real-world error messages contain parameterized values — GUIDs, IDs, timestamps, names, sequence numbers. Each unique value creates its own category:

| # | Label                                                                  | Dead Letter Reason         | Count |
|---|------------------------------------------------------------------------|---------------------------|-------|
| 1 | Could not create user with ID 3cefe1dd-91a0-490d-adfe-dc569472f6e9    | MaxDeliveryCountExceeded   | 1     |
| 2 | Could not create user with ID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee    | MaxDeliveryCountExceeded   | 1     |
| 3 | Could not create user with ID 11111111-2222-3333-4444-555555555555    | MaxDeliveryCountExceeded   | 1     |
| ... | ... (hundreds more) | | |

This makes interactive category selection useless — you'd have to select hundreds of individual entries that are logically the same error.

## The Solution: `--merge-similar`

The `--merge-similar` flag uses LCS-based dynamic clustering to detect parameterized values and merge similar categories:

```
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue -i --merge-similar
```

| # | Label                                    | Dead Letter Reason         | Count |
|---|------------------------------------------|---------------------------|-------|
| 1 | Could not create user with ID *          | MaxDeliveryCountExceeded   | 247   |
| 2 | OrderProcessor                           | TimeoutExceeded            | 23    |

Now selecting category 1 resubmits all 247 messages, regardless of which specific GUID appeared in each.

## Algorithm: LCS-Based Greedy Clustering

### Core Idea

Instead of hardcoded regex patterns, the algorithm **compares actual category data** to dynamically detect parameterized values. A **template** is defined by its **frame** — an ordered list of literal tokens common to all members. A `*` wildcard sits implicitly between, before, and after each frame token, matching any number of tokens (including zero).

Example: frame `[Error, for, user, in, region]` produces the template `"Error * for user * in region *"`, matching:
- `"Error 123 for user Bob in region us-east"`
- `"Error 456 for user 'Alice Smith' in region eu-west-1"`

This handles **variable-length parameters** (e.g., multi-word names) that fixed-pattern approaches cannot.

### Algorithm Steps

1. **Tokenize** each category's Label and DeadLetterReason by whitespace
2. **Sort** categories by count descending (high-frequency categories form better initial templates), then by token count descending
3. **Greedy clustering**: For each category C:
   - For each existing template T, compute `LCS(T.labelFrame, C.labelTokens)` and `LCS(T.reasonFrame, C.reasonTokens)`
   - Score each field: `score = lcsLen / max(frameLen, tokensLen)`
   - Match if **both** `labelScore >= 0.5` **and** `reasonScore >= 0.5`
   - Pick the best-scoring match above threshold
   - If matched: shrink T's frames to the new LCS, add C to T's group
   - If not matched: create a new singleton template from C
4. **Post-processing**:
   - Templates with 1 member → no merging, emit as-is
   - Safety rule: frame must have ≥ 1 token (combined label + reason) to merge
5. **Render** display templates by analyzing gap positions across all members

### Scoring

Using `max(frameLen, tokensLen)` as the denominator ensures the LCS must cover a good portion of both the frame and the candidate string. The threshold of **0.5** means at least half the tokens must be shared.

For empty token sequences (both sides empty), the score is 1.0 (perfect match).

### Frame Shrinking

When we shrink a frame (take LCS of old frame with new member), the new frame is a subsequence of the old frame. The old frame was a subsequence of all previous members. By transitivity, the new frame is still a subsequence of all members.

### Template Rendering

Given a frame and all member token sequences:

1. For each member, align the frame against the member's tokens (greedy subsequence alignment)
2. For each gap between consecutive frame tokens (and before/after), check if **any** member has extra tokens
3. Insert `*` at gaps where content exists

Example: frame `[User, is, not, valid]`, members `["User 'John Smith' is not valid", "User 'Bob' is not valid"]`:
- Gap "User" → "is": both members have extra tokens → insert `*`
- All other gaps: empty → no `*`
- **Template: `"User * is not valid"`**

### Examples

| Input categories | Result |
|---|---|
| `"User 'John Smith' is not valid"`, `"User 'Bob' is not valid"` | `"User * is not valid"` |
| `"Error 1 for user 'John Smith' in region us-east"`, `"Error 2 for user 'Bob' in region eu-west"` | `"Error * for user * in region *"` |
| `"Could not create user with ID <guid1>"`, `"...with ID <guid2>"` | `"Could not create user with ID *"` |
| `"OrderProcessor"`, `"PaymentHandler"` | Kept separate (no common tokens) |

### Performance

- LCS computation: O(m × n) per comparison via DP
- Total: O(K × N × L²) where K = templates (~20), N = categories (~1000), L = avg tokens (~10)
- Completes in microseconds for typical DLQ data

## Merge + Expand Pattern

The key architectural insight is that merging is **only a display/grouping concern**. The downstream message filtering (`SnapshotForCategories`) still uses exact-match `DlqCategoryKey` values.

The flow:

1. **Merge**: Cluster categories by LCS similarity, sum counts, and build a `MergeMap` (merged key → set of original keys)
2. **Display**: Show merged categories to the user in the interactive table
3. **Select**: User picks merged categories by index number
4. **Expand**: Convert selected merged keys back to all original keys via `ExpandKeys`
5. **Filter**: Pass expanded original keys to the existing `SnapshotForCategories` method

```
Original categories          Merged categories         User selects
┌─────────────────────┐     ┌────────────────────┐     merged #1
│ Error with ID abc   │────▸│ Error with ID *    │─────────┐
│ Error with ID def   │────▸│ (count: 3)         │         │
│ Error with ID ghi   │────▸│                    │         │
└─────────────────────┘     └────────────────────┘         │
                                                           ▼
                            ExpandKeys()              Filter with
                            ┌────────────────────┐    original keys
                            │ {abc, def, ghi}    │───▸ exact match
                            └────────────────────┘
```

## Files

- `CategoryMerger.cs` — Static utility with LCS-based `Merge` algorithm
- `CategoryMergeResult.cs` — Result record with `ExpandKeys` method
- `StreamDlqCategories.cs` — Threading the `MergeSimilar` flag through command → snapshot
- `ResubmitDlqCommandHandler.cs` — Wiring up key expansion in the interactive flow
- `ResubmitDlqCliCommand.cs` — The `--merge-similar` CLI option
