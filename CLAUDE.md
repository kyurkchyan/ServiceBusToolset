# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run -- <command> [options]
dotnet run -- --help
```

## Architecture

.NET 10 CLI tool for Azure Service Bus operations using CommandLineParser for argument parsing and DefaultAzureCredential for authentication.

### Project Structure

```
ServiceBusToolset/
├── Options/      # CLI options with [Verb] and [Option] attributes
├── Commands/     # Command implementations
├── Services/     # Business logic and external integrations
└── Models/       # Data models
```

### Adding a New Command

1. **Create Options class** in `Options/`:
   - Use `[Verb("command-name", HelpText = "...")]`
   - Use `[Option('c', "long-name", Required = true/false, HelpText = "...")]`
   - Add `Validate()` method returning `string?` (null = valid)

2. **Create Command class** in `Commands/`:
   - Implement `ICommand<TOptions>` interface
   - Inherit `BaseCommand<TOptions>` for DLQ operations (provides `CreateDlqReceiver`, `GetEntityDescription`)
   - Use primary constructor pattern
   - Handle exceptions: `AuthenticationFailedException`, `ServiceBusException`, `OperationCanceledException`

3. **Register in Program.cs**:
   - Add options type to `ParseArguments<...>()`
   - Add `MapResult` handler that creates command and calls `ExecuteAsync`

### Key Services

- `IServiceBusClientFactory` - Creates `ServiceBusClient` and `ServiceBusAdministrationClient`
- `IConsoleOutput` - Console output abstraction (Info, Success, Warning, Error, Verbose, Table)
- `IDlqCategoryAnalyzer` - Groups DLQ messages by Label and DeadLetterReason
- `IAppInsightsService` - Queries Application Insights for diagnostics

### Dependencies

- `CommandLineParser` - CLI argument parsing with verbs
- `Spectre.Console` - Rich console output and tables
- `System.Reactive` - Rx.NET for reactive streams (monitor-queues)
- `Azure.Identity` - DefaultAzureCredential (requires `az login` for local dev)
