# resubmit-dlq

Resubmit messages from a dead letter queue back to the main queue or topic.

## Synopsis

```bash
dotnet run -- resubmit-dlq -n <namespace> (-q <queue> | -t <topic> -s <subscription>) [options]
```

## Options

| Option | Short | Description |
|--------|-------|-------------|
| `--namespace` | `-n` | **(Required)** Fully qualified Service Bus namespace |
| `--queue` | `-q` | Queue name |
| `--topic` | `-t` | Topic name (requires `--subscription`) |
| `--subscription` | `-s` | Subscription name (requires `--topic`) |
| `--before` | | Only resubmit messages enqueued before this UTC datetime (ISO 8601) |
| `--dry-run` | | Preview message count without resubmitting |
| `--interactive` | `-i` | Interactive mode: view and select categories to resubmit |
| `--verbose` | `-v` | Enable verbose output |

## Examples

### Resubmit All Messages

```bash
# From a queue DLQ back to the queue
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue

# From a topic subscription DLQ back to the topic
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -t mytopic -s mysub
```

### Dry Run

Preview message count without resubmitting:

```bash
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue --dry-run
```

```
[DRY RUN] Found 1,523 messages in DLQ for queue 'myqueue'
```

### Time-Based Filtering

Resubmit only messages enqueued before a specific date:

```bash
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue --before 2024-01-01T00:00:00Z
```

### Interactive Mode

View messages grouped by Label and DeadLetterReason, then select which to resubmit:

```bash
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue -i
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

Select categories to resubmit (comma-separated numbers, 'all', or 'q' to quit): 1,2
Resubmitting 1,259 messages from 2 categories...
Resubmitted 1,259 messages from DLQ for queue 'myqueue'.
```

**Selection options:**

| Input | Action |
|-------|--------|
| `1,3,5` | Select specific categories |
| `1-5` | Select a range |
| `all` / `a` | Select all categories |
| `q` / empty | Quit without resubmitting |

## Message Properties

When resubmitting, the following message properties are preserved:

- Body and ContentType
- Subject (Label)
- MessageId and CorrelationId
- SessionId and PartitionKey
- To, ReplyTo, ReplyToSessionId
- TimeToLive
- All custom ApplicationProperties

## Required Permissions

The authenticated identity needs one of these Azure RBAC roles:

- **Azure Service Bus Data Owner** (recommended)
- Both **Azure Service Bus Data Receiver** and **Azure Service Bus Data Sender**
