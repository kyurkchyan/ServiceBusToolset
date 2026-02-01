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
        Console.Write("Select categories to dump (comma-separated numbers, 'all', or 'q' to quit): ");
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

        Output.Info($"Dumping {selection.SelectedCount} messages from {selection.SelectedCategoryCount} categories...");

        var dumpProgress = CreateProgressReporter("Peeked {0} messages...");

        var dumpCommand = new DumpDlqMessagesCommand(cliCommand.Namespace,
                                                     target,
                                                     cliCommand.OutputFile!,
                                                     cliCommand.BeforeEnqueueTime,
                                                     selection.SelectedKeys,
                                                     dumpProgress);

        var dumpResult = await mediator.Send(dumpCommand, cancellationToken);

        Console.WriteLine();

        if (!dumpResult.IsSuccess)
        {
            return dumpResult.ToErrorResult<Unit>();
        }

        Output.Success($"Dumped {dumpResult.Value.MessageCount} messages to '{dumpResult.Value.OutputFilePath}'");

        return Result.Success(Unit.Value);
    }

    private static EntityTarget CreateTarget(DumpDlqCliCommand cliCommand)
        => cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
}
