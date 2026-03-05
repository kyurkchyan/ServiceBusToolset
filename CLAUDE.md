# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run --project src/ServiceBusToolset.CLI -- <command> [options]
dotnet run --project src/ServiceBusToolset.CLI -- --help
```

## Architecture

.NET 10 CLI tool for Azure Service Bus operations using:

- **Vertical Slice Architecture** - Features organized by business capability
- **Mediator Pattern** (martinothamar/Mediator) - CQRS for Application layer
- **Ardalis.Result** - Structured return types
- **Microsoft DI** - Dependency injection

### Project Structure

```
src/
├── ServiceBusToolset.Application/       # Business logic layer
│   ├── Common/                          # Cross-cutting utilities
│   │   └── ServiceBus/                  # Service Bus abstractions & helpers
│   └── DeadLetters/                     # DLQ feature area
│       ├── Common/                      # Shared DLQ utilities
│       └── DumpDlq/                     # Vertical slice: dump-dlq feature
│
└── ServiceBusToolset.CLI/               # Presentation layer
    ├── Commands/                        # CLI command handlers
    ├── Options/                         # CLI argument definitions
    └── Services/                        # CLI-specific services
```

### Vertical Slice Architecture

Each feature is a self-contained slice with all related code in one folder:

```
DeadLetters/DumpDlq/
├── DumpDlqMessagesCommand.cs        # Input: ICommand<Result<T>>
├── DumpDlqMessagesCommandHandler.cs # Logic: ICommandHandler<TCommand, Result<T>>
└── DlqDumpResult.cs                 # Output: Result record
```

Cross-cutting concerns go in `Common/` folders at the appropriate level:

- `Common/ServiceBus/` - Generic Service Bus utilities (any feature)
- `DeadLetters/Common/` - DLQ utilities (shared by DumpDlq, PurgeDlq, etc.)

### Adding a New Feature

1. **Create Application layer slice** in appropriate area:
   ```
   src/ServiceBusToolset.Application/DeadLetters/PurgeDlq/
   ├── PurgeDlqMessagesCommand.cs
   ├── PurgeDlqMessagesCommandHandler.cs
   └── PurgeDlqResult.cs
   ```

2. **Create CLI handler** in `CLI/Commands/`:
    - Inject `ISender` for Mediator
    - Map CLI options to Application commands
    - Handle `Result<T>` responses with `BaseCommandHandler`

3. **Register in Program.cs**:
    - Add options type to `ParseArguments<...>()`
    - Add `MapResult` handler

### Key Services

**Application Layer:**

- `IServiceBusClientFactory` - Creates Service Bus clients
- `DlqMessageService` - DLQ peek/filter operations

**CLI Layer:**

- `IConsoleOutput` - Console output abstraction
- `BaseCommandHandler` - Exception handling, result mapping

### Dependencies

- `Mediator` - CQRS pattern with source generation
- `Ardalis.Result` - Result<T> return types
- `CommandLineParser` - CLI argument parsing
- `Spectre.Console` - Rich console output
- `Azure.Identity` - DefaultAzureCredential

## Code Style

- **Never use `#region`/`#endregion`** - Use class organization and whitespace instead
- **Test naming**: `[ClassName]Should` for test classes, `[Action]_When[Condition]` for test methods
- **Assertions**: Use Shouldly library
- **Mocking**: Use NSubstitute
