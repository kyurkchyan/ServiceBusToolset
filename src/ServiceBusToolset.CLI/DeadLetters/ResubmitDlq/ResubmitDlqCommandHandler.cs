using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using Unit = Mediator.Unit;

namespace ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;

public sealed class ResubmitDlqCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler<ResubmitDlqCliCommand, Unit>(output)
{
    protected override Task<Result<Unit>> ExecuteCoreAsync(
        ResubmitDlqCliCommand command,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        var target = CreateTarget(command);
        var entityDescription = target.GetDescription();

        if (command.DryRun)
        {
            return ExecuteDryRunAsync(command,
                                      target,
                                      entityDescription,
                                      verbose,
                                      cancellationToken);
        }

        return command.Interactive
                   ? ExecuteInteractiveResubmitAsync(command,
                                                     target,
                                                     entityDescription,
                                                     cancellationToken)
                   : ExecuteResubmitAsync(command,
                                          target,
                                          entityDescription,
                                          cancellationToken);
    }

    private async Task<Result<Unit>> ExecuteDryRunAsync(
        ResubmitDlqCliCommand cliCommand,
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
            return Result<Unit>.Error(new ErrorList(result.Errors.ToList()));
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

    private async Task<Result<Unit>> ExecuteResubmitAsync(
        ResubmitDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        var targetInfo = GetTargetInfo(cliCommand);
        Output.Info($"Resubmitting DLQ messages for {entityDescription}{targetInfo}...");

        var progress = CreateResubmitProgressReporter();

        var command = new ResubmitDlqMessagesCommand(cliCommand.Namespace,
                                                     target,
                                                     cliCommand.EffectiveTarget,
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
            Output.Success($"Resubmitted {result.Value.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo} (skipped {result.Value.SkippedCount} newer messages)");
        }
        else
        {
            Output.Success($"Resubmitted {result.Value.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo}");
        }

        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ExecuteInteractiveResubmitAsync(
        ResubmitDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Scanning DLQ for {entityDescription}...");

        var streamCommand = new StreamDlqCategoriesCommand(cliCommand.Namespace, target);
        var sessionResult = await mediator.Send(streamCommand, cancellationToken);

        if (!sessionResult.IsSuccess)
        {
            return sessionResult.ToErrorResult<Unit>();
        }

        using var session = sessionResult.Value;

        DlqCategorySnapshot? latestSnapshot = null;
        var snapshotLock = new object();

        using var subscription = session.CategoryStream.Subscribe(snapshot =>
        {
            lock (snapshotLock)
            {
                latestSnapshot = snapshot;
            }

            RenderCategoryTable(snapshot, entityDescription);
        });

        Console.Write("\nSelect categories to resubmit (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        DlqCategorySnapshot currentSnapshot;
        lock (snapshotLock)
        {
            currentSnapshot = latestSnapshot ?? new DlqCategorySnapshot([], 0, false);
        }

        if (session.Error != null)
        {
            Output.Error($"Error while scanning DLQ: {session.Error.Message}");
        }

        if (currentSnapshot.Categories.Count == 0)
        {
            Output.Info("No messages found in DLQ.");
            return Result.Success(Unit.Value);
        }

        var selectedIndices = CategorySelectionParser.Parse(input, currentSnapshot.Categories.Count);
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

        var selection = CategorySelection.Build(currentSnapshot.Categories, selectedIndices);
        var messagesToResubmit = session.SnapshotForCategories(selection.SelectedKeys, cliCommand.BeforeEnqueueTime);

        if (messagesToResubmit.Count == 0)
        {
            Output.Info("No messages match the selected categories.");
            return Result.Success(Unit.Value);
        }

        var targetInfo = GetTargetInfo(cliCommand);
        Output.Info($"Resubmitting {messagesToResubmit.Count} messages from {selection.SelectedCategoryCount} categories{targetInfo}...");

        var resubmitProgress = CreateResubmitProgressReporter();

        var resubmitCommand = new ResubmitFromCacheCommand(cliCommand.Namespace,
                                                           target,
                                                           cliCommand.EffectiveTarget,
                                                           messagesToResubmit,
                                                           session.ResubmitTracker,
                                                           resubmitProgress);

        var resubmitResult = await mediator.Send(resubmitCommand, cancellationToken);

        Console.WriteLine();

        if (!resubmitResult.IsSuccess)
        {
            return resubmitResult.ToErrorResult<Unit>();
        }

        Output.Success($"Resubmitted {resubmitResult.Value.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo}.");

        return Result.Success(Unit.Value);
    }

    private void RenderCategoryTable(DlqCategorySnapshot snapshot, string entityDescription)
    {
        if (snapshot.Categories.Count == 0)
        {
            var status = snapshot.IsComplete ? "Complete" : "Scanning...";
            Output.Progress($"{status} {snapshot.TotalMessageCount} messages in {entityDescription}");
            return;
        }

        Console.WriteLine();
        DlqCategoryDisplay.DisplayTable(snapshot.Categories,
                                        snapshot.TotalMessageCount,
                                        Output.Info,
                                        Output.Table);

        if (!snapshot.IsComplete)
        {
            Output.Info("(still scanning...)");
        }
    }

    private IProgress<(int Resubmitted, int Skipped)> CreateResubmitProgressReporter()
    {
        return new Progress<(int Resubmitted, int Skipped)>(progress =>
        {
            if (progress.Skipped > 0)
            {
                Output.Progress($"Resubmitted {progress.Resubmitted} messages (skipped {progress.Skipped})...");
            }
            else
            {
                Output.Progress($"Resubmitted {progress.Resubmitted} messages...");
            }
        });
    }

    private static string GetTargetInfo(ResubmitDlqCliCommand cliCommand)
    {
        var hasCustomTarget = !string.IsNullOrEmpty(cliCommand.TargetQueue) || !string.IsNullOrEmpty(cliCommand.TargetTopic);
        if (!hasCustomTarget)
        {
            return string.Empty;
        }

        var targetType = !string.IsNullOrEmpty(cliCommand.TargetQueue) ? "queue" : "topic";
        return $" to {targetType} '{cliCommand.EffectiveTarget}'";
    }

    private static EntityTarget CreateTarget(ResubmitDlqCliCommand cliCommand)
        => cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
}
