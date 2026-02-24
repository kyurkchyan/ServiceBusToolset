# diagnose-dlq

Diagnose DLQ messages by correlating with Application Insights telemetry. This command queries Application Insights to find exceptions, traces, and failed dependencies related to the dead-lettered messages.

## Synopsis

```bash
dotnet run -- diagnose-dlq -n <namespace> (-q <queue> | -t <topic> -s <subscription>) -a <app-insights-resource-id> [options]
```

## Options

| Option | Short | Description |
|--------|-------|-------------|
| `--namespace` | `-n` | **(Required)** Fully qualified Service Bus namespace |
| `--queue` | `-q` | Queue name |
| `--topic` | `-t` | Topic name (requires `--subscription`) |
| `--subscription` | `-s` | Subscription name (requires `--topic`) |
| `--app-insights` | `-a` | **(Required)** Application Insights resource ID |
| `--output` | `-o` | Output JSON file path (optional, prints summary to console) |
| `--before` | | Only include messages enqueued before this UTC datetime (ISO 8601) |
| `--max-messages` | | Maximum number of messages to diagnose (default: 1000) |
| `--interactive` | `-i` | Interactive mode: view and select categories to diagnose |
| `--merge-similar` | | Merge similar DLQ categories using LCS-based clustering (interactive mode only) |
| `--verbose` | `-v` | Enable verbose output |

## Getting the App Insights Resource ID

You can find the resource ID in the Azure portal:

1. Navigate to your Application Insights resource
2. Go to **Properties** (under Settings)
3. Copy the **Resource ID**

It looks like: `/subscriptions/{subscription-id}/resourceGroups/{resource-group}/providers/microsoft.insights/components/{app-insights-name}`

Or use Azure CLI:
```bash
az monitor app-insights component show --app <app-insights-name> -g <resource-group> --query id -o tsv
```

## Examples

### Basic Diagnosis

```bash
dotnet run -- diagnose-dlq \
  -n mynamespace.servicebus.windows.net \
  -q myqueue \
  -a "/subscriptions/xxx/resourceGroups/my-rg/providers/microsoft.insights/components/my-app-insights"
```

### Save Full Results to File

```bash
dotnet run -- diagnose-dlq \
  -n mynamespace.servicebus.windows.net \
  -q myqueue \
  -a "/subscriptions/xxx/resourceGroups/my-rg/providers/microsoft.insights/components/my-app-insights" \
  -o diagnostics.json
```

### Interactive Mode

Select specific message categories to diagnose:

```bash
dotnet run -- diagnose-dlq \
  -n mynamespace.servicebus.windows.net \
  -q myqueue \
  -a "/subscriptions/xxx/resourceGroups/my-rg/providers/microsoft.insights/components/my-app-insights" \
  -i
```

### Limit Number of Messages

```bash
dotnet run -- diagnose-dlq \
  -n mynamespace.servicebus.windows.net \
  -q myqueue \
  -a "/subscriptions/xxx/resourceGroups/my-rg/providers/microsoft.insights/components/my-app-insights" \
  --max-messages 50
```

## How It Works

1. **Extract Operation ID**: The command reads the `Diagnostic-Id` or `traceparent` property from each message's application properties. This contains the W3C trace context with the operation ID.

2. **Query Application Insights**: For each message, it queries:
   - **Exceptions**: Errors that occurred during message processing
   - **Traces**: Warning and error level log messages
   - **Dependencies**: Failed external calls (HTTP, SQL, etc.)

3. **Correlate Results**: Results are matched by `operation_Id` in Application Insights, which corresponds to the trace ID from the message.

## Sample Output

```
Connecting to Application Insights...
Diagnosing DLQ messages for queue 'myqueue'...
Peeked 100 messages...
Diagnosed 87/100 messages (skipped 13)...
Diagnosed 87 messages, skipped 13 (no operation ID or query error)

Found telemetry for 42 of 87 messages

Diagnostic Summary:
===================

Top Exceptions:
+-------+------------------------------------------+----------------------------------------------+
| Count | Type                                     | Message                                      |
+-------+------------------------------------------+----------------------------------------------+
|    23 | System.InvalidOperationException         | Entity not found in database                 |
|    12 | System.Net.Http.HttpRequestException     | Connection refused                           |
|     5 | System.TimeoutException                  | Operation timed out after 30 seconds         |
+-------+------------------------------------------+----------------------------------------------+

Failed Dependencies:
+-------+------+------------------------------------------+
| Count | Type | Target                                   |
+-------+------+------------------------------------------+
|    15 | SQL  | mydb.database.windows.net                |
|     8 | HTTP | api.external-service.com                 |
+-------+------+------------------------------------------+
```

## Output Format

When using `-o` to save results, the output is a JSON array:

```json
[
  {
    "messageId": "abc-123",
    "subject": "OrderCreated",
    "operationId": "5d7504f5b99c1e407b43dff61172ad10",
    "enqueuedTime": "2024-01-15T10:30:00+00:00",
    "deadLetterReason": "MaxDeliveryCountExceeded",
    "body": { "orderId": 12345 },
    "exceptions": [
      {
        "timestamp": "2024-01-15T10:30:05+00:00",
        "problemId": "System.InvalidOperationException at MyService.ProcessOrder",
        "exceptionType": "System.InvalidOperationException",
        "outerMessage": "Failed to process order",
        "innermostMessage": "Entity not found in database",
        "details": "..."
      }
    ],
    "traces": [
      {
        "timestamp": "2024-01-15T10:30:04+00:00",
        "message": "Processing order 12345",
        "severityLevel": 2
      }
    ],
    "failedDependencies": [
      {
        "timestamp": "2024-01-15T10:30:05+00:00",
        "type": "SQL",
        "target": "mydb.database.windows.net",
        "name": "SELECT * FROM Orders WHERE Id = @id",
        "data": "...",
        "resultCode": 0,
        "success": false,
        "durationMs": 150.5
      }
    ]
  }
]
```

## Correlation Requirements

For this command to work, your messages must have one of these properties in `applicationProperties`:

| Property | Format | Example |
|----------|--------|---------|
| `Diagnostic-Id` | W3C trace context | `00-5d7504f5b99c1e407b43dff61172ad10-5ad61dc9ce2119eb-00` |
| `traceparent` | W3C trace context | `00-5d7504f5b99c1e407b43dff61172ad10-5ad61dc9ce2119eb-00` |
| `Operation-Id` | Trace ID | `5d7504f5b99c1e407b43dff61172ad10` |

If none of these are present, the command falls back to the message's `CorrelationId`.

## Required Permissions

The authenticated identity needs:

**Service Bus:**
- **Azure Service Bus Data Receiver** (to peek messages)

**Application Insights:**
- **Log Analytics Reader** or **Monitoring Reader** (to query telemetry)

## Troubleshooting

### No telemetry found

If no telemetry is found for messages:

1. **Check the App Insights resource**: Ensure you're querying the correct Application Insights instance that receives telemetry from your message processor.

2. **Check retention**: Application Insights has a default retention of 90 days. Older telemetry may have been purged.

3. **Check correlation**: Verify that your message processor is correctly propagating the trace context from the message to Application Insights.

4. **Check time range**: The command searches from 1 hour before to 24 hours after the message's enqueue time. If processing happened outside this window, telemetry won't be found.
