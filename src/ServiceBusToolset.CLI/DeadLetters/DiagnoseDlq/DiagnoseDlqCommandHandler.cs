using System.Text.Json;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;

public sealed class DiagnoseDlqCommandHandler(ISender mediator,
                                              IConsoleOutput output) : BaseCommandHandler(output)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<int> ExecuteAsync(DiagnoseDlqCliCommand cliCommand, CancellationToken cancellationToken = default)
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
                                                      Output.Info("Connecting to Application Insights...");
                                                      Output.Verbose($"Connected to App Insights: {cliCommand.AppInsightsResourceId}", cliCommand.Verbose);

                                                      if (cliCommand.Interactive)
                                                      {
                                                          return await ExecuteInteractiveDiagnoseAsync(cliCommand,
                                                                                                       target,
                                                                                                       entityDescription,
                                                                                                       cancellationToken);
                                                      }

                                                      return await ExecuteDiagnoseAsync(cliCommand,
                                                                                        target,
                                                                                        entityDescription,
                                                                                        cancellationToken);
                                                  },
                                                  cliCommand.Verbose);
    }

    private async Task<int> ExecuteDiagnoseAsync(
        DiagnoseDlqCliCommand cliCommand,
        EntityTarget target,
        string entityDescription,
        CancellationToken cancellationToken)
    {
        Output.Info($"Diagnosing DLQ messages for {entityDescription}...");

        var progress = CreateProgressReporter("Peeked {0} messages...");
        var batchProgress = CreateBatchProgressReporter();

        var command = new DiagnoseDlqCommand(cliCommand.Namespace,
                                             target,
                                             cliCommand.AppInsightsResourceId,
                                             cliCommand.MaxMessages,
                                             cliCommand.BeforeEnqueueTime,
                                             null,
                                             progress,
                                             batchProgress);

        var result = await mediator.Send(command, cancellationToken);

        Console.WriteLine();

        return HandleResult(result, r => OutputDiagnoseResults(r, cliCommand));
    }

    private async Task<int> ExecuteInteractiveDiagnoseAsync(
        DiagnoseDlqCliCommand cliCommand,
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
        Console.Write("Select categories to diagnose (comma-separated numbers, 'all', or 'q' to quit): ");
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

        Output.Info($"Diagnosing up to {Math.Min(selection.SelectedCount, cliCommand.MaxMessages)} messages from {selection.SelectedCategoryCount} categories...");

        var progress = CreateProgressReporter("Peeked {0} messages...");
        var batchProgress = CreateBatchProgressReporter();

        var command = new DiagnoseDlqCommand(cliCommand.Namespace,
                                             target,
                                             cliCommand.AppInsightsResourceId,
                                             cliCommand.MaxMessages,
                                             cliCommand.BeforeEnqueueTime,
                                             selection.SelectedKeys,
                                             progress,
                                             batchProgress);

        var result = await mediator.Send(command, cancellationToken);

        Console.WriteLine();

        return HandleResult(result, r => OutputDiagnoseResults(r, cliCommand));
    }

    private void OutputDiagnoseResults(DiagnoseDlqResult result, DiagnoseDlqCliCommand cliCommand)
    {
        if (result.TotalProcessed == 0)
        {
            Output.Info("No messages found matching criteria.");
            return;
        }

        Output.Info($"Queried App Insights for {result.TotalProcessed - result.SkippedNoOperationId} messages (skipped {result.SkippedNoOperationId} without operation ID)");

        // Filter to only results with actual telemetry
        var resultsWithTelemetry = result.Results
                                         .Where(r => r.Exceptions.Count > 0 || r.Traces.Count > 0 || r.FailedDependencies.Count > 0)
                                         .ToList();

        if (resultsWithTelemetry.Count == 0)
        {
            Output.Warning("No telemetry found for any of the diagnosed messages.");
            Output.Info("This could mean:");
            Output.Info("  - The messages were processed by a service not sending telemetry to this App Insights");
            Output.Info("  - The telemetry has been purged (default retention is 90 days)");
            Output.Info("  - The operation IDs don't match the expected format");
            return;
        }

        Output.Success($"Found telemetry for {resultsWithTelemetry.Count} of {result.Results.Count} messages");

        // Print summary to console - grouped by Subject
        PrintDiagnosticSummary(resultsWithTelemetry);

        // Write to file if specified
        if (!string.IsNullOrEmpty(cliCommand.OutputFile))
        {
            var json = JsonSerializer.Serialize(resultsWithTelemetry, JsonOptions);
            File.WriteAllText(cliCommand.OutputFile, json);
            Output.Success($"Full diagnostic results written to '{cliCommand.OutputFile}'");
        }
    }

    private void PrintDiagnosticSummary(IReadOnlyCollection<DiagnosticResult> results)
    {
        Output.Info("");
        Output.Info("Diagnostic Summary by Message Type:");
        Output.Info("====================================");

        // Group by Subject (message type)
        var groupedBySubject = results
                               .GroupBy(r => r.Subject ?? "(none)")
                               .OrderByDescending(g => g.Count());

        foreach (var subjectGroup in groupedBySubject)
        {
            var messageCount = subjectGroup.Count();
            var totalExceptions = subjectGroup.Sum(r => r.Exceptions.Count);

            Output.Info("");
            Output.Info($"[{subjectGroup.Key}] - {messageCount} messages, {totalExceptions} exceptions");
            Output.Info(new string('-', 60));

            // Get exceptions for this subject, grouped by type only
            var exceptionGroups = subjectGroup
                                  .SelectMany(r => r.Exceptions)
                                  .GroupBy(e => e.ExceptionType ?? "(unknown)")
                                  .OrderByDescending(g => g.Count())
                                  .Take(5)
                                  .ToList();

            if (exceptionGroups.Count > 0)
            {
                var headers = new[]
                {
                    "Count",
                    "Exception Type",
                    "Sample Message"
                };
                var rows = exceptionGroups.Select(g => new[]
                {
                    g.Count().ToString(),
                    g.Key,
                    GetExceptionMessage(g.First())
                });
                Output.Table(headers, rows);
            }
            else
            {
                Output.Info("  No exceptions found (check traces/dependencies in output file)");
            }

            // Show failed dependencies if any
            var dependencyGroups = subjectGroup
                                   .SelectMany(r => r.FailedDependencies)
                                   .GroupBy(d => new
                                   {
                                       d.Type,
                                       d.Target
                                   })
                                   .OrderByDescending(g => g.Count())
                                   .Take(3)
                                   .ToList();

            if (dependencyGroups.Count != 0)
            {
                Output.Info("");
                Output.Info("  Failed Dependencies:");
                foreach (var dep in dependencyGroups)
                {
                    Output.Info($"    - [{dep.Count()}x] {dep.Key.Type}: {TruncateString(dep.Key.Target ?? "", 40)}");
                }
            }
        }

        // Overall summary
        Output.Info("");
        Output.Info("Overall Top Exceptions:");
        Output.Info("=======================");

        var allExceptions = results
                            .SelectMany(r => r.Exceptions)
                            .GroupBy(e => e.ExceptionType ?? "(unknown)")
                            .OrderByDescending(g => g.Count())
                            .Take(10)
                            .ToList();

        if (allExceptions.Count > 0)
        {
            var headers = new[]
            {
                "Count",
                "Type",
                "Sample Message"
            };
            var rows = allExceptions.Select(g => new[]
            {
                g.Count().ToString(),
                g.Key,
                GetExceptionMessage(g.First())
            });
            Output.Table(headers, rows);
        }
    }

    private IProgress<(int Current, int Total)> CreateBatchProgressReporter()
    {
        return new Progress<(int Current, int Total)>(batch =>
        {
            Output.Progress($"Querying App Insights batch {batch.Current}/{batch.Total}...");
        });
    }

    private static EntityTarget CreateTarget(DiagnoseDlqCliCommand cliCommand)
        => cliCommand.IsQueueMode
               ? EntityTarget.ForQueue(cliCommand.Queue!)
               : EntityTarget.ForSubscription(cliCommand.Topic!, cliCommand.Subscription!);

    private static string GetExceptionMessage(ExceptionInfo ex)
    {
        // Prefer innermostMessage, fall back to outerMessage
        if (!string.IsNullOrWhiteSpace(ex.InnermostMessage))
        {
            return ex.InnermostMessage;
        }

        return !string.IsNullOrWhiteSpace(ex.OuterMessage)
                   ? ex.OuterMessage
                   : "(no message)";
    }

    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}
