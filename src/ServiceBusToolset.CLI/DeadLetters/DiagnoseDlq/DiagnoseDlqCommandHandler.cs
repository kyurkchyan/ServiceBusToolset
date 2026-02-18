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
        var streamCommand = new StreamDlqCommand(cliCommand.Namespace, target, cliCommand.MergeSimilar);
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

        // Phase 2: Selection & Diagnose
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

        Console.Write("\nSelect categories to diagnose (comma-separated numbers, 'all', or 'q' to quit): ");
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
        var messagesToDiagnose = session.SnapshotForCategories(effectiveKeys, cliCommand.BeforeEnqueueTime);

        if (messagesToDiagnose.Count == 0)
        {
            Output.Info("No messages match the selected categories.");
            return Result.Success(Unit.Value);
        }

        Output.Info($"Diagnosing up to {Math.Min(messagesToDiagnose.Count, cliCommand.MaxMessages)} messages from {selection.SelectedCategoryCount} categories...");

        var batchProgress = CreateBatchProgressReporter();

        var diagnoseCommand = new DiagnoseFromCacheCommand(cliCommand.AppInsightsResourceId,
                                                           cliCommand.MaxMessages,
                                                           messagesToDiagnose,
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
