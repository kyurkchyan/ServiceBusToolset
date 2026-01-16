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

| Command | Description |
|---------|-------------|
| [purge-dlq](docs/purge-dlq.md) | Purge messages from a dead letter queue |

## Quick Start

```bash
# Purge all DLQ messages from a queue
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue

# Interactive mode - select which message categories to purge
dotnet run -- purge-dlq -n mynamespace.servicebus.windows.net -q myqueue -i
```

## Authentication

Uses [DefaultAzureCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.defaultazurecredential). For local development, run `az login`.

## License

MIT
