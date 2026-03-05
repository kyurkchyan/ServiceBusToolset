using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.CLI.Common.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

public static class DlqScanSessionExtensions
{
    private const int TableChromeLines = 12;

    public static async Task RunScanningPhaseAsync(
        this DlqScanSession session,
        IConsoleOutput output,
        string entityDescription)
    {
        DlqCategorySnapshot latestSnapshot = new([], 0, false);
        var scrollOffset = 0;

        await AnsiConsole.Live(new Text("Initializing..."))
                         .AutoClear(true)
                         .StartAsync(async ctx =>
                         {
                             void Refresh()
                             {
                                 var renderable = BuildScanningRenderable(latestSnapshot,
                                                                          entityDescription,
                                                                          session.TotalDlqCount,
                                                                          scrollOffset);
                                 ctx.UpdateTarget(renderable);
                                 ctx.Refresh();
                             }

                             using (session.CategoryStream.Subscribe(snapshot =>
                                    {
                                        latestSnapshot = snapshot;
                                        Refresh();
                                    }))
                             {
                                 var scanTask = session.ScanCompletion.Task;
                                 var keyTask = Task.Run(() =>
                                 {
                                     if (Console.IsInputRedirected)
                                     {
                                         session.ScanCancellationToken.WaitHandle.WaitOne();
                                         return;
                                     }

                                     while (!session.ScanCancellationToken.IsCancellationRequested)
                                     {
                                         if (Console.KeyAvailable)
                                         {
                                             var key = Console.ReadKey(true);
                                             if (key.KeyChar is 'x' or 'X')
                                             {
                                                 return;
                                             }

                                             var totalRows = latestSnapshot.Categories.Count;
                                             var maxVisible = Math.Max(1, Console.WindowHeight - TableChromeLines);
                                             var maxOffset = Math.Max(0, totalRows - maxVisible);

                                             var newOffset = key switch
                                             {
                                                 { Key: ConsoleKey.UpArrow, Modifiers: ConsoleModifiers.Shift } =>
                                                     Math.Max(0, scrollOffset - maxVisible),
                                                 { Key: ConsoleKey.DownArrow, Modifiers: ConsoleModifiers.Shift } =>
                                                     Math.Min(maxOffset, scrollOffset + maxVisible),
                                                 { Key: ConsoleKey.UpArrow } =>
                                                     Math.Max(0, scrollOffset - 1),
                                                 { Key: ConsoleKey.DownArrow } =>
                                                     Math.Min(maxOffset, scrollOffset + 1),
                                                 _ => scrollOffset
                                             };

                                             if (newOffset != scrollOffset)
                                             {
                                                 scrollOffset = newOffset;
                                                 Refresh();
                                             }
                                         }

                                         Thread.Sleep(50);
                                     }
                                 });

                                 await Task.WhenAny(scanTask, keyTask);
                                 session.StopScanning();
                                 await scanTask;
                             }
                         });
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

    private static IRenderable BuildScanningRenderable(
        DlqCategorySnapshot snapshot,
        string entityDescription,
        long? totalDlqCount,
        int scrollOffset)
    {
        var peekedInfo = totalDlqCount.HasValue
                             ? $"Peeked {snapshot.TotalMessageCount} from {totalDlqCount.Value}"
                             : $"{snapshot.TotalMessageCount} messages found so far";

        if (snapshot.Categories.Count == 0)
        {
            return new Rows(new Text($"Scanning DLQ for {entityDescription}... {peekedInfo}"),
                            new Markup("[dim]Press 'x' to stop scanning and select categories[/]"));
        }

        var (headers, allRows) = DlqCategoryDisplay.GenerateTableData(snapshot.Categories);
        var rowList = allRows.ToList();

        var maxVisible = Math.Max(1, Console.WindowHeight - TableChromeLines);
        var clampedOffset = Math.Clamp(scrollOffset, 0, Math.Max(0, rowList.Count - maxVisible));
        var visibleRows = rowList.Skip(clampedOffset).Take(maxVisible);

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.Expand();

        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn(header) { NoWrap = false });
        }

        foreach (var row in visibleRows)
        {
            table.AddRow(row.Select(Markup.Escape).ToArray());
        }

        var elements = new List<IRenderable>();
        elements.Add(new Text("Dead Letter Summary:"));

        if (clampedOffset > 0)
        {
            elements.Add(new Markup($"[dim]  ▲ {clampedOffset} more rows above[/]"));
        }

        elements.Add(table);

        var remainingBelow = rowList.Count - clampedOffset - maxVisible;
        if (remainingBelow > 0)
        {
            elements.Add(new Markup($"[dim]  ▼ {remainingBelow} more rows below[/]"));
        }

        elements.Add(new Text($"Total: {snapshot.TotalMessageCount} messages"));
        elements.Add(new Text($"Scanning... {peekedInfo}"));
        elements.Add(new Markup("[dim]↑/↓ scroll  Shift+↑/↓ page  x to stop[/]"));

        return new Rows(elements);
    }
}
