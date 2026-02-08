# Integration Test Strategy

## Goal

Verify full user-facing feature paths end-to-end — real DI, real Mediator routing, and real Azure Service Bus operations — without replicating the granularity of the 170+ unit tests that already cover the Application layer with mocked `IServiceBusClientFactory`.

## Test Infrastructure

### Project Structure and NuGet Dependencies

```
tests/ServiceBusToolset.Integration.Tests/
├── ServiceBusToolset.Integration.Tests.csproj
├── Properties/
│   └── AssemblyInfo.cs                    # [assembly: AssemblyFixture]
├── Infrastructure/
│   ├── ServiceBusEmulatorFixture.cs       # Testcontainers lifecycle
│   ├── EmulatorServiceBusClientFactory.cs # IServiceBusClientFactory for emulator
│   └── BaseIntegrationTest.cs            # Per-test DI, entity isolation, cleanup
├── DeadLetters/
│   ├── DumpDlqIntegrationShould.cs
│   ├── CountDlqIntegrationShould.cs
│   ├── AnalyzeDlqIntegrationShould.cs
│   ├── PurgeDlqIntegrationShould.cs
│   ├── ResubmitDlqIntegrationShould.cs
│   └── DiagnoseDlqIntegrationShould.cs
└── Monitoring/
    ├── MonitorQueuesIntegrationShould.cs
    └── MonitorSubscriptionsIntegrationShould.cs
```

**NuGet packages:**

```xml
<ItemGroup>
    <PackageReference Include="Testcontainers.ServiceBus" Version="..." />
    <PackageReference Include="xunit.v3" Version="..." />
    <PackageReference Include="xunit.runner.visualstudio" Version="..." />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="NSubstitute" Version="5.3.0" />
    <PackageReference Include="Shouldly" Version="4.2.1" />
    <PackageReference Include="System.Reactive" Version="6.0.1" />
</ItemGroup>

<ItemGroup>
    <ProjectReference Include="..\..\src\ServiceBusToolset.Application\ServiceBusToolset.Application.csproj" />
</ItemGroup>
```

Note the project references only the Application layer — integration tests exercise the Mediator pipeline directly via `ISender`, bypassing the CLI layer entirely.

### Assembly Fixture: ServiceBusEmulatorFixture

**Why `AssemblyFixture` instead of `ICollectionFixture`?**

The Azure Service Bus emulator is expensive to start — it pulls a Docker image, spins up a SQL Server dependency, and initializes the AMQP broker, taking roughly 15–20 seconds. Starting one per test class or per collection is wasteful. xUnit v2's `ICollectionFixture<T>` solves the "one instance per run" problem, but it forces every test class into a single named collection, which disables xUnit's default parallel-by-class execution. xUnit v3's `AssemblyFixture` attribute provides the same single-instance lifecycle without the collection constraint — all test classes share the fixture *and* run in parallel by default.

```
Properties/AssemblyInfo.cs
```

```csharp
using ServiceBusToolset.Integration.Tests.Infrastructure;

[assembly: AssemblyFixture(typeof(ServiceBusEmulatorFixture))]
```

```csharp
using Testcontainers.ServiceBus;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    private readonly ServiceBusContainer _container = new ServiceBusBuilder()
        .WithAcceptLicenseAgreement(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
```

**Thread safety:** The fixture is effectively read-only after `InitializeAsync` completes — it only exposes a connection string. All entity creation and message operations happen in `BaseIntegrationTest` on a per-test basis, so the fixture itself requires no synchronization.

### Test Implementation: EmulatorServiceBusClientFactory

**The design problem:** The production `ServiceBusClientFactory` (in the CLI layer) creates clients using `DefaultAzureCredential` with a fully qualified namespace:

```csharp
// Production (CLI layer)
public class ServiceBusClientFactory : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
        => new(fullyQualifiedNamespace, new DefaultAzureCredential());

    public ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace)
        => new(fullyQualifiedNamespace, new DefaultAzureCredential());
}
```

The emulator uses connection strings, not Azure AD credentials. The `IServiceBusClientFactory` interface accepts `string fullyQualifiedNamespace` — but the test implementation simply ignores this parameter and uses the emulator's connection string instead:

```csharp
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class EmulatorServiceBusClientFactory(string connectionString) : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
        => new(connectionString);

    public ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace)
        => new(connectionString);
}
```

This is the *only* seam needed. The entire Application layer — Mediator pipeline, `DlqMessageService`, every command handler — works with real `ServiceBusClient` and `ServiceBusAdministrationClient` instances against the emulator. No code changes, no conditional logic, no test-specific branches. The difference between unit tests (which mock `IServiceBusClientFactory` to return NSubstitute fakes) and integration tests (which provide a real factory pointing at the emulator) is this single class.

### Base Test Class: BaseIntegrationTest

This is the largest piece of infrastructure. It provides per-test entity isolation, a real DI container, entity lifecycle helpers, and deterministic cleanup.

#### Test ID and Entity Isolation

Every test instance generates an 8-character hex identifier from a GUID. Helper methods append this ID to entity names, guaranteeing that parallel tests never collide — even when they use the same logical name like `"orders"`:

```csharp
public abstract class BaseIntegrationTest : IAsyncDisposable
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private readonly ServiceProvider _serviceProvider;

    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    private readonly List<string> _createdQueues = [];
    private readonly List<(string Topic, string Subscription)> _createdSubscriptions = [];
    private readonly List<string> _createdTopics = [];
    private readonly List<string> _tempFiles = [];

    protected string QueueName(string baseName) => $"{baseName}-{TestId}";
    protected string TopicName(string baseName) => $"{baseName}-{TestId}";
    protected string SubscriptionName(string baseName) => $"{baseName}-{TestId}";
```

A test calling `QueueName("orders")` gets `"orders-a1b2c3d4"` — unique to that test instance. The tracking lists (`_createdQueues`, `_createdTopics`, `_createdSubscriptions`) record every entity for cleanup in `DisposeAsync`.

#### DI Container Setup

Each test instance builds a fresh `ServiceCollection`. This mirrors how the production `Program.cs` wires up the Application layer, but replaces the CLI-layer `ServiceBusClientFactory` with the emulator-backed one:

```csharp
    protected BaseIntegrationTest(
        ServiceBusEmulatorFixture fixture,
        Action<IServiceCollection>? configureServices = null)
    {
        _fixture = fixture;

        var services = new ServiceCollection();

        // 1. Register the full Application layer — Mediator pipeline, DlqMessageService, IAppInsightsService
        services.AddApplication();

        // 2. Replace the client factory with the emulator-backed implementation
        services.AddSingleton<IServiceBusClientFactory>(
            new EmulatorServiceBusClientFactory(fixture.ConnectionString));

        // 3. Per-class overrides (e.g., mocking IAppInsightsService for DiagnoseDlq tests)
        configureServices?.Invoke(services);

        // 4. Build the container
        _serviceProvider = services.BuildServiceProvider();
    }
```

The `configureServices` delegate is the extensibility point — test classes that need to override specific registrations (like `DiagnoseDlqIntegrationShould` replacing `IAppInsightsService` with an NSubstitute mock) pass a lambda to the base constructor.

Tests dispatch commands through Mediator by creating a scope and resolving `ISender`:

```csharp
    protected ISender CreateSender()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISender>();
    }
```

**Why a single scope, not the AAA triple-scope pattern?** In database-backed integration tests (e.g., EF Core), creating separate scopes for Arrange/Act/Assert is critical — EF's `DbContext` caches entities per scope, so a single scope would make assertions pass vacuously by reading cached state rather than querying the database. Service Bus operations are stateless across scope boundaries. Sending, receiving, and peeking messages all go through AMQP to the broker with no client-side caching. A single scope per test is simpler and sufficient.

#### Entity Lifecycle Helpers

The base class provides methods to create Service Bus entities and populate them with messages:

```csharp
    protected async Task CreateQueueAsync(string queueName)
    {
        var adminClient = new ServiceBusAdministrationClient(_fixture.ConnectionString);
        await adminClient.CreateQueueAsync(queueName);
        _createdQueues.Add(queueName);
    }

    protected async Task CreateTopicAsync(string topicName)
    {
        var adminClient = new ServiceBusAdministrationClient(_fixture.ConnectionString);
        await adminClient.CreateTopicAsync(topicName);
        _createdTopics.Add(topicName);
    }

    protected async Task CreateSubscriptionAsync(string topicName, string subscriptionName)
    {
        var adminClient = new ServiceBusAdministrationClient(_fixture.ConnectionString);
        await adminClient.CreateSubscriptionAsync(topicName, subscriptionName);
        _createdSubscriptions.Add((topicName, subscriptionName));
    }
```

**Dead-lettering messages** is the most important helper. There is no "send directly to DLQ" API in Azure Service Bus — the only way to populate the dead-letter sub-queue is to send a message to the main entity, receive it, and explicitly dead-letter it:

```csharp
    protected async Task DeadLetterMessageAsync(
        EntityTarget target,
        ServiceBusMessage message,
        string reason = "TestReason",
        string description = "Integration test dead-letter")
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);

        // 1. Send the message to the queue or subscription
        var senderEntity = target.IsQueueMode ? target.Queue! : target.Topic!;
        await using var sender = client.CreateSender(senderEntity);
        await sender.SendMessageAsync(message);

        // 2. Receive the message from the queue or subscription
        await using var receiver = target.IsQueueMode
            ? client.CreateReceiver(target.Queue!)
            : client.CreateReceiver(target.Topic!, target.Subscription!);

        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));

        // 3. Dead-letter the received message
        await receiver.DeadLetterMessageAsync(received, reason, description);
    }
```

For Monitor tests, a simpler helper populates active messages without dead-lettering:

```csharp
    protected async Task PopulateActiveMessagesAsync(
        string queueName,
        IEnumerable<ServiceBusMessage> messages)
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender(queueName);

        foreach (var message in messages)
            await sender.SendMessageAsync(message);
    }
```

#### Cleanup in DisposeAsync

Every tracked entity is deleted in reverse dependency order — subscriptions first (they depend on topics), then topics, then queues. Temp files are cleaned up last:

```csharp
    public async ValueTask DisposeAsync()
    {
        var adminClient = new ServiceBusAdministrationClient(_fixture.ConnectionString);

        // Subscriptions must be deleted before their parent topics
        foreach (var (topic, subscription) in _createdSubscriptions)
        {
            try { await adminClient.DeleteSubscriptionAsync(topic, subscription); }
            catch { /* entity may already be gone */ }
        }

        foreach (var topic in _createdTopics)
        {
            try { await adminClient.DeleteTopicAsync(topic); }
            catch { /* entity may already be gone */ }
        }

        foreach (var queue in _createdQueues)
        {
            try { await adminClient.DeleteQueueAsync(queue); }
            catch { /* entity may already be gone */ }
        }

        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); }
            catch { /* best effort */ }
        }

        _serviceProvider.Dispose();
    }
}
```

The `try/catch` blocks are intentional — a failing test may leave entities in an unexpected state, and cleanup should not mask the actual test failure with a secondary exception.

### File I/O (DumpDlq)

`DumpDlqMessagesCommand` requires an `OutputFilePath`. The base class provides a helper that generates a unique temp path and registers it for cleanup:

```csharp
    protected string TempFilePath(string extension = ".json")
    {
        var path = Path.Combine(Path.GetTempPath(), $"integration-{TestId}-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }
```

Tests use this to avoid filesystem collisions and ensure no temp files survive the test run.

### Observable Testing (Monitor features)

Monitor commands return `IObservable<IReadOnlyList<T>>` — a push-based stream of statistics snapshots. Integration tests need to capture the first meaningful emission and assert against it.

The test ID suffix enables precise filtering. Each test creates entities with names ending in `-{TestId}`, and passes a wildcard filter scoped to that suffix:

```csharp
[Fact]
public async Task EmitQueueStatistics_WhenQueuesExist()
{
    // Arrange
    var q1 = QueueName("orders");
    var q2 = QueueName("payments");
    await CreateQueueAsync(q1);
    await CreateQueueAsync(q2);
    await PopulateActiveMessagesAsync(q1, [new ServiceBusMessage("msg1"), new ServiceBusMessage("msg2")]);

    using var cts = new CancellationTokenSource();
    var sender = CreateSender();

    // Act
    var result = await sender.Send(new MonitorQueuesCommand(
        FullyQualifiedNamespace: "ignored-by-emulator",
        QueueFilter: $"*-{TestId}",
        RefreshInterval: TimeSpan.FromSeconds(1),
        CancellationToken: cts.Token));

    var statistics = await result.Value.QueueStatistics.FirstAsync();
    cts.Cancel();

    // Assert
    statistics.Count.ShouldBe(2);
    statistics.ShouldContain(s => s.Name == q1 && s.ActiveMessageCount == 2);
    statistics.ShouldContain(s => s.Name == q2 && s.ActiveMessageCount == 0);
}
```

The key patterns:
- `QueueFilter: $"*-{TestId}"` ensures the observable only sees this test's entities, even though the emulator contains entities from all parallel tests
- `System.Reactive`'s `FirstAsync()` captures the first emission as a `Task<T>`, avoiding manual `TaskCompletionSource` wiring
- `CancellationTokenSource` is cancelled after assertion to terminate the observable's polling loop

### IAppInsightsService Override for DiagnoseDlq

`DiagnoseDlqMessagesCommand` is the only feature that depends on `IAppInsightsService`, which calls the Azure Monitor Logs API. There is no emulator for App Insights, so this dependency must be mocked. The `configureServices` delegate on the base class constructor makes this a one-liner:

```csharp
public class DiagnoseDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture, services =>
    {
        services.AddSingleton(Substitute.For<IAppInsightsService>());
    })
{
    // Tests can resolve the mock to configure returns:
    // var mockAppInsights = _serviceProvider.GetRequiredService<IAppInsightsService>();
}
```

**How DI override order works:** `AddApplication()` registers `IAppInsightsService` as scoped (via `services.AddScoped<IAppInsightsService, AppInsightsService>()`). The `configureServices` delegate runs *after* `AddApplication()` and registers an NSubstitute singleton for the same interface. Microsoft's DI container resolves the *last* registration for a given service type, so the mock wins. The real `AppInsightsService` (which requires Azure credentials) is never instantiated.

### Complete Test Example

Putting it all together — here is a full end-to-end test for `PurgeDlqIntegrationShould`:

```csharp
using Azure.Messaging.ServiceBus;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.Integration.Tests.Infrastructure;
using Shouldly;

namespace ServiceBusToolset.Integration.Tests.DeadLetters;

public class PurgeDlqIntegrationShould(ServiceBusEmulatorFixture fixture)
    : BaseIntegrationTest(fixture)
{
    [Fact]
    public async Task RemoveAllMessages_WhenNoFiltersProvided()
    {
        // Arrange
        var queue = QueueName("purge-all");
        await CreateQueueAsync(queue);

        var target = EntityTarget.ForQueue(queue);
        for (var i = 0; i < 5; i++)
        {
            await DeadLetterMessageAsync(
                target,
                new ServiceBusMessage($"message-{i}") { Subject = "Order.Failed" },
                reason: "MaxDeliveryCountExceeded");
        }

        var sender = CreateSender();

        // Act
        var result = await sender.Send(new PurgeDlqMessagesCommand(
            FullyQualifiedNamespace: "ignored-by-emulator",
            Target: target));

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.PurgedCount.ShouldBe(5);
        result.Value.SkippedCount.ShouldBe(0);

        // Verify the DLQ is actually empty
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var receiver = client.CreateReceiver(queue,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var remaining = await receiver.PeekMessageAsync();
        remaining.ShouldBeNull();
    }
}
```

This test exercises the full stack: `ISender` dispatches the command through Mediator's source-generated pipeline to `PurgeDlqMessagesCommandHandler`, which resolves `IServiceBusClientFactory` from DI, creates a real `ServiceBusClient` connected to the emulator, and receives-and-deletes all dead-lettered messages over AMQP. The assertion confirms the handler's return value *and* independently verifies the broker state by peeking the DLQ directly.

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
