# ServiceBusToolset

A command-line tool for managing Azure Service Bus.

## Prerequisites

- .NET 10 SDK or later
- Azure CLI logged in (`az login`)

## Installation

```bash
dotnet build
```

## Commands

| Command                              | Description                                                       |
|--------------------------------------|-------------------------------------------------------------------|
| [purge-dlq](docs/purge-dlq.md)       | Purge messages from a dead letter queue                           |
| [resubmit-dlq](docs/resubmit-dlq.md) | Resubmit messages from a dead letter queue back to the main queue |
| [dump-dlq](docs/dump-dlq.md)         | Export DLQ messages to a JSON file                                |

## Quick Start

```bash
# Purge all DLQ messages from a queue
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue

# Interactive mode - select which message categories to purge
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue -i

# Resubmit DLQ messages back to the main queue
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue

# Interactive mode - select which message categories to resubmit
dotnet run -- resubmit-dlq -n mynamespace.servicebus.windows.net -q myqueue -i

# Dump DLQ messages to a JSON file
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json

# Interactive mode - select which message categories to dump
dotnet run -- dump-dlq -n mynamespace.servicebus.windows.net -q myqueue -o dlq-messages.json -i
```

## Authentication

Uses [DefaultAzureCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential). For
local development, run `az login`.

## License

MIT
