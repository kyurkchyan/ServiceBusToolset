# dump-dlq

Export DLQ messages to a JSON file. This is a non-destructive operation that uses peek to read messages without removing
them from the queue.

## Synopsis

```bash
dotnet run -- dump-dlq -n <namespace> (-q <queue> | -t <topic> -s <subscription>) -o <output> [options]
```

## Options

| Option            | Short | Description                                                                                              |
|-------------------|-------|----------------------------------------------------------------------------------------------------------|
| `--namespace`     | `-n`  | **(Required)** Fully qualified Service Bus namespace                                                     |
| `--queue`         | `-q`  | Queue name                                                                                               |
| `--topic`         | `-t`  | Topic name (requires `--subscription`)                                                                   |
| `--subscription`  | `-s`  | Subscription name (requires `--topic`)                                                                   |
| `--output`        | `-o`  | Output JSON file path (required unless `--dry-run`)                                                      |
| `--before`        |       | Only include messages enqueued before this UTC datetime (ISO 8601)                                       |
| `--dry-run`       |       | Preview message count without writing to file                                                            |
| `--interactive`   | `-i`  | Interactive mode: view and select categories to dump                                                     |
| `--merge-similar` |       | Merge similar DLQ categories using LCS-based clustering (interactive mode only)                          |
| `--categorize-by` |       | Properties to categorize by. `#Prop` for system, `$Prop` for body. Default: `#Subject,#DeadLetterReason` |
| `--verbose`       | `-v`  | Enable verbose output                                                                                    |

## Examples

### Dump All Messages

```bash
# From a queue DLQ
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json

# From a topic subscription DLQ
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -t mytopic -s mysub -o dlq-messages.json
```

### Dry Run

Preview message count without writing to file:

```bash
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue --dry-run
```

```
[DRY RUN] Found 1,523 messages in DLQ for queue 'myqueue'
```

### Time-Based Filtering

Dump only messages enqueued before a specific date:

```bash
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o old-messages.json --before 2024-01-01T00:00:00Z
```

### Interactive Mode

View messages grouped by category properties, then select which to dump:

```bash
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o selected.json -i
```

```
Dead Letter Summary:
+---+---------------------+--------------------------------+-------+
| # | #Subject            | #DeadLetterReason              | Count |
+---+---------------------+--------------------------------+-------+
| 1 | OrderCreated        | MaxDeliveryCountExceeded       |   847 |
| 2 | PaymentProcessed    | MaxDeliveryCountExceeded       |   412 |
| 3 | (none)              | TTLExpiredException            |   198 |
| 4 | OrderCreated        | DeadLetterReasonHeader         |    66 |
+---+---------------------+--------------------------------+-------+
Total: 1,523 messages

Select categories to dump (comma-separated numbers, 'all', or 'q' to quit): 1,3
Dumping 1,045 messages from 2 categories...
Dumped 1,045 messages to 'selected.json'
```

### Custom Categorization

Categorize by any combination of system properties (`#Prop`) and JSON body properties (`$Prop`):

```bash
# Categorize by dead letter reason only
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o output.json -i \
  --categorize-by "#DeadLetterReason"

# Categorize by dead letter reason and a body property
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o output.json -i \
  --categorize-by '#DeadLetterReason,$ErrorCode'

# Nested body properties use dot notation
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o output.json -i \
  --categorize-by '#Subject,$Product.Category'
```

**Property syntax:**

| Prefix | Source                                                               | Example                                                           |
|--------|----------------------------------------------------------------------|-------------------------------------------------------------------|
| `#`    | System property on `ServiceBusReceivedMessage`                       | `#Subject`, `#DeadLetterReason`, `#ContentType`, `#CorrelationId` |
| `#`    | Falls back to `ApplicationProperties` if not a known system property | `#custom-header`                                                  |
| `$`    | Deserialized JSON body property                                      | `$ErrorCode`, `$Product.Category.Name`                            |

Unresolved properties (missing from message or body) display as `(none)`.

**Selection options:**

| Input       | Action                     |
|-------------|----------------------------|
| `1,3,5`     | Select specific categories |
| `1-5`       | Select a range             |
| `all` / `a` | Select all categories      |
| `q` / empty | Quit without dumping       |

## Output Format

Messages are exported as a JSON array with the following structure:

```json
[
  {
    "messageId": "abc-123",
    "correlationId": null,
    "subject": "OrderCreated",
    "contentType": "application/json",
    "body": {
      "orderId": 12345,
      "status": "pending"
    },
    "deadLetterReason": "MaxDeliveryCountExceeded",
    "deadLetterErrorDescription": "Message could not be consumed after 10 delivery attempts.",
    "enqueuedTime": "2024-01-15T10:30:00+00:00",
    "expiresAt": "2024-01-22T10:30:00+00:00",
    "sequenceNumber": 42,
    "sessionId": null,
    "partitionKey": null,
    "to": null,
    "replyTo": null,
    "replyToSessionId": null,
    "timeToLive": "7.00:00:00",
    "applicationProperties": {
      "customProp1": "value1",
      "retryCount": 3
    }
  }
]
```

### Body Encoding

- **JSON content**: Parsed and included as a native JSON object/array (not escaped string)
- **Text content**: Included as a JSON string
- **Binary content**: Base64 encoded string
- **Unknown content**: Attempts to parse as JSON first, then UTF-8 text, falls back to Base64 if invalid

## Required Permissions

The authenticated identity needs one of these Azure RBAC roles:

- **Azure Service Bus Data Owner**
- **Azure Service Bus Data Receiver**
