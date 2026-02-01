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
        Output.Info($"Analyzing DLQ for {entityDescription}...");

        var analyzeProgress = CreateProgressReporter("Peeked {0} messages...");

        var analyzeCommand = new AnalyzeDlqCategoriesCommand(cliCommand.Namespace,
                                                             target,
                                                             analyzeProgress);

        var categoriesResult = await mediator.Send(analyzeCommand, cancellationToken);

        Console.WriteLine();

        if (!categoriesResult.IsSuccess)
        {
            return categoriesResult.ToErrorResult<Unit>();
        }

        var categories = categoriesResult.Value.Categories;

        if (categories.Count == 0)
        {
            Output.Info("No messages found in DLQ.");
            return Result.Success(Unit.Value);
        }

        DlqCategoryDisplay.DisplayTable(categories,
                                        categoriesResult.Value.TotalMessageCount,
                                        Output.Info,
                                        Output.Table);

        Output.Info("");
        Console.Write("Select categories to purge (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        var selectedIndices = CategorySelectionParser.Parse(input, categories.Count);
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

        var selection = CategorySelection.Build(categories, selectedIndices);

        Output.Info($"Purging {selection.SelectedCount} messages from {selection.SelectedCategoryCount} categories...");

        var purgeProgress = CreatePurgeProgressReporter();

        var purgeCommand = new PurgeDlqMessagesCommand(cliCommand.Namespace,
                                                       target,
                                                       cliCommand.BeforeEnqueueTime,
                                                       selection.SelectedKeys,
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
