using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

public static class DlqScanSessionExtensions
{
    public static async Task RunScanningPhaseAsync(
        this DlqScanSession session,
        IConsoleOutput output,
        string entityDescription)
    {
        using (session.CategoryStream.Subscribe(snapshot =>
               {
                   Console.Clear();
                   RenderScanningView(output,
                                      snapshot,
                                      entityDescription,
                                      session.TotalDlqCount);
               }))
        {
            var scanTask = session.ScanCompletion.Task;
            var keyTask = Task.Run(() => WaitForStopKey(session.ScanCancellationToken));

            await Task.WhenAny(scanTask, keyTask);
            session.StopScanning();
            await scanTask;
        }
    }

    public static InteractiveCategorySelection? GetCategorySelection(
        this DlqScanSession session,
        IConsoleOutput output,
        bool mergeSimilar,
        DateTimeOffset? beforeTime,
        string actionVerb)
    {
        var finalSnapshot = DlqCategoryScanner.BuildCategorySnapshot(session.Cache, mergeSimilar);

        if (session.Error != null)
        {
            output.Error($"Error while scanning DLQ: {session.Error.Message}");
        }

        if (finalSnapshot.Categories.Count == 0)
        {
            output.Info("No messages found in DLQ.");
            return null;
        }

        Console.Clear();
        DlqCategoryDisplay.DisplayTable(finalSnapshot.Categories,
                                        finalSnapshot.TotalMessageCount,
                                        output.Info,
                                        output.Table);

        Console.Write($"\nSelect categories to {actionVerb} (comma-separated numbers, 'all', or 'q' to quit): ");
        var input = output.ReadLine();

        var selectedIndices = CategorySelectionParser.Parse(input, finalSnapshot.Categories.Count);
        if (selectedIndices == null)
        {
            output.Info("Operation cancelled.");
            return null;
        }

        if (selectedIndices.Count == 0)
        {
            output.Warning("No valid categories selected.");
            return null;
        }

        var selection = CategorySelection.Build(finalSnapshot.Categories, selectedIndices);
        var effectiveKeys = finalSnapshot.MergeResult?.ExpandKeys(selection.SelectedKeys)
                            ?? selection.SelectedKeys;
        var messages = session.SnapshotForCategories(effectiveKeys, beforeTime);

        if (messages.Count == 0)
        {
            output.Info("No messages match the selected categories.");
            return null;
        }

        return new InteractiveCategorySelection(messages, selection.SelectedCategoryCount);
    }

    private static void RenderScanningView(
        IConsoleOutput output,
        DlqCategorySnapshot snapshot,
        string entityDescription,
        long? totalDlqCount)
    {
        var peekedInfo = totalDlqCount.HasValue
                             ? $"Peeked {snapshot.TotalMessageCount} from {totalDlqCount.Value}"
                             : $"{snapshot.TotalMessageCount} messages found so far";

        if (snapshot.Categories.Count == 0)
        {
            output.Info($"Scanning DLQ for {entityDescription}... {peekedInfo}");
            output.Info("Press 'x' to stop scanning and select categories");
            return;
        }

        DlqCategoryDisplay.DisplayTable(snapshot.Categories,
                                        snapshot.TotalMessageCount,
                                        output.Info,
                                        output.Table);

        output.Info($"Scanning... {peekedInfo}");
        output.Info("Press 'x' to stop scanning and select categories");
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
}
