# Porting Reactive Scanning and Smart Categorization to All DLQ Commands

## Background

The [reactive cache](reactive-cache.md) and [smart category merging](smart-category-merging.md) were originally built exclusively for `resubmit-dlq --interactive`. The three other interactive DLQ commands — `dump-dlq`, `purge-dlq`, and `diagnose-dlq` — still used the old blocking flow:

```
1. Peek ALL messages from DLQ           ← blocks, no progress
2. Categorize in memory                 ← user stares at blank terminal
3. Display static category table
4. User selects categories
5. Re-fetch matching messages from DLQ  ← doubles the API calls
6. Execute operation (dump/purge/diagnose)
```

This was implemented via `AnalyzeDlqCategoriesCommand` → `DlqMessageService.AnalyzeCategoriesAsync`, a blocking peek-all-then-categorize pipeline with a progress callback. After categorization completed, each command sent its own operation command which re-peeked Service Bus from scratch with filters applied.

The goal: give every interactive DLQ command the same live-updating, no-re-fetch experience that resubmit already had — without duplicating 300+ lines of scanning and UI code four times.

## The Extraction

The reactive scanning logic was embedded directly in resubmit-specific types: `DlqResubmitSession` owned the cache and stream, `StreamDlqCategoriesCommandHandler` contained the `FeedCacheAsync` and `BuildCategorySnapshot` static methods, and the CLI handler in `ResubmitDlqCommandHandler` had its own rendering and keyboard-polling code. Porting meant extracting all of this into shared abstractions.

### Application Layer: Three Shared Types

**`DlqScanSession`** — base class extracted from `DlqResubmitSession`, holding everything a scan needs:

```csharp
public class DlqScanSession : IDisposable
{
    public ReactiveMessageCache<ServiceBusReceivedMessage, long> Cache { get; }
    public IObservable<DlqCategorySnapshot> CategoryStream { get; }
    public TaskCompletionSource ScanCompletion { get; }
    public long TotalDlqCount { get; set; }
    public Exception? Error { get; set; }
    public CancellationToken ScanCancellationToken { get; }

    public void StopScanning();
    public IReadOnlyList<ServiceBusReceivedMessage> SnapshotForCategories(
        IReadOnlySet<DlqCategoryKey> keys, DateTimeOffset? beforeTime);

    protected virtual bool MatchesFilter(ServiceBusReceivedMessage message) => true;
}
```

The `virtual MatchesFilter` is the extension point. `DlqResubmitSession` overrides it to exclude already-resubmitted messages via `ResubmitTracker`. The other three commands use the base implementation which accepts everything.

**`DlqCategoryScanner`** — static class with the two core operations extracted from `StreamDlqCategoriesCommandHandler`:

- `BuildCategorySnapshot(cache, mergeSimilar)` — groups cache contents by Subject + DeadLetterReason, optionally runs `CategoryMerger.Merge`, returns a `DlqCategorySnapshot`.
- `FeedCacheAsync(clientFactory, namespace, target, cache, session, messageFilter?, ct)` — background pagination loop. The key generalization: `messageFilter` is now an optional `Func<ServiceBusReceivedMessage, bool>?` instead of a hardcoded `ResubmitTracker` check. Resubmit passes `m => !tracker.WasResubmitted(m.MessageId)`; everyone else passes `null`.

**`StreamDlqCommand` / `StreamDlqCommandHandler`** — a new shared Mediator command that creates a plain `DlqScanSession`, starts the background feed via `Task.Run`, and returns the session immediately. The existing `StreamDlqCategoriesCommand` still exists for resubmit, creating a `DlqResubmitSession` with its tracker-aware filter.

**`DlqCategorySnapshot`** — moved from being a nested record inside `DlqResubmitSession.cs` to its own file in `DeadLetters/Common/`:

```csharp
public sealed record DlqCategorySnapshot(
    IReadOnlyList<DlqCategory> Categories,
    int TotalMessageCount,
    bool IsComplete,
    CategoryMergeResult? MergeResult = null);
```

### CLI Layer: Shared Interactive Flow

**`DlqScanSessionExtensions`** — the biggest new file (~209 lines), containing two extension methods on `DlqScanSession` that replace the per-handler UI code:

`RunScanningPhaseAsync(session, output, entityDescription)` — the live scanning phase. Replaces the old `Console.Clear()` loop with `AnsiConsole.Live` for flicker-free in-place updates. Subscribes to `session.CategoryStream` and refreshes on each emission. Adds keyboard scrolling (arrow keys scroll by row, Shift+arrow by page) and handles `Console.IsInputRedirected` for non-interactive environments.

`GetCategorySelection(session, output, mergeSimilar, beforeTime, actionVerb)` — the post-scan selection phase. Builds a final snapshot, renders the static table, reads user input, parses it, expands merged keys if applicable, and calls `session.SnapshotForCategories` to get the actual messages. Returns an `InteractiveCategorySelection` record (messages + selected category count), or `null` if cancelled.

The `actionVerb` parameter ("dump", "purge", "diagnose", "resubmit") customizes the prompt text so each command gets contextual prompts like "Enter categories to purge" rather than a generic message.

### What Each CLI Handler Looks Like Now

Before (dump example, ~40 lines of inline scanning + selection):

```csharp
private async Task<Result<DlqDumpResult>> ExecuteInteractiveDumpAsync(...)
{
    Output.Info("Analyzing DLQ categories...");
    var analyzeResult = await Sender.Send(new AnalyzeDlqCategoriesCommand(...));
    // ... render static table, read input, parse selection ...
    var result = await Sender.Send(new DumpDlqMessagesCommand(
        namespace, target, outputPath, selection.Filters, beforeTime));
    return result;
}
```

After (~15 lines, all orchestration):

```csharp
private async Task<Result<DlqDumpResult>> ExecuteInteractiveDumpAsync(...)
{
    var scanResult = await Sender.Send(new StreamDlqCommand(namespace, target, mergeSimilar));
    var session = scanResult.Value;

    await session.RunScanningPhaseAsync(Output, entityDescription);
    var selection = session.GetCategorySelection(Output, mergeSimilar, beforeTime, "dump");
    if (selection is null) return Result.Success(new DlqDumpResult(0));

    return await Sender.Send(new DumpFromCacheCommand(selection.Messages, outputPath));
}
```

The same three-line pattern — `StreamDlq` → `RunScanningPhaseAsync` → `GetCategorySelection` → `*FromCache` — now drives all four interactive commands.

## Cache-Based Operations: No Re-Fetch

The old approach sent operation commands that re-peeked Service Bus with category filters. The new approach sends already-fetched messages directly.

### `DumpFromCacheCommand`

Takes `IReadOnlyList<ServiceBusReceivedMessage>` + `OutputFilePath`. Serializes messages to JSON via `MessageSerializer`. No Service Bus connection needed — pure in-memory transformation.

### `PurgeFromCacheCommand`

Takes the cached message list with known sequence numbers. Opens a destructive `ReceiveMessagesAsync` receiver on the DLQ, builds a `HashSet<long>` of target sequence numbers for O(1) lookup:

- **Match** (sequence number in set): `CompleteMessageAsync` — permanently removes from DLQ.
- **No match**: `AbandonMessageAsync` — returns to DLQ for other consumers.
- **Exit**: when all targets are processed, or after 3 consecutive empty receive batches.

This is the same receive-match-or-abandon pattern that `ResubmitFromCacheCommandHandler` already used, adapted for purge (complete without resubmitting).

### `DiagnoseFromCacheCommand`

Takes the message list and delegates to `MessageDiagnostics.DiagnoseMessagesAsync` — a static helper extracted from the old `DiagnoseDlqCommandHandler`. The diagnostics logic (operation ID extraction, App Insights correlation lookup) was previously private methods buried in the handler; now it's a shared utility in `DiagnoseDlq/Common/MessageDiagnostics.cs`.

## The Inheritance Hierarchy

```
DlqScanSession (base)
├── Used directly by: dump-dlq, purge-dlq, diagnose-dlq
│   Created by: StreamDlqCommandHandler
│   MatchesFilter: always true (no filtering)
│
└── DlqResubmitSession (subclass)
    Used by: resubmit-dlq
    Created by: StreamDlqCategoriesCommandHandler
    MatchesFilter: excludes already-resubmitted via ResubmitTracker
    Extra state: ResubmitTracker
```

The base class handles 100% of the scanning lifecycle. The subclass only adds the resubmit-specific filtering concern.

## Purge Confirmation

The non-interactive `purge-dlq` path (without `-i`) now prompts for confirmation before bulk-purging:

```
Purging DLQ for queue 'orders'...
Are you sure you want to purge all dead letter messages? (y/N):
```

This uses `Output.ReadLine()` (via `IConsoleOutput`) rather than `Console.ReadLine()` directly, making it testable. Any input other than "y"/"Y" cancels. This matches the confirmation already present in `resubmit-dlq`.

## Removed Code

| Removed | Replaced By |
|---|---|
| `AnalyzeDlqCategories.cs` — blocking analyze command + handler + result type | `StreamDlq.cs` — reactive streaming command |
| `DlqMessageService.AnalyzeCategoriesAsync` — static peek-all-then-categorize | `DlqCategoryScanner.BuildCategorySnapshot` + `FeedCacheAsync` |
| `DlqCategorySnapshot` nested inside `DlqResubmitSession.cs` | Own file in `DeadLetters/Common/` |
| `BuildCategorySnapshot` / `FeedCacheAsync` in `StreamDlqCategoriesCommandHandler` | Moved to `DlqCategoryScanner` with generalized filter parameter |
| `RenderScanningView` / `WaitForStopKey` in `ResubmitDlqCommandHandler` | `DlqScanSessionExtensions.RunScanningPhaseAsync` with scrolling support |
| Inline category selection logic (duplicated per handler) | `DlqScanSessionExtensions.GetCategorySelection` (parameterized with `actionVerb`) |
| `DiagnoseMessagesAsync` / `ExtractOperationId` / `TryDecodeBody` private in `DiagnoseDlqCommandHandler` | `MessageDiagnostics.cs` static helper |

Net result: ~1,063 lines removed, ~3,982 lines added — but that includes the entirely new test harness project. The core Application and CLI changes are a net simplification: four handlers now share one scanning pipeline instead of each having its own.

## Test Harness

A new `ServiceBusToolset.TestHarness` project was added for seeding realistic test data into a live Azure Service Bus instance. This enables end-to-end manual testing of all commands against real infrastructure.

### Why a Test Harness?

Unit tests and integration tests (against the Service Bus emulator) validate correctness, but they can't exercise the full interactive UX — live table rendering, keyboard scrolling, Spectre.Console formatting, App Insights correlation lookups. The test harness generates a realistic distribution of dead-letter messages with correlated telemetry, so you can run the actual CLI commands and see the complete experience.

### The `generate-dlq` Command

```bash
dotnet run --project src/ServiceBusToolset.TestHarness -- generate-dlq \
  -n mynamespace.servicebus.windows.net \
  -q myqueue \
  -c 200 \
  -a "InstrumentationKey=..."
```

The flow:
1. `DeadLetterMessageFactory.CreateSpecs(count)` generates a list of message specifications
2. Messages are sent to the queue, received back, and dead-lettered with the specified reasons
3. If an App Insights connection string is provided, correlated telemetry is sent via `TelemetryGenerator`

### Realistic Data Distribution

`DeadLetterMessageFactory` produces three tiers of messages designed to exercise different features:

**Tier 1 — Exact categories (40%):** Fixed subjects × fixed reasons (e.g., `"OrderProcessor"` / `"MaxDeliveryCountExceeded"`). These create distinct, countable categories in the normal table view.

**Tier 2 — Parameterized subjects (40%):** Templates with variable values (e.g., `"Error processing order ORD-1001"`, `"Error processing order ORD-1002"`). These generate many singleton categories that `--merge-similar` should cluster into `"Error processing order *"`.

**Tier 3 — Parameterized reasons (20%):** Fixed subjects × parameterized reasons. Also exercises `--merge-similar` clustering on the reason column.

### Telemetry Profiles

Each message is assigned a `TelemetryProfile` controlling what App Insights data gets generated:

| Profile | % | What Gets Sent |
|---|---|---|
| `NoOperationId` | 20% | No `Diagnostic-Id` injected — `diagnose-dlq` will skip these |
| `NoTelemetry` | 30% | Has `Diagnostic-Id` but no telemetry — `diagnose-dlq` finds no traces |
| `ExceptionOnly` | 12.5% | Exception telemetry only |
| `TraceOnly` | 12.5% | Trace (MessageData) only |
| `FailedDependencyOnly` | 12.5% | Failed dependency call only |
| `FullTelemetry` | 12.5% | Exception + trace + failed dependency |

This distribution tests `diagnose-dlq`'s ability to handle the full spectrum: messages with no correlation, messages with correlation but no telemetry, and messages with various combinations of telemetry types.

### Telemetry Generation

`TelemetryGenerator` sends telemetry directly to the App Insights ingestion endpoint using the `application/x-json-stream` format (newline-delimited JSON envelopes). It builds `ExceptionData`, `MessageData`, and `RemoteDependencyData` envelopes using `ai.operation.id = traceId` as the correlation key — the same key that `diagnose-dlq` uses to look up telemetry via the App Insights REST API.

The W3C `Diagnostic-Id` (`00-{traceId}-{spanId}-01`) is injected into each message's application properties before dead-lettering, establishing the correlation chain from Service Bus message → App Insights telemetry.

### Infrastructure

The `infra/test/` directory contains Bicep templates and PowerShell scripts for provisioning a test environment:

- `main.bicep` — Log Analytics workspace, App Insights, Service Bus namespace (Standard SKU), test queue with `maxDeliveryCount: 1`
- `setup.ps1` — deploys the Bicep template, assigns `Azure Service Bus Data Owner` role to the current user
- `teardown.ps1` — deletes the resource group

## Test Coverage

### New Unit Tests

| Test Class | Tests | What It Covers |
|---|---|---|
| `DlqCategoryScannerShould` | 7 | Category grouping, merge integration, empty cache, cache feeding with/without filters, completion signaling, error handling |
| `DlqScanSessionShould` | 4 | Category filtering, before-time filtering, empty results, cancellation |
| `StreamDlqCommandHandlerShould` | 3 | Session creation, cache population, no hidden filtering |
| `DumpFromCacheCommandHandlerShould` | 3 | Message serialization, empty input, file creation |
| `DiagnoseFromCacheCommandHandlerShould` | 5 | Empty input, operation ID extraction, missing operation IDs, max message limit, App Insights initialization |
| `PurgeFromCacheCommandHandlerShould` | 3 | Sequence number matching, empty input, completion |

### New Integration Tests

| Test Class | What It Covers |
|---|---|
| `StreamDlqForDumpIntegrationShould` | `StreamDlqCommand` populates cache correctly against emulator |
| `DumpFromCacheIntegrationShould` | End-to-end dump from cached messages |
| `PurgeFromCacheIntegrationShould` | End-to-end purge with sequence number matching |
| `DiagnoseFromCacheIntegrationShould` | End-to-end diagnose from cached messages |
| `MergeSimilarDumpIntegrationShould` | `--merge-similar` CLI flow for dump |
| `MergeSimilarPurgeIntegrationShould` | `--merge-similar` CLI flow for purge |
| `MergeSimilarDiagnoseIntegrationShould` | `--merge-similar` CLI flow for diagnose |

### Deleted Tests

- `AnalyzeDlqCategoriesHandlerShould` (5 tests) — replaced by `DlqCategoryScannerShould`
- `DlqMessageServiceShould.AnalyzeCategories_*` (2 tests) — functionality moved to `DlqCategoryScanner`

## Architecture After

```
Application Layer
├── DeadLetters/Common/
│   ├── DlqCategoryScanner.cs          ← shared: BuildCategorySnapshot + FeedCacheAsync
│   ├── DlqCategorySnapshot.cs         ← shared: snapshot record
│   ├── DlqScanSession.cs             ← shared: base session (cache + stream + signals)
│   └── StreamDlq.cs                   ← shared: Mediator command for dump/purge/diagnose
│
├── DeadLetters/DumpDlq/
│   └── DumpFromCache.cs               ← cache-based dump (no re-fetch)
│
├── DeadLetters/PurgeDlq/
│   └── PurgeFromCache.cs              ← cache-based purge (no re-fetch)
│
├── DeadLetters/DiagnoseDlq/
│   ├── Common/MessageDiagnostics.cs   ← extracted diagnostic logic
│   └── DiagnoseFromCache.cs           ← cache-based diagnose (no re-fetch)
│
└── DeadLetters/ResubmitDlq/
    ├── DlqResubmitSession.cs          ← subclass: adds ResubmitTracker filter
    ├── StreamDlqCategories.cs         ← resubmit-specific session creation
    └── ResubmitFromCache.cs           ← cache-based resubmit (no re-fetch)

CLI Layer
├── DeadLetters/Common/
│   ├── DlqScanSessionExtensions.cs    ← shared: RunScanningPhaseAsync + GetCategorySelection
│   └── InteractiveCategorySelection.cs ← shared: selection result record
│
├── DeadLetters/DumpDlq/
│   └── DumpDlqCommandHandler.cs       ← 3-line interactive flow
├── DeadLetters/PurgeDlq/
│   └── PurgeDlqCommandHandler.cs      ← 3-line interactive flow + confirmation
├── DeadLetters/DiagnoseDlq/
│   └── DiagnoseDlqCommandHandler.cs   ← 3-line interactive flow
└── DeadLetters/ResubmitDlq/
    └── ResubmitDlqCommandHandler.cs   ← 3-line interactive flow

Test Harness
└── ServiceBusToolset.TestHarness/
    ├── DeadLetters/GenerateDlq/
    │   ├── DeadLetterMessageFactory.cs ← 3-tier realistic data distribution
    │   ├── GenerateDlqCommandHandler.cs ← send → receive → dead-letter → telemetry
    │   ├── TelemetryGenerator.cs       ← bulk App Insights ingestion
    │   └── TelemetryProfile.cs         ← enum: NoOperationId, NoTelemetry, etc.
    └── Program.cs
```

## Key Design Decisions

| Decision | Rationale |
|---|---|
| Base class (`DlqScanSession`) + virtual `MatchesFilter` | Resubmit needs tracker-aware filtering; others don't. A virtual method keeps the base clean while allowing the single extension point. |
| Separate `StreamDlqCommand` vs `StreamDlqCategoriesCommand` | Could have used one command with an optional tracker, but two commands keeps the Mediator pipeline explicit — you know from the command type whether you're getting a plain or resubmit-aware session. |
| `*FromCache` commands take `IReadOnlyList<ServiceBusReceivedMessage>` | The cached messages are the single source of truth. No Service Bus re-fetch, no filters to re-apply, no risk of seeing different data between selection and operation. |
| `actionVerb` parameter on `GetCategorySelection` | Small but important UX detail. "Enter categories to dump" vs "Enter categories to purge" vs "Enter categories to diagnose" gives clear context. |
| `AnsiConsole.Live` replacing `Console.Clear()` | Flicker-free updates via Spectre.Console's live rendering. The old `Console.Clear()` caused visible flicker, especially on slow terminals. |
| 3-tier message factory distribution | Tier 1 tests normal categorization, Tier 2 tests `--merge-similar` on subjects, Tier 3 tests `--merge-similar` on reasons. Together they exercise the full feature matrix. |
| Direct App Insights HTTP ingestion | Avoids the `Microsoft.ApplicationInsights` SDK which buffers and batches. Direct HTTP gives control over exact envelope structure and immediate visibility in the portal. |
