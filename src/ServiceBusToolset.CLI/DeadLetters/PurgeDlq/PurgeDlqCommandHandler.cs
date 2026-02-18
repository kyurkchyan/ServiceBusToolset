using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
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
        var streamCommand = new StreamDlqCommand(cliCommand.Namespace, target, cliCommand.MergeSimilar);
        var sessionResult = await mediator.Send(streamCommand, cancellationToken);

        if (!sessionResult.IsSuccess)
        {
            return sessionResult.ToErrorResult<Unit>();
        }

        using var session = sessionResult.Value;

        // Phase 1: Scanning (live updates, no user input)
        using (session.CategoryStream.Subscribe(snapshot =>
               {
                   Console.Clear();
                   RenderScanningView(snapshot, entityDescription, session.TotalDlqCount);
               }))
        {
            var scanTask = session.ScanCompletion.Task;
            var keyTask = Task.Run(() => WaitForStopKey(session.ScanCancellationToken));

            await Task.WhenAny(scanTask, keyTask);
            session.StopScanning();
            await scanTask;
        }

        // Phase 2: Selection & Purge
        var finalSnapshot = DlqCategoryScanner.BuildCategorySnapshot(session.Cache, cliCommand.MergeSimilar);

        if (session.Error != null)
        {
            Output.Error($"Error while scanning DLQ: {session.Error.Message}");
        }

        if (finalSnapshot.Categories.Count == 0)
        {
            Output.Info("No messages found in DLQ.");
            return Result.Success(Unit.Value);
        }

        Console.Clear();
        DlqCategoryDisplay.DisplayTable(finalSnapshot.Categories,
                                        finalSnapshot.TotalMessageCount,
                                        Output.Info,
                                        Output.Table);

        Console.Write("\nSelect categories to purge (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        var selectedIndices = CategorySelectionParser.Parse(input, finalSnapshot.Categories.Count);
        if (selectedIndices == null)
        {
            Output.Info("Operation cancelled.");
            return Result.Success(Unit.Value);
        }

        if (selectedIndices.Count == 0)
        {
            Output.Warning("No valid categories selected.");
            return Result.Success(Unit.Value);
        }

        var selection = CategorySelection.Build(finalSnapshot.Categories, selectedIndices);
        var effectiveKeys = finalSnapshot.MergeResult?.ExpandKeys(selection.SelectedKeys)
                            ?? selection.SelectedKeys;
        var messagesToPurge = session.SnapshotForCategories(effectiveKeys, cliCommand.BeforeEnqueueTime);

        if (messagesToPurge.Count == 0)
        {
            Output.Info("No messages match the selected categories.");
            return Result.Success(Unit.Value);
        }

        Output.Info($"Purging {messagesToPurge.Count} messages from {selection.SelectedCategoryCount} categories...");

        var purgeProgress = CreatePurgeProgressReporter();

        var purgeCommand = new PurgeFromCacheCommand(cliCommand.Namespace,
                                                     target,
                                                     messagesToPurge,
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

    private void RenderScanningView(DlqCategorySnapshot snapshot, string entityDescription, long? totalDlqCount)
    {
        var peekedInfo = totalDlqCount.HasValue
                             ? $"Peeked {snapshot.TotalMessageCount} from {totalDlqCount.Value}"
                             : $"{snapshot.TotalMessageCount} messages found so far";

        if (snapshot.Categories.Count == 0)
        {
            Output.Info($"Scanning DLQ for {entityDescription}... {peekedInfo}");
            Output.Info("Press 'x' to stop scanning and select categories");
            return;
        }

        DlqCategoryDisplay.DisplayTable(snapshot.Categories,
                                        snapshot.TotalMessageCount,
                                        Output.Info,
                                        Output.Table);

        Output.Info($"Scanning... {peekedInfo}");
        Output.Info("Press 'x' to stop scanning and select categories");
    }

    private static void WaitForStopKey(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            cancellationToken.WaitHandle.WaitOne();
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar is 'x' or 'X')
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }
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
