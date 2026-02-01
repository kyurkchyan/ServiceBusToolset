using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.Application.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;

public sealed class ResubmitDlqCommandHandler(ISender mediator,
                                              IConsoleOutput output) : BaseCommandHandler(output)
{
    public async Task<int> ExecuteAsync(ResubmitDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
    {
        var validationError = cliCommand.Validate();
        if (validationError != null)
        {
            Output.Error(validationError);
            return 1;
        }

        var target = CreateTarget(cliCommand);
        var entityDescription = target.GetDescription();

        return await ExecuteWithExceptionHandling(async () =>
                                                  {
                                                      if (cliCommand.DryRun)
                                                      {
                                                          return await ExecuteDryRunAsync(cliCommand,
                                                                                          target,
                                                                                          entityDescription,
                                                                                          cancellationToken);
                                                      }

                                                      if (cliCommand.Interactive)
                                                      {
                                                          return await ExecuteInteractiveResubmitAsync(cliCommand,
                                                                                                       target,
                                                                                                       entityDescription,
                                                                                                       cancellationToken);
                                                      }

                                                      return await ExecuteResubmitAsync(cliCommand,
                                                                                        target,
                                                                                        entityDescription,
                                                                                        cancellationToken);
                                                  },
                                                  cliCommand.Verbose);
    }

    private async Task<int> ExecuteDryRunAsync(
        ResubmitDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"[DRY RUN] Counting messages in DLQ for {entityDescription}...");

        if (cliCommand.BeforeEnqueueTime.HasValue)
        {
            Output.Verbose("Using slow count due to --before filter", cliCommand.Verbose);
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

        return HandleResult(result,
                            r =>
                            {
                                if (r.FilteredCount.HasValue)
                                {
                                    Output.Success($"[DRY RUN] Found {r.FilteredCount} messages enqueued before {r.BeforeTime:O} (total: {r.TotalCount})");
                                }
                                else
                                {
                                    Output.Success($"[DRY RUN] Found {r.TotalCount} messages in DLQ for {entityDescription}");
                                }
                            });
    }

    private async Task<int> ExecuteResubmitAsync(
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

        return HandleResult(result,
                            r =>
                            {
                                if (r.SkippedCount > 0)
                                {
                                    Output.Success($"Resubmitted {r.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo} (skipped {r.SkippedCount} newer messages)");
                                }
                                else
                                {
                                    Output.Success($"Resubmitted {r.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo}");
                                }
                            });
    }

    private async Task<int> ExecuteInteractiveResubmitAsync(
        ResubmitDlqCliCommand cliCommand,
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
            foreach (var error in categoriesResult.Errors)
            {
                Output.Error(error);
            }

            return 1;
        }

        var categories = categoriesResult.Value.Categories;

        if (categories.Count == 0)
        {
            Output.Info("No messages found in DLQ.");
            return 0;
        }

        DlqCategoryDisplay.DisplayTable(categories, categoriesResult.Value.TotalMessageCount,
                                        Output.Info, Output.Table);

        Output.Info("");
        Console.Write("Select categories to resubmit (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        var selectedIndices = CategorySelectionParser.Parse(input, categories.Count);
        if (selectedIndices == null)
        {
            Output.Info("Operation cancelled.");
            return 0;
        }

        if (selectedIndices.Count == 0)
        {
            Output.Warning("No valid categories selected.");
            return 0;
        }

        var selection = CategorySelection.Build(categories, selectedIndices);

        var targetInfo = GetTargetInfo(cliCommand);
        Output.Info($"Resubmitting {selection.SelectedCount} messages from {selection.SelectedCategoryCount} categories{targetInfo}...");

        var resubmitProgress = CreateResubmitProgressReporter();

        var resubmitCommand = new ResubmitDlqMessagesCommand(cliCommand.Namespace,
                                                             target,
                                                             cliCommand.EffectiveTarget,
                                                             cliCommand.BeforeEnqueueTime,
                                                             selection.SelectedKeys,
                                                             resubmitProgress);

        var resubmitResult = await mediator.Send(resubmitCommand, cancellationToken);

        Console.WriteLine();

        return HandleResult(resubmitResult,
                            r =>
                            {
                                Output.Success($"Resubmitted {r.ResubmittedCount} messages from DLQ for {entityDescription}{targetInfo}.");
                            });
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
