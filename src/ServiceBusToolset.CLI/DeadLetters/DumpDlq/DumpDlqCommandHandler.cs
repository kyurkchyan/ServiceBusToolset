using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using Unit = Mediator.Unit;

namespace ServiceBusToolset.CLI.DeadLetters.DumpDlq;

public sealed class DumpDlqCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler<DumpDlqCliCommand, Unit>(output)
{
    protected override async Task<Result<Unit>> ExecuteCoreAsync(
        DumpDlqCliCommand command,
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
            return await ExecuteInteractiveDumpAsync(command,
                                                     target,
                                                     entityDescription,
                                                     cancellationToken);
        }

        return await ExecuteDumpAsync(command,
                                      target,
                                      entityDescription,
                                      cancellationToken);
    }

    private async Task<Result<Unit>> ExecuteDryRunAsync(
        DumpDlqCliCommand cliCommand,
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

        var countDlqCommand = new CountDlqMessagesCommand(cliCommand.Namespace,
                                                          target,
                                                          cliCommand.BeforeEnqueueTime,
                                                          progress);

        var result = await mediator.Send(countDlqCommand, cancellationToken);

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

    private async Task<Result<Unit>> ExecuteDumpAsync(
        DumpDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Dumping DLQ messages for {entityDescription}...");

        var progress = CreateProgressReporter("Peeked {0} messages...");

        var command = new DumpDlqMessagesCommand(cliCommand.Namespace,
                                                 target,
                                                 cliCommand.OutputFile!,
                                                 cliCommand.BeforeEnqueueTime,
                                                 null,
                                                 progress);

        var result = await mediator.Send(command, cancellationToken);

        Console.WriteLine();

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        if (result.Value.MessageCount == 0)
        {
            Output.Info("No messages found matching criteria.");
        }
        else
        {
            Output.Success($"Dumped {result.Value.MessageCount} messages to '{result.Value.OutputFilePath}'");
        }

        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ExecuteInteractiveDumpAsync(
        DumpDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var streamCommand = new StreamDlqForDumpCommand(cliCommand.Namespace, target, cliCommand.MergeSimilar);
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

        // Phase 2: Selection (static display + user input)
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

        Console.Write("\nSelect categories to dump (comma-separated numbers, 'all', or 'q' to quit): ");
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
        var messagesToDump = session.SnapshotForCategories(effectiveKeys, cliCommand.BeforeEnqueueTime);

        if (messagesToDump.Count == 0)
        {
            Output.Info("No messages match the selected categories.");
            return Result.Success(Unit.Value);
        }

        Output.Info($"Dumping {messagesToDump.Count} messages from {selection.SelectedCategoryCount} categories...");

        var dumpCommand = new DumpFromCacheCommand(messagesToDump, cliCommand.OutputFile!);
        var dumpResult = await mediator.Send(dumpCommand, cancellationToken);

        if (!dumpResult.IsSuccess)
        {
            return dumpResult.ToErrorResult<Unit>();
        }

        Output.Success($"Dumped {dumpResult.Value.MessageCount} messages to '{dumpResult.Value.OutputFilePath}'");

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

    private static EntityTarget CreateTarget(DumpDlqCliCommand cliCommand)
        => cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
}
