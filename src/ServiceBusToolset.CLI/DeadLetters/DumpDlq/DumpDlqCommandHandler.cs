using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.DumpDlq;

public sealed class DumpDlqCommandHandler(ISender mediator,
                                          IConsoleOutput output) : BaseCommandHandler(output)
{
    public async Task<int> ExecuteAsync(DumpDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
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
                                                          return await ExecuteInteractiveDumpAsync(cliCommand,
                                                                                                   target,
                                                                                                   entityDescription,
                                                                                                   cancellationToken);
                                                      }

                                                      return await ExecuteDumpAsync(cliCommand,
                                                                                    target,
                                                                                    entityDescription,
                                                                                    cancellationToken);
                                                  },
                                                  cliCommand.Verbose);
    }

    private async Task<int> ExecuteDryRunAsync(
        DumpDlqCliCommand cliCommand,
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

        var countDlqCommand = new CountDlqMessagesCommand(cliCommand.Namespace,
                                                  target,
                                                  cliCommand.BeforeEnqueueTime,
                                                  progress);

        var result = await mediator.Send(countDlqCommand, cancellationToken);

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

    private async Task<int> ExecuteDumpAsync(
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

        return HandleResult(result,
                            r =>
                            {
                                if (r.MessageCount == 0)
                                {
                                    Output.Info("No messages found matching criteria.");
                                }
                                else
                                {
                                    Output.Success($"Dumped {r.MessageCount} messages to '{r.OutputFilePath}'");
                                }
                            });
    }

    private async Task<int> ExecuteInteractiveDumpAsync(
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

        DisplayCategoryTable(categories, categoriesResult.Value.TotalMessageCount);

        Output.Info("");
        Console.Write("Select categories to dump (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = Output.ReadLine();

        var selectedIndices = ParseSelection(input, categories.Count);
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

        var selectedCategories = new HashSet<DlqCategoryKey>();
        var totalToDump = 0;
        foreach (var cat in selectedIndices.Select(idx => categories[idx]))
        {
            selectedCategories.Add(new DlqCategoryKey(cat.Label, cat.DeadLetterReason));
            totalToDump += cat.Count;
        }

        Output.Info($"Dumping {totalToDump} messages from {selectedIndices.Count} categories...");

        var dumpProgress = CreateProgressReporter("Peeked {0} messages...");

        var dumpCommand = new DumpDlqMessagesCommand(cliCommand.Namespace,
                                                     target,
                                                     cliCommand.OutputFile!,
                                                     cliCommand.BeforeEnqueueTime,
                                                     selectedCategories,
                                                     dumpProgress);

        var dumpResult = await mediator.Send(dumpCommand, cancellationToken);

        Console.WriteLine();

        return HandleResult(dumpResult,
                            r =>
                            {
                                Output.Success($"Dumped {r.MessageCount} messages to '{r.OutputFilePath}'");
                            });
    }

    private void DisplayCategoryTable(IEnumerable<DlqCategory> categories, int totalCount)
    {
        Output.Info("");
        Output.Info("Dead Letter Summary:");

        var headers = new[]
        {
            "#",
            "Label",
            "DeadLetterReason",
            "Count"
        };
        var rows = categories.Select((cat, index) => new[]
        {
            (index + 1).ToString(),
            cat.Label.ReplaceLineEndings(" "),
            cat.DeadLetterReason.ReplaceLineEndings(" "),
            cat.Count.ToString()
        });

        Output.Table(headers, rows);
        Output.Info($"Total: {totalCount} messages");
    }

    private static List<int>? ParseSelection(string? input, int maxIndex)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().ToLowerInvariant();

        switch (trimmed)
        {
            case "q":
            case "quit":
                return null;
            case "all":
            case "a":
                return Enumerable.Range(0, maxIndex).ToList();
        }

        var indices = new List<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var rangeParts = part.Split('-', 2);
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end))
                {
                    for (var i = start; i <= end; i++)
                    {
                        var idx = i - 1;
                        if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                        {
                            indices.Add(idx);
                        }
                    }
                }
            }
            else if (int.TryParse(part, out var num))
            {
                var idx = num - 1;
                if (idx >= 0 && idx < maxIndex && !indices.Contains(idx))
                {
                    indices.Add(idx);
                }
            }
        }

        return indices;
    }

    private static EntityTarget CreateTarget(DumpDlqCliCommand cliCommand)
    {
        if (cliCommand.IsQueueMode)
        {
            return EntityTarget.ForQueue(cliCommand.Queue!);
        }

        return EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);
    }
}
