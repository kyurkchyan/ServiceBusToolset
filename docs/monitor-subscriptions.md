# monitor-subscriptions

Monitor Service Bus topic subscription statistics in a live-updating console table.

## Usage

```bash
dotnet run -- monitor-subscriptions -n <namespace> [options]
```

## Options

| Option | Short | Required | Default | Description |
|--------|-------|----------|---------|-------------|
| `--namespace` | `-n` | Yes | - | Fully qualified Service Bus namespace |
| `--topic` | `-t` | No | - | Topic name filter (wildcards or contains) |
| `--subscription` | `-s` | No | - | Subscription name filter (wildcards or contains) |
| `--refresh-interval` | `-r` | No | 5 | Refresh interval in seconds (min: 1) |
| `--verbose` | `-v` | No | false | Enable verbose output |

## Filter Patterns

Both `--topic` and `--subscription` options support:

- **Wildcards**: `*` matches any characters, `?` matches single character
  - `order-*` matches `order-topic`, `order-events`
  - `*-processor` matches `email-processor`, `payment-processor`
  - `sub-?` matches `sub-1`, `sub-a`
- **Contains**: Any string without wildcards performs a contains match
  - `payment` matches `payment-topic`, `my-payment-events`

## Output

```
╭────────────────────────────────────────────────────────────────────────────────╮
│                       Service Bus Subscription Monitor                          │
├─────────────────────┬─────────────────────┬──────────┬──────────┬──────────────┤
│ Topic               │ Subscription        │   Active │      DLQ │    Scheduled │
├─────────────────────┼─────────────────────┼──────────┼──────────┼──────────────┤
│ order-events        │ order-processor     │      125 │        3 │            0 │
│ order-events        │ analytics-consumer  │       42 │        0 │            0 │
│ payment-events      │ payment-processor   │    1,234 │       15 │            0 │
│                     │                     │          │          │              │
│ TOTAL               │                     │    1,401 │       18 │            0 │
╰────────────────────────────────────────────────────────────────────────────────╯
                              Last updated: 14:32:45
```

### Color Coding

- **DLQ count**: Red when > 0
- **Active count**: Yellow when > 1000
- **Totals row**: Bold

## Examples

```bash
# Monitor all subscriptions across all topics
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net

# Monitor subscriptions for topics matching a pattern
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net -t "order-*"

# Monitor specific subscriptions across all topics
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net -s "*-processor"

# Combine topic and subscription filters with custom refresh
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net -t "order-*" -s "*-processor" -r 10

# Filter by contains
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net -t payment

# Verbose mode shows update timestamps
dotnet run -- monitor-subscriptions -n mybus.servicebus.windows.net -v
```

## Behavior

- The table updates only when subscription counts change (not on every poll)
- Press `Ctrl+C` to stop monitoring gracefully
- Requires `Azure Service Bus Data Owner` or `Reader` role on the namespace
- Subscriptions are sorted by topic name, then subscription name

## Implementation

Uses Rx.NET for reactive polling and Spectre.Console for flicker-free live table updates. The observable pipeline:

1. `Observable.Timer(0, interval)` - Emit immediately, then every N seconds
2. `SelectMany(FetchSubscriptionStatistics)` - Async fetch all subscription stats
3. `DistinctUntilChanged(comparer)` - Only emit when counts change
4. `TakeUntil(cancellation)` - Stop on Ctrl+C
5. `ForEachAsync(updateTable)` - Update Spectre.Console Live display
