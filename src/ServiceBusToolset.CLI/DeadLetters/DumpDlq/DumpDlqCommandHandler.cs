using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;
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

    /// <summary>
    /// Runs an interactive session to scan and categorize dead-letter messages, lets the user select categories, and dumps the selected messages to a file.
    /// </summary>
    /// <param name="cliCommand">CLI command options controlling namespace, categorization, merging, filters, and output file.</param>
    /// <param name="target">The Service Bus entity target (queue or subscription) to operate on.</param>
    /// <param name="entityDescription">Human-readable description of the target entity used for output messages.</param>
    /// <param name="cancellationToken">Token to observe for cancellation of the operation.</param>
    /// <returns>`Result&lt;Unit&gt;` with success when the operation completes or the user cancels the dump selection; otherwise an error result describing the failure.</returns>
    private async Task<Result<Unit>> ExecuteInteractiveDumpAsync(
        DumpDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        CategorizationSchema schema;
        try
        {
            schema = CategorizationSchema.Parse(cliCommand.CategorizeBy);
        }
        catch (ArgumentException ex)
        {
            return Result.Invalid(new ValidationError(ex.Message));
        }

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
                                                     "dump");
        if (selection == null)
        {
            return Result.Success(Unit.Value);
        }

        Output.Info($"Dumping {selection.Messages.Count} messages from {selection.SelectedCategoryCount} categories...");

        var dumpCommand = new DumpFromCacheCommand(selection.Messages, cliCommand.OutputFile!);
        var dumpResult = await mediator.Send(dumpCommand, cancellationToken);

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
