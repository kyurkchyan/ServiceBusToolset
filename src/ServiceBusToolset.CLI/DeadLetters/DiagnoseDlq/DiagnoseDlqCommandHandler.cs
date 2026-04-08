using System.Text.Json;
using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.DeadLetters.Common;
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

        if (!string.IsNullOrEmpty(command.AppInsightsResourceId))
        {
            Output.Info("Connecting to Application Insights...");
            Output.Verbose($"App Insights resource: {command.AppInsightsResourceId}", verbose);
        }
        else
        {
            Output.Warning("No App Insights resource specified — basic diagnostic mode (dead letter reasons only).");
        }

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

    /// <summary>
    /// Runs an interactive DLQ diagnosis session: streams messages using the provided categorization schema, lets the user select categories to diagnose, and performs diagnosis on the selected cached messages.
    /// </summary>
    /// <param name="cliCommand">CLI options that control the diagnose run (namespace, categorization, merge behavior, App Insights resource, time filter, and output file).</param>
    /// <param name="target">The queue or subscription DLQ target to diagnose.</param>
    /// <param name="entityDescription">Human-readable description of the target used for console output.</param>
    /// <param name="cancellationToken">Token to cancel the interactive diagnose operation.</param>
    /// <returns>`Result<Unit>` containing success when diagnosis completed or was canceled by the user selection flow, or an error result when an underlying operation failed.</returns>
    private async Task<Result<Unit>> ExecuteInteractiveDiagnoseAsync(
        DiagnoseDlqCliCommand cliCommand,
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
                                                     "diagnose");
        if (selection == null)
        {
            return Result.Success(Unit.Value);
        }

        Output.Info($"Diagnosing {selection.Messages.Count} messages from {selection.SelectedCategoryCount} categories...");

        var batchProgress = CreateBatchProgressReporter();

        var diagnoseCommand = new DiagnoseFromCacheCommand(cliCommand.AppInsightsResourceId,
                                                           selection.Messages,
                                                           batchProgress);

        var result = await mediator.Send(diagnoseCommand, cancellationToken);

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

        var basicMode = string.IsNullOrEmpty(cliCommand.AppInsightsResourceId);

        if (basicMode)
        {
            Output.Info($"Analyzed {result.TotalProcessed} messages (basic mode — no App Insights)");
            PrintBasicDiagnosticSummary(result.Results);

            if (!string.IsNullOrEmpty(cliCommand.OutputFile))
            {
                var json = JsonSerializer.Serialize(result.Results, JsonOptions);
                File.WriteAllText(cliCommand.OutputFile, json);
                Output.Success($"Full diagnostic results written to '{cliCommand.OutputFile}'");
            }

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

    private void PrintBasicDiagnosticSummary(IReadOnlyCollection<DiagnosticResult> results)
    {
        Output.Info("");
        Output.Info("Basic Diagnostic Summary (Dead Letter Reasons):");
        Output.Info("================================================");

        var byReason = results
                       .GroupBy(r => r.DeadLetterReason ?? "(none)")
                       .OrderByDescending(g => g.Count())
                       .ToList();

        var headers = new[]
        {
            "Count",
            "Dead Letter Reason",
            "Subjects (sample)"
        };
        var rows = byReason.Select(g =>
        {
            var subjects = g
                           .Select(r => r.Subject ?? "(none)")
                           .Where(s => s != "(none)")
                           .Distinct()
                           .Take(3)
                           .ToList();
            var subjectSample = subjects.Count > 0
                                    ? string.Join(", ", subjects)
                                    : "(none)";
            return new[]
            {
                g.Count().ToString(),
                g.Key,
                subjectSample
            };
        });

        Output.Table(headers, rows);
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
