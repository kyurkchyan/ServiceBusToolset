using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;
using Unit = Mediator.Unit;

namespace ServiceBusToolset.CLI.DeadLetters.PurgeDlq;

public sealed class PurgeDlqCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler<PurgeDlqCliCommand, Unit>(output)
{
    protected override async Task<Result<Unit>> ExecuteCoreAsync(
        PurgeDlqCliCommand command,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        var target = CreateTarget(command);
        var entityDescription = target.GetDescription();

        if (command.DryRun)
        {
            return await ExecuteDryRunAsync(command,
                                            target,
                                            entityDescription,
                                            verbose,
                                            cancellationToken);
        }

        if (command.Interactive)
        {
            return await ExecuteInteractivePurgeAsync(command,
                                                      target,
                                                      entityDescription,
                                                      cancellationToken);
        }

        return await ExecutePurgeAsync(command,
                                       target,
                                       entityDescription,
                                       cancellationToken);
    }

    private async Task<Result<Unit>> ExecuteDryRunAsync(
        PurgeDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        bool verbose,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            Output.Verbose("Using slow count due to --before filter", verbose);
        }

        var progress = cliCommand.BeforeEnqueueTime.HasValue
                           ? CreateProgressReporter("Counted {0} messages...")
                           : null;

        var countCommand = new CountDlqMessagesCommand(cliCommand.Namespace,
                                                       target,
                                                       cliCommand.BeforeEnqueueTime,
                                                       progress);

        var result = await mediator.Send(countCommand, cancellationToken);

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            Console.WriteLine();
        }

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        if (result.Value.FilteredCount.HasValue)
        {
            Output.Success($"[DRY RUN] Found {result.Value.FilteredCount} messages enqueued before {result.Value.BeforeTime:O} (total: {result.Value.TotalCount})");
        }
        else
        {
            Output.Success($"[DRY RUN] Found {result.Value.TotalCount} messages in DLQ for {entityDescription}");
        }

        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ExecutePurgeAsync(
        PurgeDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Purging DLQ for {entityDescription}...");

        Console.Write("Are you sure you want to purge all dead letter messages? (y/N): ");
        var confirmation = Output.ReadLine();
        if (!string.Equals(confirmation?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Output.Info("Operation cancelled.");
            return Result.Success(Unit.Value);
        }

        var progress = CreatePurgeProgressReporter();

        var command = new PurgeDlqMessagesCommand(cliCommand.Namespace,
                                                  target,
                                                  cliCommand.BeforeEnqueueTime,
                                                  null,
                                                  progress);

        var result = await mediator.Send(command, cancellationToken);

        Console.WriteLine();

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        if (result.Value.SkippedCount > 0)
        {
            Output.Success($"Purged {result.Value.PurgedCount} messages from DLQ for {entityDescription} (skipped {result.Value.SkippedCount} newer messages)");
        }
        else
        {
            Output.Success($"Purged {result.Value.PurgedCount} messages from DLQ for {entityDescription}");
        }

        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ExecuteInteractivePurgeAsync(
        PurgeDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var schema = CategorizationSchema.Parse(cliCommand.CategorizeBy);
        var streamCommand = new StreamDlqCommand(cliCommand.Namespace,
                                                 target,
                                                 cliCommand.MergeSimilar,
                                                 schema);
        var sessionResult = await mediator.Send(streamCommand, cancellationToken);

        if (!sessionResult.IsSuccess)
        {
            return sessionResult.ToErrorResult<Unit>();
        }

        using var session = sessionResult.Value;

        await session.RunScanningPhaseAsync(Output, entityDescription);

        var selection = session.GetCategorySelection(Output,
                                                     cliCommand.MergeSimilar,
                                                     cliCommand.BeforeEnqueueTime,
                                                     "purge");
        if (selection == null)
        {
            return Result.Success(Unit.Value);
        }

        Output.Info($"Purging {selection.Messages.Count} messages from {selection.SelectedCategoryCount} categories...");

        var purgeProgress = CreatePurgeProgressReporter();

        var purgeCommand = new PurgeFromCacheCommand(cliCommand.Namespace,
                                                     target,
                                                     selection.Messages,
                                                     purgeProgress);

        var purgeResult = await mediator.Send(purgeCommand, cancellationToken);

        Console.WriteLine();

        if (!purgeResult.IsSuccess)
        {
            return purgeResult.ToErrorResult<Unit>();
        }

        Output.Success($"Purged {purgeResult.Value.PurgedCount} messages from DLQ for {entityDescription}.");

        return Result.Success(Unit.Value);
    }

    private Progress<(int Purged, int Skipped)> CreatePurgeProgressReporter()
    {
        return new Progress<(int Purged, int Skipped)>(progress =>
        {
            if (progress.Skipped > 0)
            {
                Output.Progress($"Purged {progress.Purged} messages (skipped {progress.Skipped})...");
            }
            else
            {
                Output.Progress($"Purged {progress.Purged} messages...");
            }
        });
    }

    private static EntityTarget CreateTarget(PurgeDlqCliCommand cliCommand)
        => cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
}
