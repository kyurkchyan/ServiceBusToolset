# monitor-queues

Monitor Service Bus queue statistics in a live-updating console table.

## Usage

```bash
dotnet run -- monitor-queues -n <namespace> [options]
```

## Options

| Option | Short | Required | Default | Description |
|--------|-------|----------|---------|-------------|
| `--namespace` | `-n` | Yes | - | Fully qualified Service Bus namespace |
| `--filter` | `-f` | No | - | Queue name filter (wildcards or contains) |
| `--refresh-interval` | `-r` | No | 5 | Refresh interval in seconds (min: 1) |
| `--verbose` | `-v` | No | false | Enable verbose output |

## Filter Patterns

The `--filter` option supports:

- **Wildcards**: `*` matches any characters, `?` matches single character
  - `order-*` matches `order-queue`, `order-processing`
  - `*-dlq` matches `payment-dlq`, `notification-dlq`
  - `order-?` matches `order-1`, `order-a`
- **Contains**: Any string without wildcards performs a contains match
  - `payment` matches `payment-queue`, `my-payment-service`

## Output

```
╭──────────────────────────────────────────────────────────────────╮
│                    Service Bus Queue Monitor                      │
├───────────────────────┬──────────┬──────────┬───────────────────┤
│ Queue Name            │   Active │      DLQ │         Scheduled │
├───────────────────────┼──────────┼──────────┼───────────────────┤
│ order-queue           │      125 │        3 │                 0 │
│ payment-queue         │       42 │        0 │                12 │
│ notification-queue    │    1,234 │       15 │                 0 │
│                       │          │          │                   │
│ TOTAL                 │    1,401 │       18 │                12 │
╰──────────────────────────────────────────────────────────────────╯
                      Last updated: 14:32:45
```

### Color Coding

- **DLQ count**: Red when > 0
- **Active count**: Yellow when > 1000
- **Totals row**: Bold

## Examples

```bash
# Monitor all queues with default 5-second refresh
dotnet run -- monitor-queues -n mybus.servicebus.windows.net

# Monitor queues matching pattern with 10-second refresh
dotnet run -- monitor-queues -n mybus.servicebus.windows.net -f "order-*" -r 10

# Filter by contains
dotnet run -- monitor-queues -n mybus.servicebus.windows.net -f payment

# Verbose mode shows update timestamps
dotnet run -- monitor-queues -n mybus.servicebus.windows.net -v
```

## Behavior

- The table updates only when queue counts change (not on every poll)
- Press `Ctrl+C` to stop monitoring gracefully
- Requires `Azure Service Bus Data Owner` or `Reader` role on the namespace

## Implementation

Uses Rx.NET for reactive polling and Spectre.Console for flicker-free live table updates. The observable pipeline:

1. `Observable.Timer(0, interval)` - Emit immediately, then every N seconds
2. `SelectMany(FetchQueueStatistics)` - Async fetch all queue stats
3. `DistinctUntilChanged(comparer)` - Only emit when counts change
4. `TakeUntil(cancellation)` - Stop on Ctrl+C
5. `ForEachAsync(updateTable)` - Update Spectre.Console Live display
