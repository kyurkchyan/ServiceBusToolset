# ServiceBusToolset

[![Build](https://github.com/kyurkchyan/ServiceBusToolset/actions/workflows/alpha-release.yml/badge.svg?branch=master)](https://github.com/kyurkchyan/ServiceBusToolset/actions/workflows/alpha-release.yml)
[![Coverage](https://img.shields.io/endpoint?url=https://gist.githubusercontent.com/kyurkchyan/COVERAGE_GIST_ID/raw/coverage-badge.json)](https://github.com/kyurkchyan/ServiceBusToolset/actions/workflows/alpha-release.yml)
[![NuGet](https://img.shields.io/nuget/v/ServiceBusToolset)](https://www.nuget.org/packages/ServiceBusToolset)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A command-line tool for managing Azure Service Bus.

## Installation

### Global Tool (Recommended)

```bash
# Install from NuGet
dotnet tool install -g ServiceBusToolset

# Update to latest version
dotnet tool update -g ServiceBusToolset

# Install preview/alpha version
dotnet tool install -g ServiceBusToolset --prerelease
```

### Build from Source

```bash
git clone https://github.com/kyurkchyan/ServiceBusToolset.git
cd ServiceBusToolset
dotnet build
```

## Prerequisites

- .NET 10 SDK or later
- Azure CLI logged in (`az login`)

## Commands

| Command                                                | Description                                                       |
|--------------------------------------------------------|-------------------------------------------------------------------|
| [purge-dlq](docs/purge-dlq.md)                         | Purge messages from a dead letter queue                           |
| [resubmit-dlq](docs/resubmit-dlq.md)                   | Resubmit messages from a dead letter queue back to the main queue |
| [dump-dlq](docs/dump-dlq.md)                           | Export DLQ messages to a JSON file                                |
| [diagnose-dlq](docs/diagnose-dlq.md)                   | Diagnose DLQ messages using Application Insights telemetry        |
| [monitor-queues](docs/monitor-queues.md)               | Monitor queue statistics in a live-updating console table         |
| [monitor-subscriptions](docs/monitor-subscriptions.md) | Monitor topic subscription statistics in a live-updating table    |

## Quick Start

```bash
# Purge all DLQ messages from a queue
sbtools purge-dlq -n mynamespace.servicebus.windows.net -q myqueue

# Interactive mode - select which message categories to purge
sbtools purge-dlq -n mynamespace.servicebus.windows.net -q myqueue -i

# Resubmit DLQ messages back to the main queue
sbtools resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue

# Interactive mode - select which message categories to resubmit
sbtools resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue -i

# Dump DLQ messages to a JSON file
sbtools dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json

# Interactive mode - select which message categories to dump
sbtools dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json -i

# Categorize by custom properties (system #Prop, body $Prop)
sbtools dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json -i \
  --categorize-by "#DeadLetterReason,$ErrorCode"

# Diagnose DLQ messages using Application Insights
sbtools diagnose-dlq -n mynamespace.servicebus.windows.net -q myqueue \
  -a "/subscriptions/.../resourceGroups/.../providers/microsoft.insights/components/my-app-insights"

# Monitor all queues with live-updating table
sbtools monitor-queues -n mynamespace.servicebus.windows.net

# Monitor queues matching a pattern with 10-second refresh
sbtools monitor-queues -n mynamespace.servicebus.windows.net -f "order-*" -r 10

# Monitor all topic subscriptions
sbtools monitor-subscriptions -n mynamespace.servicebus.windows.net

# Monitor subscriptions for specific topics with subscription filter
sbtools monitor-subscriptions -n mynamespace.servicebus.windows.net -t "orders*" -s "*-processor"
```

> **Note:** If running from source instead of the global tool, replace `sbtools` with `dotnet run --` in the commands
> above.

## Authentication

Uses [DefaultAzureCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential). For
local development, run `az login`.

## License

MIT
