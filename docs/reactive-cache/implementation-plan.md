# Plan: In-Memory Reactive Cached Message Tracking for DLQ Resubmit

## Context

The current interactive `resubmit-dlq` flow has critical UX and performance issues:

1. **No incremental visibility** - User sees nothing until ALL DLQ messages are peeked and categorized
2. **Unbounded wait** - In high-traffic scenarios, new DLQs keep arriving; the "fetch all" step may never complete
3. **Double fetch** - After user picks categories, the entire DLQ is re-fetched from Service Bus for the actual resubmit
4. **No cycle protection** - A resubmitted message that dead-letters again could be resubmitted endlessly

The solution uses DynamicData's `SourceCache` to build an in-memory reactive cache that streams categorization to the
user as messages arrive, allows selection at any point, and resubmits from the cached snapshot without re-fetching.

## Dependencies to Add

**`src/ServiceBusToolset.Application/ServiceBusToolset.Application.csproj`**: Add `DynamicData` (latest stable, ~9.0.4)

**`tests/ServiceBusToolset.Application.Tests/ServiceBusToolset.Application.Tests.csproj`**: Add
`Microsoft.Reactive.Testing` v6.1.0

## New Files

### 1. Generic Reactive Message Cache

**`src/ServiceBusToolset.Application/Common/ServiceBus/Reactive/ReactiveMessageCache.cs`**

Generic `SourceCache<TMessage, TKey>` wrapper. Reusable for any queue-based feature.

- `AddOrUpdate(IEnumerable<TMessage>)` - Thread-safe batch add via `_cache.Edit()`
- `MarkComplete()` - Signals no more messages will be added (sets `IsComplete` flag)
- `Connect()` - Returns `IObservable<IChangeSet<TMessage, TKey>>` for reactive operators
- `Snapshot()` - Point-in-time `IReadOnlyList<TMessage>` via `_cache.Items`
- `Count` / `CountChanged` / `Lookup(key)` - Utility accessors
- Implements `IDisposable`

### 2. Resubmit Cycle Tracker

**`src/ServiceBusToolset.Application/Common/ServiceBus/Reactive/ResubmitTracker.cs`**

Thread-safe `HashSet<string>` tracking resubmitted MessageIds. Prevents infinite cycles when a resubmitted message
dead-letters again.

- `MarkResubmitted(string messageId)` / `MarkResubmitted(IEnumerable<string>)`
- `WasResubmitted(string messageId) -> bool`
- Lock-based synchronization (simple add/check pattern)

### 3. DLQ Resubmit Session

**`src/ServiceBusToolset.Application/DeadLetters/ResubmitDlq/DlqResubmitSession.cs`**

Session object tying together the cache, category stream, and resubmit tracker.

- `DlqCategorySnapshot` record: `(IReadOnlyList<DlqCategory> Categories, int TotalMessageCount, bool IsComplete)`
- `DlqResubmitSession` class:
    - `Cache` - `ReactiveMessageCache<ServiceBusReceivedMessage, long>` (keyed by SequenceNumber)
    - `CategoryStream` - `IObservable<DlqCategorySnapshot>` (throttled 1s)
    - `ResubmitTracker` - cycle prevention
    - `Error` - `Exception?` property, set by background feed task on failure; CLI checks this before proceeding
    - `SnapshotForCategories(IReadOnlySet<DlqCategoryKey>, DateTimeOffset? beforeTime)` - filters cache snapshot by
      category keys, excludes already-resubmitted messages, optionally filters by enqueue time
    - Implements `IDisposable`
    - **Single-round session**: user selects once, resubmits, session ends

### 4. Stream DLQ Categories Command + Handler

**`src/ServiceBusToolset.Application/DeadLetters/ResubmitDlq/StreamDlqCategories.cs`**

New Mediator command that returns immediately with a `DlqResubmitSession`.

**Command**: `StreamDlqCategoriesCommand(FullyQualifiedNamespace, Target) : ICommand<Result<DlqResubmitSession>>`

**Handler logic**:

1. Creates `ReactiveMessageCache` + `ResubmitTracker`
2. Builds category stream: `cache.Connect().Throttle(1s).Select(_ => BuildCategorySnapshot(cache)).StartWith(empty)`
3. Starts background `Task.Run(FeedCacheAsync)` that peeks DLQ in batches of 100 (reuses
   `ReceiverFactory.CreateDlqReceiver` and `PeekMessagesAsync` pattern from `DlqMessageService.AnalyzeCategoriesAsync`)
4. Returns session immediately

**`BuildCategorySnapshot`**: Groups cached messages by `DlqCategoryKey.FromMessage(subject, deadLetterReason)` into
`Dictionary<DlqCategoryKey, int>`, same logic as current `DlqMessageService.AnalyzeCategoriesAsync`.

**`FeedCacheAsync`**: Peeks batches using sequence-number-based pagination. Filters out
`resubmitTracker.WasResubmitted(messageId)` before adding to cache. Calls `cache.MarkComplete()` when no more messages.
On exception: stores the error on the session (`session.Error`) and marks complete so the UI can surface the error to
the user.

### 5. Resubmit From Cache Command + Handler

**`src/ServiceBusToolset.Application/DeadLetters/ResubmitDlq/ResubmitFromCache.cs`**

New Mediator command that resubmits from a pre-built snapshot (no re-fetch of categories).

**Command**:
`ResubmitFromCacheCommand(FullyQualifiedNamespace, Target, TargetEntity, IReadOnlyList<ServiceBusReceivedMessage> MessagesToResubmit, ResubmitTracker, IProgress?)` :
`ICommand<Result<ResubmitDlqResult>>`

**Handler logic** (follows pattern of existing `ResubmitDlqMessagesCommandHandler`):

1. Builds `HashSet<long> targetSequenceNumbers` from snapshot
2. Uses `ReceiveMessagesAsync` (destructive read) in batches
3. Matches received messages by sequence number:
    - **Match**: Resubmit (send + complete) using `MessageResubmitHelper.CreateResubmitMessage`, then
      `tracker.MarkResubmitted(messageId)`
    - **No match**: Abandon (put back on DLQ)
4. Early exit when `targetSequenceNumbers` is empty
5. Returns existing `ResubmitDlqResult(resubmittedCount, skippedCount)`

### 6. Shared Message Resubmit Helper (Extract)

**`src/ServiceBusToolset.Application/DeadLetters/Common/MessageResubmitHelper.cs`**

Extract `CreateResubmitMessage(ServiceBusReceivedMessage)` from `ResubmitDlqMessagesCommandHandler` (line 165-188) into
a static helper. Both the existing handler and the new `ResubmitFromCacheCommandHandler` will use it.

## Modified Files

### 7. Existing Resubmit Handler

**`src/ServiceBusToolset.Application/DeadLetters/ResubmitDlq/ResubmitDlqMessagesCommandHandler.cs`**

Replace private `CreateResubmitMessage` with call to `MessageResubmitHelper.CreateResubmitMessage`. No other changes -
the non-interactive path remains identical.

### 8. CLI Interactive Handler

**`src/ServiceBusToolset.CLI/DeadLetters/ResubmitDlq/ResubmitDlqCommandHandler.cs`**

Rewrite **only** `ExecuteInteractiveResubmitAsync`. The dry-run and non-interactive paths are unchanged.

New flow:

1. `mediator.Send(StreamDlqCategoriesCommand)` - returns session immediately
2. Subscribe to `session.CategoryStream` - stores `latestSnapshot` in a lock-protected variable; renders category table
   on each emission
3. `Output.ReadLine()` - blocks for user input while stream continues updating `latestSnapshot` in background
4. On user input: take `latestSnapshot`, parse selection with existing `CategorySelectionParser.Parse`, build
   `CategorySelection`
5. `session.SnapshotForCategories(selectedKeys, beforeTime)` - get filtered message snapshot
6. `mediator.Send(ResubmitFromCacheCommand)` - resubmit from snapshot
7. Display results

Add private `RenderCategoryTable(DlqCategorySnapshot, string entityDescription)` method:

- Shows progress line ("Scanning... N messages") when no categories yet
- Shows category table via existing `DlqCategoryDisplay.DisplayTable` + loading indicator when not complete

## Data Flow

```
CLI: ExecuteInteractiveResubmitAsync
  |
  |-> Send(StreamDlqCategoriesCommand) -> returns DlqResubmitSession
  |     |-> Background: FeedCacheAsync peeks batches -> cache.AddOrUpdate()
  |     |-> Reactive: cache.Connect().Throttle(1s) -> DlqCategorySnapshot emissions
  |
  |-> Subscribe(CategoryStream) -> renders table every ~1s
  |-> ReadLine() (user can select at ANY time, even while loading)
  |
  |-> User selects "1,3" -> snapshot categories at that moment
  |-> session.SnapshotForCategories(keys) -> filters by category + excludes resubmitted
  |
  |-> Send(ResubmitFromCacheCommand(snapshot)) -> destructive receive + match by SeqNum
  |     |-> Send to target, Complete originals, tracker.MarkResubmitted()
  |-> Display success
```

## Key Design Decisions

- **Cache key = SequenceNumber (long)**: Unique within a queue/subscription. MessageId can duplicate.
- **Tracker key = MessageId (string)**: Persists across resubmit cycles (a resubmitted message gets a new SequenceNumber
  but keeps its MessageId).
- **Throttle(1s)** on category stream: User requested "refresh UI every second", not on every change.
- **Peek (non-destructive) for cache**: Messages stay in DLQ. Destructive receive happens only during resubmit.
- **Snapshot-based resubmit**: No re-categorization or re-fetch. Exact messages from the cache at selection time.
- **Background task for feeding**: Handler returns immediately, background task populates cache. Follows
  `MonitorSubscriptionsCommandHandler` pattern.
- **Error surfacing**: Background peek errors stored on `session.Error`; CLI checks and displays to user rather than
  silently marking complete.
- **Single round**: User selects categories once, resubmits, session ends. Can be extended to multi-round later.

## Test Plan

### Unit Tests (new files)

**`tests/.../Common/ServiceBus/Reactive/ReactiveMessageCacheShould.cs`**
Uses simple `TestMessage(string Id, string Category)` record, not ServiceBus types:

- `ReturnEmptySnapshot_WhenNoItemsAdded`
- `ContainItems_WhenAddOrUpdateCalled`
- `DeduplicateByKey_WhenSameKeyAddedTwice`
- `EmitChangeSet_WhenItemsAdded`
- `ReportCorrectCount_WhenItemsAdded`
- `ReportIsComplete_WhenMarkCompleteCalled`
- `ReturnPointInTimeSnapshot_WhenSnapshotCalled`

**`tests/.../Common/ServiceBus/Reactive/ResubmitTrackerShould.cs`**

- `ReturnFalse_WhenMessageNotTracked`
- `ReturnTrue_WhenMessageWasMarkedResubmitted`
- `TrackMultipleIds_WhenBatchMarked`
- `BeThreadSafe_WhenAccessedConcurrently`

**`tests/.../DeadLetters/ResubmitDlq/DlqResubmitSessionShould.cs`**
Uses `MockServiceBusClientFactory` + `ServiceBusReceivedMessageBuilder`:

- `ReturnFilteredSnapshot_WhenCategoryKeysProvided`
- `ExcludeResubmittedMessages_WhenSnapshotTaken`
- `ReturnEmptyList_WhenNoCategoriesMatch`
- `ApplyBeforeTimeFilter_WhenBeforeTimeProvided`

**`tests/.../DeadLetters/ResubmitDlq/StreamDlqCategoriesHandlerShould.cs`**

- `ReturnSession_WhenHandled`
- `PopulateCacheWithMessages_WhenFeedCompletes`
- `EmitCategorySnapshots_WhenMessagesArrive`
- `GroupMessagesIntoCategoriesCorrectly`
- `MarkCacheAsComplete_WhenAllMessagesPeeked`
- `ExcludePreviouslyResubmittedMessages_WhenFeeding`

**`tests/.../DeadLetters/ResubmitDlq/ResubmitFromCacheCommandHandlerShould.cs`**
Follows exact patterns from existing `ResubmitDlqMessagesCommandHandlerShould`:

- `ResubmitMatchingMessages_WhenSnapshotProvided`
- `AbandonNonMatchingMessages_WhenNotInSnapshot`
- `TrackResubmittedMessageIds_WhenResubmitSucceeds`
- `ReturnZeroCounts_WhenEmptySnapshot`
- `PreserveMessageProperties_WhenResubmitting`
- `DisposeClient_WhenHandlingCompletes`
- `ReportProgress_WhenProgressProvided`

### Existing Tests

All existing tests in `ResubmitDlqMessagesCommandHandlerShould` must continue passing (non-interactive and dry-run paths
unchanged).

## Verification

1. `dotnet build` - ensure DynamicData integrates, Mediator source generator picks up new commands
2. `dotnet test` - all existing + new tests pass
3. Manual test: `dotnet run --project src/ServiceBusToolset.CLI -- resubmit-dlq -n <ns> -q <queue> -i`
    - Verify categories appear and update as messages are scanned
    - Verify user can select before loading completes
    - Verify resubmit works from snapshot
    - Verify resubmitted messages are skipped if they dead-letter again

## Implementation Order

1. Add NuGet dependencies (Application.csproj, Tests.csproj)
2. `ReactiveMessageCache.cs` + tests
3. `ResubmitTracker.cs` + tests
4. `MessageResubmitHelper.cs` (extract from existing handler)
5. Update `ResubmitDlqMessagesCommandHandler.cs` to use helper
6. `DlqResubmitSession.cs` + tests
7. `StreamDlqCategories.cs` + tests
8. `ResubmitFromCache.cs` + tests
9. Rewrite `ResubmitDlqCommandHandler.ExecuteInteractiveResubmitAsync`
10. Run all tests, verify build
