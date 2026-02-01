using System.Text.Json;
using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.Application.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using Unit = Mediator.Unit;

namespace ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;

public sealed class DiagnoseDlqCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler<DiagnoseDlqCliCommand, Unit>(output)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    protected override async Task<Result<Unit>> ExecuteCoreAsync(
        DiagnoseDlqCliCommand command,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        var target = CreateTarget(command);
        var entityDescription = target.GetDescription();

        Output.Info("Connecting to Application Insights...");
        Output.Verbose($"Connected to App Insights: {command.AppInsightsResourceId}", verbose);

        if (command.Interactive)
        {
            return await ExecuteInteractiveDiagnoseAsync(command,
                                                         target,
                                                         entityDescription,
                                                         cancellationToken);
        }

        return await ExecuteDiagnoseAsync(command,
                                          target,
                                          entityDescription,
                                          cancellationToken);
    }

    private async Task<Result<Unit>> ExecuteDiagnoseAsync(
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

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        OutputDiagnoseResults(result.Value, cliCommand);
        return Result.Success(Unit.Value);
    }

    private async Task<Result<Unit>> ExecuteInteractiveDiagnoseAsync(
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
        Console.Write("Select categories to diagnose (comma-separated numbers, 'all', or 'q' to quit): ");
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

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        OutputDiagnoseResults(result.Value, cliCommand);
        return Result.Success(Unit.Value);
    }

    private void OutputDiagnoseResults(DiagnoseDlqResult result, DiagnoseDlqCliCommand cliCommand)
    {
        if (result.TotalProcessed == 0)
        {
            Output.Info("No messages found matching criteria.");
            return;
        }

        Output.Info($"Queried App Insights for {result.TotalProcessed - result.SkippedNoOperationId} messages (skipped {result.SkippedNoOperationId} without operation ID)");

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

        PrintDiagnosticSummary(resultsWithTelemetry);

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
