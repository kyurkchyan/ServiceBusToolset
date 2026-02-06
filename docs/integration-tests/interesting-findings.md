# Interesting Findings: Service Bus Emulator Behaviors & Testing Approaches

Lessons learned while building integration tests against the Azure Service Bus Emulator (v2.0.0) with Testcontainers.

## Emulator Connection Quota

**Problem:** The emulator enforces a low connection quota for AMQP connections. When xUnit runs test classes in
parallel (8 classes), each creating its own `ServiceBusClient`, the quota is quickly exhausted:

```
Azure.Messaging.ServiceBus.ServiceBusException:
ConnectionsQuotaExceeded for namespace sbemulatorns
```

**Root cause:** `ServiceBusClient` opens persistent AMQP connections. With 8 test classes in parallel, plus additional
clients created by command handlers via `IServiceBusClientFactory`, the total connection count exceeds the emulator's
limit.

**Solution:** Share a single `ServiceBusClient` from the assembly-scoped `ServiceBusEmulatorFixture`. All test helper
methods (dead-lettering, populating messages) use `_fixture.Client` instead of creating per-test instances.
`ServiceBusClient` is thread-safe and multiplexes operations over its connection pool.

**Key insight:** Senders and receivers (lightweight AMQP links) are still created and disposed per-operation — only the
underlying client/connection is shared.

## Dual Connection Strings (AMQP vs HTTP)

**Problem:** `ServiceBusAdministrationClient` (create/delete queues, get runtime properties) uses HTTP, not AMQP. The
`GetConnectionString()` from Testcontainers only returns the AMQP endpoint (port 5672). Using it with the admin client
results in `Connection refused (127.0.0.1:443)`.

**Solution:** Build a separate connection string for the admin client using the emulator's management port (5300):

```csharp
public string AdministrationConnectionString =>
    $"Endpoint=sb://{_container.Hostname}:{_container.GetMappedPublicPort(5300)};"
    + "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
```

The `EmulatorServiceBusClientFactory` accepts both connection strings and routes `CreateClient()` to AMQP and
`CreateAdministrationClient()` to HTTP.

**Requires:** Azure.Messaging.ServiceBus >= 7.20.1 (earlier versions stripped the custom port from the connection
string).

## Emulator Image Version Matters

**Problem:** The emulator Docker image `latest` tag pointed to v1.x, which does NOT support the admin/management API.
Calls to `CreateQueueAsync()` via `ServiceBusAdministrationClient` returned `ResponseEnded` errors.

**Solution:** Pin to `servicebus-emulator:2.0.0` (released Jan 2026), which added admin client support. Never use
`:latest` for the emulator image — always pin to a specific version.

## Runtime Properties Not Tracked by Emulator

**Problem:** `GetQueueRuntimePropertiesAsync` / `GetSubscriptionRuntimePropertiesAsync` return **all zeros** for every
message count property (`ActiveMessageCount`, `DeadLetterMessageCount`, `TotalMessageCount`, `ScheduledMessageCount`,
etc.). This is not eventual consistency — the counts are permanently zero regardless of how long you wait.

**Verified by diagnostic test:** After dead-lettering 3 messages, AMQP peeking correctly returns 3, while the admin API
returns 0 for all counts — both immediately and after a 2-second delay.

**Root cause:** The emulator v2.0.0 admin client support (released Jan 2026) covers entity CRUD operations
(create/delete/update queues, topics, subscriptions) but does **not** implement runtime property tracking. The official
[admin client announcement](https://techcommunity.microsoft.com/blog/messagingonazureblog/introducing-administration-client-support-for-the-azure-service-bus-emulator/4486433)
only mentions "creating, updating, and deleting entities" — no mention of runtime properties or message counts. The
official .NET sample code also only demonstrates CRUD operations.

**Impact on tests:**

- `CountDlqMessagesCommand` without a filter uses `GetQueueRuntimePropertiesAsync` (the "fast path") — untestable.
- `MonitorQueuesCommand` / `MonitorSubscriptionsCommand` stream runtime properties — message counts untestable.
- `WaitForDlqCountAsync` originally used the admin API and silently returned without reaching the expected count.

**Solution:** Use AMQP peeking instead of admin runtime properties wherever possible:

```csharp
// WaitForDlqCountAsync — peek DLQ messages via AMQP instead of admin API
var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
var messages = await receiver.PeekMessagesAsync(expectedCount, cancellationToken: ct);
if (messages.Count >= expectedCount) return;
```

For count tests, provide a far-future `BeforeTime` to exercise the AMQP peeking code path (which counts messages by
peeking) instead of the admin API fast path:

```csharp
var beforeTime = DateTimeOffset.UtcNow.AddHours(1);
var result = await sender.Send(new CountDlqMessagesCommand(ns, target, beforeTime), ct);
// TotalCount and FilteredCount are both accurate via AMQP peeking
```

For monitor tests, verify entity discovery by name but skip message count assertions.

## Abandoned DLQ Messages Reappear

**Problem:** When a receiver abandons a DLQ message, it returns to the DLQ and is available for subsequent receive
calls. This is expected Azure Service Bus behavior but is invisible in unit tests where receivers are mocked.

**Impact:** This behavior exposed a critical infinite loop bug in filtered purge/resubmit handlers (see
`integration_test_findings.md`). It also means that `SkippedCount` must track unique messages by sequence number, not
simply increment a counter per abandon operation.

**Key insight:** Any loop that receives, filters, and abandons DLQ messages MUST define "progress" as "messages matched
the filter" rather than "messages were received." Otherwise, the same non-matching messages cycle endlessly.
