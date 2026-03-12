# Flexible Categorization Engine

## The Problem

All four DLQ commands (`dump-dlq`, `purge-dlq`, `resubmit-dlq`, `diagnose-dlq`) group messages into categories for
interactive selection. Previously, categorization was hardcoded to two properties: `Subject` (Label) and
`DeadLetterReason`. This was baked into every layer — from `DlqCategoryKey(string Label, string DeadLetterReason)`
through the scan session, display table, merge algorithm, and message filtering.

This worked for simple cases:

```text
╭───┬──────────────────┬──────────────────────────┬───────╮
│ # │ Label            │ DeadLetterReason         │ Count │
├───┼──────────────────┼──────────────────────────┼───────┤
│ 1 │ OrderProcessor   │ MaxDeliveryCountExceeded │   847 │
│ 2 │ PaymentHandler   │ TTLExpiredException      │   412 │
╰───┴──────────────────┴──────────────────────────┴───────╯
```

But real-world Service Bus usage often demands different grouping strategies:

- **Group by error code in the message body** — when `Subject` is generic but the JSON payload contains a structured
  `errorCode` field.
- **Group by deployment context** — when messages carry `environment` or `region` metadata in their body that matters
  more than the dead-letter reason.
- **Group by custom application headers** — when teams set application-specific properties like `TenantId` or
  `ProcessorVersion` on messages.
- **Single-dimension grouping** — sometimes only `DeadLetterReason` matters, and `Subject` just adds noise.

With hardcoded categorization, none of this was possible without modifying source code.

## The Solution: `--categorize-by`

The `--categorize-by` option lets users define which properties form the category dimensions, using a prefix syntax:

| Prefix          | Source                                         | Example                                         |
|-----------------|------------------------------------------------|-------------------------------------------------|
| `#PropertyName` | System property on `ServiceBusReceivedMessage` | `#Subject`, `#DeadLetterReason`, `#ContentType` |
| `$PropertyName` | JSON body property (deserialized)              | `$errorCode`, `$tier`                           |
| `$Nested.Path`  | Nested JSON body property via dot notation     | `$error.severity`, `$context.region`            |

```bash
# Default (backward-compatible, same as before)
dump-dlq -n ns.servicebus.windows.net -q myqueue -i

# Single dimension: just the dead-letter reason
dump-dlq -n ns.servicebus.windows.net -q myqueue -i --categorize-by "#DeadLetterReason"

# Mixed: system property + JSON body property
dump-dlq -n ns.servicebus.windows.net -q myqueue -i --categorize-by "#DeadLetterReason,$errorCode"

# Nested body property
dump-dlq -n ns.servicebus.windows.net -q myqueue -i --categorize-by "#Subject,$error.severity"

# Three dimensions
dump-dlq -n ns.servicebus.windows.net -q myqueue -i --categorize-by "$tier,#Subject,#DeadLetterReason"
```

The table headers update dynamically:

```text
╭───┬──────────────────────────┬─────────────────┬───────╮
│ # │ #DeadLetterReason        │ $error.severity │ Count │
├───┼──────────────────────────┼─────────────────┼───────┤
│ 1 │ MaxDeliveryCountExceeded │ critical        │   312 │
│ 2 │ MaxDeliveryCountExceeded │ warning         │   198 │
│ 3 │ TTLExpiredException      │ info            │    47 │
╰───┴──────────────────────────┴─────────────────┴───────╯
```

## Architecture

### Core Types

The engine is built on three new types in `Application/DeadLetters/Common/`:

**`CategoryPropertyRef`** — A parsed reference to a single property. Knows whether it's a system or body property and
carries the dot-separated path.

```csharp
public enum PropertySource { System, Body }

public sealed record CategoryPropertyRef(PropertySource Source, string PropertyPath)
{
    public string DisplayName => Source == PropertySource.System
        ? $"#{PropertyPath}" : $"${PropertyPath}";

    public static CategoryPropertyRef Parse(string reference);
    // "#Subject"  → (System, "Subject")
    // "$error.code" → (Body, "error.code")
}
```

**`CategorizationSchema`** — An ordered list of property references that defines the categorization dimensions. Provides
a static `Default` that preserves backward compatibility.

```csharp
public sealed class CategorizationSchema
{
    public static readonly CategorizationSchema Default = new([
        new(PropertySource.System, "Subject"),
        new(PropertySource.System, "DeadLetterReason")
    ]);

    public IReadOnlyList<CategoryPropertyRef> Properties { get; }
    public int DimensionCount => Properties.Count;
    public bool UsesBodyProperties { get; }  // cached flag for optimization

    public static CategorizationSchema Parse(IEnumerable<string>? references);
    // null/empty → Default
}
```

**`CategoryPropertyResolver`** — Resolves a `CategoryPropertyRef` against a `ServiceBusReceivedMessage` to produce a
string value. Handles system property dispatch, JSON body deserialization with caching, and dot-path navigation.

### From 2D to N-dimensional

The key structural change was evolving `DlqCategoryKey` from a two-field record to an N-dimensional key:

```text
Before:  sealed record DlqCategoryKey(string Label, string DeadLetterReason)
After:   sealed class  DlqCategoryKey(ImmutableArray<string> Values) + IEquatable
```

The same transformation applied to `DlqCategory`. Both types retain backward-compatible `Label` and `DeadLetterReason`
convenience properties that index into `Values[0]` and `Values[1]`, so existing code that only uses the default schema
continues to work unchanged.

Custom `IEquatable<DlqCategoryKey>` and `GetHashCode()` implementations were necessary because `ImmutableArray<T>` does
not provide structural equality — the default record equality would compare by reference, breaking dictionary lookups
and grouping.

### Property Resolution

System properties resolve via a switch expression over known `ServiceBusReceivedMessage` property names:

```text
Subject, DeadLetterReason, ContentType, CorrelationId,
MessageId, SessionId, ReplyTo, To, DeadLetterErrorDescription
```

Unrecognized names fall through to `message.ApplicationProperties`, enabling categorization by custom headers without a
special syntax. If nothing matches, the value is `"(none)"`.

Body properties use the existing `MessageBodyDecoder.Decode()` to get a `JsonNode`, then navigate the dot-separated path
segment by segment. A `ConcurrentDictionary<long, JsonNode?>` keyed by `SequenceNumber` caches decoded bodies —
important because the reactive scanning architecture rebuilds category snapshots every second from the same cached
messages.

### Integration with Existing Features

**`--merge-similar`** — The LCS-based category merger was generalized from 2 hardcoded dimensions (label frame + reason
frame) to N dimensions. The `TokenizedCategory` type changed from `(string[] LabelTokens, string[] ReasonTokens)` to
`string[][] DimensionTokens`. Scoring computes per-dimension LCS scores and requires all to meet the 0.5 threshold. The
core LCS, scoring, and template rendering algorithms are unchanged.

**Reactive scanning** — The `StreamDlq` command, `DlqScanSession`, and `DlqCategoryScanner` all accept optional
`CategorizationSchema` and `CategoryPropertyResolver` parameters. When omitted, they fall back to the default schema.
The resolver's body cache integrates naturally with the reactive architecture — bodies are decoded once and reused
across snapshot rebuilds.

**Interactive display** — `DlqCategoryDisplay.GenerateTableData()` generates column headers dynamically from
`schema.Properties.Select(p => p.DisplayName)` instead of hardcoded `"Label"` / `"DeadLetterReason"` strings. The table
adapts to any number of dimensions.

## Data Flow

```text
CLI option                    Application layer              Display
────────────                  ─────────────────              ───────

--categorize-by               CategorizationSchema.Parse()
"#DeadLetterReason,$tier"  →  Schema { Properties: [        → Table headers:
                                (System, "DeadLetterReason"),   "#DeadLetterReason", "$tier"
                                (Body, "tier")
                              ]}
                                      │
                                      ▼
                              DlqCategoryKey.FromMessage()
                              resolver.ResolveProperty(msg, prop)
                                      │
                                      ▼
                              DlqCategoryKey(["MaxDelivery..", "1"])
                                      │
                                      ▼
                              GroupBy key → DlqCategory(values, count)
                                      │
                                      ▼
                              CategoryMerger.Merge() (if --merge-similar)
                                      │
                                      ▼
                              Interactive selection → ExpandKeys → Filter
```

## Design Decisions

**Sealed class over record for `DlqCategoryKey`** — Records generate equality based on field values, but
`ImmutableArray<T>` has reference equality semantics. A sealed class with explicit `IEquatable` implementation gives
correct structural equality for dictionary keys and LINQ grouping.

**ApplicationProperties fallback for `#` syntax** — Rather than requiring a separate prefix for custom headers,
unrecognized `#PropertyName` values fall through to `message.ApplicationProperties`. This means `#Diagnostic-Id`
resolves a custom header, while `#Subject` resolves the built-in property. One syntax covers both.

**Body cache keyed by SequenceNumber** — Each message in a Service Bus peek has a unique, stable sequence number. Using
this as the cache key (rather than MessageId) avoids issues with duplicate message IDs and aligns with how the reactive
cache identifies messages.

**`"(none)"` for unresolved values** — When a property doesn't exist on a message (wrong path, binary body, null value),
the resolver returns `"(none)"` rather than throwing. This groups all unresolvable messages together in one category,
which is the most useful behavior for interactive exploration.

**Default schema for backward compatibility** — When `--categorize-by` is not specified,
`CategorizationSchema.Parse(null)` returns `CategorizationSchema.Default` (`#Subject,#DeadLetterReason`). Every code
path that previously hardcoded these two properties now passes `schema ?? CategorizationSchema.Default`, producing
identical behavior.

## Files

3 new files in `Application/DeadLetters/Common/` (`CategoryPropertyRef`, `CategorizationSchema`,
`CategoryPropertyResolver`), 6 modified core types (`DlqCategoryKey`, `DlqCategory`, `DlqCategorySnapshot`,
`DlqCategoryScanner`, `DlqCategoryDisplay`, `CategoryMerger`), 6 modified infrastructure files (`DlqScanSession`,
`DlqMessageService`, `StreamDlq`, `CategorySelection`, `DlqScanSessionExtensions`, `StreamDlqCategories`), and 8 CLI
files across all 4 commands (CLI option + handler parse call each).
