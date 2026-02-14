# Reactive In-Memory Caching for Interactive DLQ Resubmission

## The Problem

The `resubmit-dlq --interactive` command lets operators inspect dead-letter queue (DLQ) messages grouped by category (subject + dead-letter reason) and selectively resubmit them. The original implementation had a sequential, blocking flow that created serious usability problems at scale:

```
1. Peek ALL messages from DLQ           ← blocks until complete
2. Categorize them in memory            ← user sees nothing during this
3. Display category table               ← only now does the user see anything
4. User selects categories
5. Re-fetch ALL messages from DLQ       ← double the work
6. Filter and resubmit matching ones
```

### What went wrong

**Blind waiting.** A DLQ with 50,000 messages takes minutes to peek through in batches of 100. The user stares at a blank terminal the entire time, with no indication of progress or even whether the tool is working.

**Unbounded scanning.** In high-traffic systems, new messages dead-letter continuously. The "peek all" loop may never terminate because new messages keep appearing beyond the last sequence number.

**Double fetch.** After the user picks categories, the tool re-fetches every message from scratch to find the ones matching the selection. This doubles the total Service Bus API calls and wall-clock time.

**No cycle protection.** If a resubmitted message immediately dead-letters again (common with poison messages), nothing prevents the user from resubmitting it in an infinite loop.

## The Solution: Reactive In-Memory Cache

The refactored implementation replaces the sequential pipeline with a reactive, streaming architecture built on [DynamicData](https://github.com/reactivemarbles/DynamicData), a library that adds reactive collection capabilities to .NET's `System.Reactive`.

The new flow:

```
1. Start background peek → cache messages as they arrive
2. Every second, rebuild category snapshot from cache
3. Render live-updating table (Console.Clear + redraw)
4. User can press 'x' to stop scanning at any point
5. Take point-in-time snapshot from cache
6. Resubmit only the snapshot — no re-fetch
```

The key insight: **decouple data ingestion from user interaction**. Messages stream into a cache on a background thread. The UI subscribes to the cache's change notifications and redraws periodically. The user's selection operates on a snapshot of whatever has been cached so far — not on a separate fetch.

## How DynamicData Works (and Why We Need It)

[DynamicData](https://github.com/reactivemarbles/DynamicData) extends Rx.NET with *reactive collections*. Its core primitive is `SourceCache<TObject, TKey>` — a thread-safe, observable dictionary. When items are added, updated, or removed, the cache emits an `IChangeSet<TObject, TKey>` through its `Connect()` observable.

This is fundamentally different from a plain `Dictionary` or `ConcurrentDictionary`:

| Feature | `ConcurrentDictionary` | DynamicData `SourceCache` |
|---|---|---|
| Thread-safe mutations | Yes | Yes |
| Notify on changes | No | Yes, via `IObservable<IChangeSet>` |
| Composable queries | No | Yes (filter, group, transform, sort) |
| Batch mutations | No | Yes, via `Edit()` |

For our use case, the critical capability is `Connect()`: it returns an `IObservable<IChangeSet>` that fires whenever the cache contents change. We pipe this through Rx operators to build a derived stream:

```csharp
var categoryStream = cache.Connect()                     // fires on every mutation
    .Sample(TimeSpan.FromSeconds(1))                     // emit at most once per second
    .Select(_ => BuildCategorySnapshot(cache))           // rebuild categories from current state
    .StartWith(new DlqCategorySnapshot([], 0, false));   // initial empty snapshot
```

### Why `.Sample()` and not `.Throttle()`

This distinction caused an actual bug during development. In Rx.NET:

- **`Throttle(1s)`** = *debounce*. Only emits after 1 second of **silence** (no upstream events). When batches arrive every ~100ms back-to-back, the throttle never fires until the entire feed completes.
- **`Sample(1s)`** = *periodic sampling*. Emits the most recent upstream value every 1 second, regardless of how fast events arrive.

With `Throttle`, the UI would freeze during scanning and only update after all 12,000 messages were peeked. `Sample` gives steady 1-second UI refreshes.

## Architecture

### Layer Separation

The implementation follows the project's vertical slice architecture with clear layer boundaries:

```
Application Layer (business logic)
├── Common/ServiceBus/Reactive/
│   ├── ReactiveMessageCache<TMessage, TKey>   ← generic, reusable
│   └── ResubmitTracker                        ← cycle prevention
│
├── DeadLetters/Common/
│   └── MessageResubmitHelper                  ← extracted shared logic
│
└── DeadLetters/ResubmitDlq/
    ├── DlqResubmitSession                     ← session object (cache + stream + tracker)
    ├── StreamDlqCategories                    ← command: start scanning, return session
    └── ResubmitFromCache                      ← command: resubmit from snapshot

CLI Layer (presentation)
└── DeadLetters/ResubmitDlq/
    └── ResubmitDlqCommandHandler              ← two-phase interactive flow
```

### The Session Object

`DlqResubmitSession` is the central coordination point. It owns:

- **`Cache`** — `ReactiveMessageCache<ServiceBusReceivedMessage, long>` keyed by `SequenceNumber`. SequenceNumber is unique within a queue/subscription (unlike MessageId which can duplicate).
- **`CategoryStream`** — `IObservable<DlqCategorySnapshot>` that emits category breakdowns every second.
- **`ResubmitTracker`** — tracks which MessageIds have been resubmitted, preventing cycles.
- **`ScanCompletion`** — `TaskCompletionSource` signaling when the background feed finishes.
- **`TotalDlqCount`** — total DLQ message count from runtime properties (best-effort, for progress display).
- **`StopScanning()`** — cancels the background feed via a `CancellationTokenSource`.

The session is `IDisposable` and cleans up the cache and CTS.

### Background Feed

`StreamDlqCategoriesCommandHandler.FeedCacheAsync` runs on a background thread:

1. **Fetch total count** (best-effort) — queries `ServiceBusAdministrationClient` for the DLQ message count. This enables "Peeked 200 from 12000" progress display. If the admin call fails (permissions, etc.), scanning continues without the total.

2. **Peek in batches** — uses `PeekMessagesAsync` (non-destructive) with sequence-number pagination. Each batch is filtered through the `ResubmitTracker` to exclude already-resubmitted messages, then added to the cache.

3. **Signal completion** — in the `finally` block, marks the cache complete and sets `ScanCompletion`. This fires regardless of success, cancellation, or error.

The feed respects a *linked* `CancellationTokenSource` combining the global cancellation token (Ctrl+C) with the session's scan token (press 'x'):

```csharp
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
    cancellationToken, session.ScanCancellationToken);
await FeedCacheAsync(..., linkedCts.Token);
```

### Cache-Based Resubmit

`ResubmitFromCacheCommandHandler` takes a pre-built message snapshot and resubmits by sequence number matching:

1. Build a `HashSet<long>` of target sequence numbers from the snapshot.
2. `ReceiveMessagesAsync` (destructive read) in batches from the DLQ.
3. For each received message:
   - **Match** (sequence number in set): create a new `ServiceBusMessage` preserving all properties, send to target, complete the original, mark as resubmitted in the tracker.
   - **No match**: abandon (put back on DLQ for other consumers).
4. Exit when all target sequence numbers are processed or after 3 consecutive empty batches.

This eliminates the double-fetch problem. The snapshot was built during the peek phase; the resubmit phase only does the destructive receive-and-forward.

## The Two-Phase CLI Flow

The CLI handler splits the interactive experience into two distinct phases:

### Phase 1: Scanning (live updates, no user input)

```csharp
using (session.CategoryStream.Subscribe(snapshot =>
{
    Console.Clear();
    RenderScanningView(snapshot, entityDescription, session.TotalDlqCount);
}))
{
    var scanTask = session.ScanCompletion.Task;
    var keyTask = Task.Run(() => WaitForStopKey(session.ScanCancellationToken));

    await Task.WhenAny(scanTask, keyTask);
    session.StopScanning();
    await scanTask;
}
```

During this phase:
- The reactive subscription redraws the screen every second with the latest category table.
- A separate thread polls `Console.KeyAvailable` for the 'x' key.
- No `ReadLine()` is called, so `Console.Clear()` is safe (no input buffer to disrupt).
- The phase ends when either the feed completes naturally or the user presses 'x'.

The display shows:
- When no categories yet: `Scanning DLQ for queue 'orders'... Peeked 200 from 12000`
- When categories exist: the category table + `Scanning... Peeked 3400 from 12000`
- Always: `Press 'x' to stop scanning and select categories`

### Phase 2: Selection (static display, user input)

After Phase 1, the subscription is disposed (no more screen redraws). The handler:

1. Takes a final snapshot from the cache via `BuildCategorySnapshot()`.
2. Clears the screen and renders the final category table.
3. Prompts for selection — this `ReadLine()` is now safe because nothing will redraw the screen.
4. Parses the selection, takes a message snapshot for the chosen categories, and resubmits.

This separation solves the original UX bugs:
- No "empty state" confusion — the scanning indicator appears immediately.
- No repeated tables — `Console.Clear()` before each render gives in-place updates.
- No lost prompt — the prompt only appears after scanning stops, and nothing can push it off-screen.

## Cycle Prevention

The `ResubmitTracker` is a thread-safe `HashSet<string>` keyed by `MessageId`. It prevents infinite resubmit loops:

1. **During cache feeding**: messages whose `MessageId` is in the tracker are filtered out before entering the cache. If a message dead-letters again after resubmission, it gets a new `SequenceNumber` but keeps its `MessageId` — so the tracker catches it.

2. **During resubmission**: after successfully sending a message to the target, its `MessageId` is added to the tracker.

3. **During snapshot filtering**: `SnapshotForCategories()` excludes tracked messages, so even if a resubmitted message somehow re-enters the cache, it won't appear in the selection.

The tracker uses `MessageId` (not `SequenceNumber`) because a resubmitted message that dead-letters again gets a new sequence number but retains its message ID.

## Non-Interactive Confirmation

A smaller but important UX fix: the non-interactive `resubmit-dlq` path (without `-i`) now asks for confirmation before proceeding:

```
Resubmitting DLQ messages for queue 'orders'...
Are you sure you want to resubmit all dead letter messages? (y/N):
```

This prevents accidental bulk resubmission when the user forgets the `--dry-run` flag.

## Key Design Decisions

| Decision | Rationale |
|---|---|
| Cache key = `SequenceNumber` (long) | Unique within a queue/subscription. `MessageId` can duplicate across messages. |
| Tracker key = `MessageId` (string) | Persists across resubmit cycles — a resubmitted message gets a new SequenceNumber but keeps its MessageId. |
| `.Sample(1s)` on category stream | Periodic updates regardless of activity. `.Throttle` (debounce) would freeze the UI during rapid ingestion. |
| Peek (non-destructive) for cache | Messages stay in the DLQ during scanning. Destructive receive only happens during the actual resubmit. |
| Snapshot-based resubmit | No re-categorization or re-fetch. The exact messages from the cache at selection time. |
| Best-effort total count | The admin API call to get the total DLQ count can fail (permissions, transient errors). Scanning works without it; the progress display just shows "N messages found so far" instead of "Peeked N from M". |
| `TaskCompletionSource` for scan completion | The reactive stream's `Sample` operator may not emit after the last changeset. A dedicated signal ensures the CLI always knows when scanning finishes. |
| Linked CancellationTokenSource | Combines Ctrl+C (global) and press-x (user-initiated) cancellation into a single token for the feed loop. The linked CTS is disposed inside the `Task.Run` lambda after the feed completes. |

## Test Coverage

The implementation includes comprehensive tests at every layer:

**Unit tests** (Application layer):
- `ReactiveMessageCacheShould` — empty snapshots, add/update, deduplication, changeset emission, point-in-time snapshots.
- `ResubmitTrackerShould` — single/batch tracking, false negatives, thread safety under concurrent access.
- `DlqResubmitSessionShould` — category filtering, resubmit exclusion, before-time filtering, empty results.
- `StreamDlqCategoriesHandlerShould` — session creation, cache population, category grouping, completion marking, resubmit exclusion during feed.
- `ResubmitFromCacheCommandHandlerShould` — matching/abandoning messages, tracker integration, empty snapshots, property preservation, progress reporting.

**Integration tests** (against Service Bus emulator):
- `StreamDlqCategoriesIntegrationShould` — full end-to-end cache population, multi-category grouping, stream emission, empty DLQ handling.
- `ResubmitFromCacheIntegrationShould` — full resubmit flow, category-filtered resubmit, tracker verification, message property preservation, empty snapshot handling.

## File Summary

### New files

| File | Purpose |
|---|---|
| `Common/ServiceBus/Reactive/ReactiveMessageCache.cs` | Generic DynamicData `SourceCache` wrapper |
| `Common/ServiceBus/Reactive/ResubmitTracker.cs` | Thread-safe MessageId cycle tracker |
| `DeadLetters/Common/MessageResubmitHelper.cs` | Extracted message cloning logic |
| `DeadLetters/ResubmitDlq/DlqResubmitSession.cs` | Session: cache + stream + tracker + signals |
| `DeadLetters/ResubmitDlq/StreamDlqCategories.cs` | Command to start scanning and return session |
| `DeadLetters/ResubmitDlq/ResubmitFromCache.cs` | Command to resubmit from cached snapshot |

### Modified files

| File | Change |
|---|---|
| `ResubmitDlqMessagesCommandHandler.cs` | Use `MessageResubmitHelper` instead of private method |
| `ResubmitDlqCommandHandler.cs` (CLI) | Two-phase interactive flow, scanning view, stop-key, confirmation prompt |
| `ServiceBusToolset.Application.csproj` | Added `DynamicData` 9.0.4 |
| `ServiceBusToolset.Application.Tests.csproj` | Added `Microsoft.Reactive.Testing` 6.1.0 |
