# purge-dlq

Purge messages from a dead letter queue.

## Synopsis

```bash
dotnet run -- purge-dlq -n <namespace> (-q <queue> | -t <topic> -s <subscription>) [options]
```

## Options

| Option | Short | Description |
|--------|-------|-------------|
| `--namespace` | `-n` | **(Required)** Fully qualified Service Bus namespace |
| `--queue` | `-q` | Queue name |
| `--topic` | `-t` | Topic name (requires `--subscription`) |
| `--subscription` | `-s` | Subscription name (requires `--topic`) |
| `--before` | | Only purge messages enqueued before this UTC datetime (ISO 8601) |
| `--dry-run` | | Preview message count without purging |
| `--interactive` | `-i` | Interactive mode: view and select categories to purge |
| `--verbose` | `-v` | Enable verbose output |

## Examples

### Purge All Messages

```bash
# From a queue DLQ
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue

# From a topic subscription DLQ
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -t mytopic -s mysub
```

### Dry Run

Preview message count without purging:

```bash
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue --dry-run
```

```
[DRY RUN] Found 1,523 messages in DLQ for queue 'myqueue'
```

### Time-Based Filtering

Purge only messages enqueued before a specific date:

```bash
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue --before 2024-01-01T00:00:00Z
```

### Interactive Mode

View messages grouped by Label and DeadLetterReason, then select which to purge:

```bash
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue -i
```

```
Analyzing DLQ for queue 'myqueue'...
Peeked 1,523 messages...

Dead Letter Summary:
╭───┬─────────────────────┬────────────────────────────────┬───────╮
│ # │ Label               │ DeadLetterReason               │ Count │
├───┼─────────────────────┼────────────────────────────────┼───────┤
│ 1 │ OrderCreated        │ MaxDeliveryCountExceeded       │   847 │
│ 2 │ PaymentProcessed    │ MaxDeliveryCountExceeded       │   412 │
│ 3 │ (none)              │ TTLExpiredException            │   198 │
│ 4 │ OrderCreated        │ DeadLetterReasonHeader         │    66 │
╰───┴─────────────────────┴────────────────────────────────┴───────╯
Total: 1,523 messages

Select categories to purge (comma-separated numbers, 'all', or 'q' to quit): 1,3
Purging 1,045 messages from 2 categories...
Purged 1,045 messages from DLQ for queue 'myqueue'.
```

**Selection options:**

| Input | Action |
|-------|--------|
| `1,3,5` | Select specific categories |
| `1-5` | Select a range |
| `all` / `a` | Select all categories |
| `q` / empty | Quit without purging |

## Required Permissions

The authenticated identity needs one of these Azure RBAC roles:

- **Azure Service Bus Data Owner**
- **Azure Service Bus Data Receiver**
