# Integration Test Findings

## Finding #1: Infinite Loop in Filtered Purge & Resubmit (Critical)

**Severity:** Critical — hangs indefinitely in production, consuming resources and never completing.

**Discovered by:** Integration test `PurgeDlqIntegrationShould.RemoveOnlyMatchingMessages_WhenCategoryAndTimeFiltersProvided`

**Affected handlers:**
- `PurgeDlqMessagesCommandHandler.PurgeWithFilterAsync`
- `ResubmitDlqMessagesCommandHandler.ResubmitWithFilterAsync`

### Description

Both filtered handlers use a `while` loop that continues receiving batches from the DLQ until `emptyBatches` reaches a threshold of 3. The original code unconditionally reset `emptyBatches = 0` whenever a non-empty batch was received:

```csharp
if (messages.Count == 0)
{
    emptyBatches++;
    continue;
}

emptyBatches = 0;  // BUG: always resets, even if no messages matched the filter
```

When a filter is applied and some messages don't match, those messages are **abandoned** back to the DLQ. On the next iteration, the receiver picks them up again. Because the batch is never empty (the same non-matching messages keep returning), `emptyBatches` never reaches the threshold and the loop runs forever.

### Root cause

The `emptyBatches` counter was tracking "did we receive any messages?" when it should have been tracking "did we make any progress?" A batch where every message is abandoned represents zero progress and should be treated the same as an empty batch.

### Fix

Two changes were required:

**1. Termination:** Reset `emptyBatches` only when at least one message was actually processed (completed/resubmitted). When no messages matched the filter, increment `emptyBatches` instead:

```csharp
if (toComplete.Count > 0)
{
    emptyBatches = 0;
}
else
{
    emptyBatches++;
}
```

**2. Accurate skip count:** Replace the `totalSkipped` counter with a `HashSet<long>` tracking sequence numbers. Since abandoned messages are re-received across multiple iterations, a simple counter would count the same message multiple times (e.g., 2 non-matching messages × 4 iterations = 8 reported skipped, when only 2 were actually skipped):

```csharp
var skippedSequenceNumbers = new HashSet<long>();
// ...
foreach (var m in toAbandon)
{
    skippedSequenceNumbers.Add(m.SequenceNumber);
}
// Result uses skippedSequenceNumbers.Count
```

### Why this matters

This bug would cause the CLI to hang indefinitely whenever a user runs `purge-dlq` or `resubmit-dlq` with a category or time filter on a DLQ that contains messages not matching the filter. The only escape would be forcefully terminating the process.

The bug was invisible to unit tests because they mock the `ServiceBusReceiver` and control what messages are returned. In unit tests, the receiver returns a pre-determined sequence and eventually returns an empty batch, so the loop terminates. Only a real Service Bus receiver exhibits the behavior where abandoned messages reappear in subsequent receive calls.

### Value of integration testing

This finding demonstrates precisely why integration tests against a real (emulated) Service Bus are essential:

1. **Real message lifecycle** — Unit tests cannot replicate the fact that abandoned DLQ messages return to the queue and are re-received.
2. **Behavioral fidelity** — The emulator faithfully reproduces the abandon-and-reappear behavior of Azure Service Bus, exposing the infinite loop.
3. **Timeout as a signal** — The test hung instead of completing, making the bug immediately obvious. A unit test with mocked receivers would have passed silently.

This single finding alone justifies the investment in the integration test infrastructure.
