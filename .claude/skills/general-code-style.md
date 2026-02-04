---
description: General code writing rules and strategies that apply globally
---

# Overview

This document explains general code writing rules and strategies that apply globally

## Primary constructor when possible

Whenever possible, use C# primary constructor syntax.
E.g. - instead of doing this

```csharp
public class DumpDlqMessagesCommandHandler : ICommandHandler<DumpDlqMessagesCommand, Result<DlqDumpResult>>
{
    private readonly IServiceBusClientFactory _clientFactory;

    public DumpDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }
}
```

Do this

```csharp
public sealed class DumpDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<DumpDlqMessagesCommand, Result<DlqDumpResult>>
{}
```

## Use arrow functions when possible

Whenever  you have a single return statement, instead of doing this

```csharp
private static EntityTarget CreateTarget(DumpDlqCliCommand cliCommand)
{
    return cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
}
```

use arrow function

```csharp
private static EntityTarget CreateTarget(DumpDlqCliCommand cliCommand)
    => cliCommand.IsQueueMode
           ? EntityTarget.ForQueue(cliCommand.Queue!)
           : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
```

## Use collection expressions when possible

Instead of doing this

```csharp
var errors = new[] { "Error 1", "Error 2" };
var handlers = new List<ICommandHandler> { handler1, handler2 };
```

Do this

```csharp
var errors = ["Error 1", "Error 2"];
var handlers = [handler1, handler2];
```
