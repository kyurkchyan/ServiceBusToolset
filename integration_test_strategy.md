# Integration Test Strategy

## Goal

Verify full user-facing feature paths end-to-end — real DI, real Mediator routing, and real Azure Service Bus operations — without replicating the granularity of the 170+ unit tests that already cover the Application layer with mocked `IServiceBusClientFactory`.

## Test Infrastructure

### Azure Service Bus Emulator via Testcontainers

- Shared xUnit **collection fixture** starts the Azure Service Bus Emulator container once per test run
- Fixture exposes a connection string (or `FullyQualifiedNamespace`) and provides helper methods to:
  - Create/delete queues, topics, and subscriptions on demand
  - **Dead-letter a message**: send a message to a queue/subscription, receive it, then call `DeadLetterMessageAsync` — this is the only way to populate the DLQ in the emulator
  - Peek or receive messages from a queue/subscription for post-test assertions
- Each test class gets a unique queue/topic name prefix to avoid cross-test interference

### DI Container

- Real `AddApplication()` registration (Mediator source-generated pipeline, `DlqMessageService`, etc.)
- Real `IServiceBusClientFactory` pointing to the emulator endpoint
- **`IAppInsightsService`: mocked** (NSubstitute) — no App Insights emulator exists; only affects `DiagnoseDlq`
- Resolve `ISender` from the container to dispatch commands, exactly as the CLI layer does

### File I/O (DumpDlq)

- Use `Path.GetTempFileName()` for output files
- Clean up in `IAsyncDisposable` / `finally`

### Observable Testing (Monitor features)

- Subscribe to the `IObservable<IReadOnlyList<T>>` returned by Monitor commands
- Capture the **first emission** via `TaskCompletionSource` or Reactive `FirstAsync()`
- Cancel via `CancellationTokenSource` after assertion

## Test Classes and Methods

### 1. `DumpDlqIntegrationShould` — 3 tests (Queue target)

| Method | Description |
|---|---|
| `DumpAllMessages_WhenDlqContainsMessages` | Dead-letter 3 messages with known subjects/bodies. Dispatch `DumpDlqMessagesCommand` with no filters. Verify the JSON output file contains exactly 3 messages with correct `Subject`, `Body`, `DeadLetterReason`, and `EnqueuedTime` properties. |
| `DumpFilteredMessages_WhenCategoryAndTimeFiltersProvided` | Dead-letter 4 messages: 2 with category `("OrderFailed", "MaxRetriesExceeded")` and 2 with category `("PaymentError", "Expired")`, staggered in time. Apply both `CategoryFilter` and `BeforeTime`. Verify only matching messages appear in file; non-matching messages remain in DLQ. |
| `ReturnZeroCount_WhenDlqIsEmpty` | Empty DLQ. Dispatch command. Verify `Result.IsSuccess` with `MessageCount == 0` and output file is empty or contains an empty JSON array. |

### 2. `CountDlqIntegrationShould` — 3 tests (Queue + Subscription)

| Method | Description |
|---|---|
| `ReturnTotalCount_WhenNoFilterProvided` | Dead-letter 5 messages. Dispatch `CountDlqMessagesCommand` with `BeforeTime = null`. Verify `TotalCount == 5` and `FilteredCount == null` (admin API fast path, no peek). |
| `ReturnFilteredCount_WhenTimeFilterProvided` | Dead-letter 4 messages: 2 enqueued "early" and 2 "recently". Dispatch with `BeforeTime` between the two batches. Verify `TotalCount == 4` and `FilteredCount == 2` (peek-based slow path). |
| `ReturnCount_WhenSubscriptionTargetProvided` | Dead-letter 3 messages in a topic subscription's DLQ. Dispatch with `EntityTarget.ForSubscription(topic, subscription)`. Verify `TotalCount == 3`. |

### 3. `AnalyzeDlqIntegrationShould` — 3 tests (Queue + Subscription)

| Method | Description |
|---|---|
| `GroupAndSortCategories_WhenDlqContainsMessages` | Dead-letter 5 messages across 2 categories (3 in one, 2 in another). Dispatch `AnalyzeDlqCategoriesCommand`. Verify `Categories` has 2 entries, sorted descending by `Count`, and `TotalMessageCount == 5`. |
| `ReturnEmptyCategories_WhenDlqIsEmpty` | Empty DLQ. Verify `Categories` is empty and `TotalMessageCount == 0`. |
| `AnalyzeCategories_WhenSubscriptionTargetProvided` | Dead-letter messages with mixed categories in a subscription DLQ. Verify grouping works identically via `EntityTarget.ForSubscription`. |

### 4. `PurgeDlqIntegrationShould` — 3 tests (Queue target)

| Method | Description |
|---|---|
| `RemoveAllMessages_WhenNoFiltersProvided` | Dead-letter 5 messages. Dispatch `PurgeDlqMessagesCommand` with no filters. Verify `PurgedCount == 5`, `SkippedCount == 0`, and DLQ peek returns 0 messages (ReceiveAndDelete path). |
| `RemoveOnlyMatchingMessages_WhenCategoryAndTimeFiltersProvided` | Dead-letter 4 messages with varied categories and times. Apply both `CategoryFilter` and `BeforeTime`. Verify selective purge: `PurgedCount` matches filter, remaining messages still in DLQ (PeekLock path). |
| `ReturnZeroPurged_WhenDlqIsEmpty` | Empty DLQ. Verify `PurgedCount == 0` and `SkippedCount == 0`. |

### 5. `ResubmitDlqIntegrationShould` — 4 tests (Queue target)

| Method | Description |
|---|---|
| `MoveAllMessagesToTargetQueue_WhenNoFiltersProvided` | Dead-letter 3 messages with rich application properties, custom headers, and varied subjects. Dispatch `ResubmitDlqMessagesCommand` targeting a separate queue. Verify all messages arrive in target queue with properties preserved through the real AMQP roundtrip, and source DLQ is empty. |
| `PreserveMessageBodyFidelity_WhenResubmitting` | Dead-letter 3 messages with different body types: JSON object, plain text string, and binary payload. Resubmit and verify exact body bytes are preserved in the target queue. |
| `MoveOnlyMatchingMessages_WhenCategoryAndTimeFiltersProvided` | Dead-letter 3 messages with mixed categories and times. Apply both filters. Verify only matching messages land in target queue; non-matching remain in source DLQ. |
| `ReturnZeroResubmitted_WhenDlqIsEmpty` | Empty DLQ. Verify `ResubmittedCount == 0` and `SkippedCount == 0`. |

### 6. `DiagnoseDlqIntegrationShould` — 3 tests (Queue target, mock `IAppInsightsService`)

| Method | Description |
|---|---|
| `DiagnoseMessages_WhenDlqContainsTracedMessages` | Dead-letter 3 messages with `Diagnostic-Id` application property containing W3C trace IDs. Mock `IAppInsightsService` to return telemetry for those operation IDs. Verify `Results` are enriched with real message metadata (Subject, DeadLetterReason) combined with mocked telemetry, and `TotalProcessed == 3`. |
| `SkipMessagesWithoutOperationId_WhenMixed` | Dead-letter 2 messages: 1 with `Diagnostic-Id` and 1 without. Verify `SkippedNoOperationId == 1` and only the traced message appears in `Results`. |
| `ApplyFilters_WhenCategoryAndTimeFiltersProvided` | Dead-letter 4 traced messages with varied categories and times. Apply both `CategoryFilter` and `BeforeTime`. Verify only matching messages are diagnosed; `TotalProcessed` reflects filtered count. |

### 7. `MonitorQueuesIntegrationShould` — 3 tests

| Method | Description |
|---|---|
| `EmitQueueStatistics_WhenQueuesExist` | Create 3 queues with known message counts (some active, some DLQ). Dispatch `MonitorQueuesCommand`. Capture the first `IObservable` emission. Verify it contains statistics for all 3 queues with correct `ActiveMessageCount` and `DeadLetterMessageCount`. |
| `FilterQueues_WhenWildcardFilterProvided` | Create 3 queues: `orders-queue`, `orders-retry`, `payments-queue`. Dispatch with `QueueFilter = "orders*"`. Verify first emission contains only the 2 matching queues. |
| `EmitEmptyList_WhenNoQueuesMatchFilter` | Dispatch with a filter that matches no existing queues. Verify first emission is an empty list. |

### 8. `MonitorSubscriptionsIntegrationShould` — 3 tests

| Method | Description |
|---|---|
| `EmitSubscriptionStatistics_WhenSubscriptionsExist` | Create 1 topic with 2 subscriptions, each with known message counts. Dispatch `MonitorSubscriptionsCommand`. Capture first emission. Verify correct `ActiveMessageCount` and `DeadLetterMessageCount` per subscription. |
| `FilterByTopicAndSubscription_WhenDualFiltersProvided` | Create 2 topics x 2 subscriptions each. Dispatch with `TopicFilter = "orders*"` and `SubscriptionFilter = "sub-1"`. Verify single matching subscription in first emission. |
| `EmitEmptyList_WhenNoSubscriptionsMatchFilter` | Dispatch with filters that match nothing. Verify first emission is an empty list. |

## Queue vs. Subscription Coverage Strategy

Not every feature needs both queue and subscription integration tests. The code divergence between the two paths is minimal — it's a single `ReceiverFactory` branch (`CreateReceiver(queue)` vs `CreateReceiver(topic, subscription)`).

| Path | Tested By |
|---|---|
| **Subscription DLQ path** | `CountDlqIntegrationShould`, `AnalyzeDlqIntegrationShould`, `MonitorSubscriptionsIntegrationShould` |
| **Queue DLQ path** | `DumpDlqIntegrationShould`, `PurgeDlqIntegrationShould`, `ResubmitDlqIntegrationShould`, `DiagnoseDlqIntegrationShould`, `MonitorQueuesIntegrationShould` |

Three features explicitly test the subscription path. This provides sufficient confidence that `ReceiverFactory` routing works correctly without duplicating every feature for both entity types.

## Summary

| Feature | Test Class | Tests | Entity Target |
|---|---|---|---|
| DumpDlq | `DumpDlqIntegrationShould` | 3 | Queue |
| CountDlq | `CountDlqIntegrationShould` | 3 | Queue + Subscription |
| AnalyzeDlq | `AnalyzeDlqIntegrationShould` | 3 | Queue + Subscription |
| PurgeDlq | `PurgeDlqIntegrationShould` | 3 | Queue |
| ResubmitDlq | `ResubmitDlqIntegrationShould` | 4 | Queue |
| DiagnoseDlq | `DiagnoseDlqIntegrationShould` | 3 | Queue (mock AppInsights) |
| MonitorQueues | `MonitorQueuesIntegrationShould` | 3 | Queue |
| MonitorSubscriptions | `MonitorSubscriptionsIntegrationShould` | 3 | Subscription |
| **Total** | **8 classes** | **25 tests** | |

## Naming Conventions

All names follow the project conventions defined in `CLAUDE.md`:

- **Test classes**: `[ClassName]Should` suffix (e.g., `DumpDlqIntegrationShould`)
- **Test methods**: `[Action]_When[Condition]` (e.g., `DumpAllMessages_WhenDlqContainsMessages`)
- **Assertions**: Shouldly library
- **Mocking**: NSubstitute (only for `IAppInsightsService` in DiagnoseDlq tests)
