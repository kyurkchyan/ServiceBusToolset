using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.CLI.Common.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

public static class DlqScanSessionExtensions
{
    private const int TableChromeLines = 12;

    /// <summary>
    /// Run the interactive dead-letter queue (DLQ) scanning UI for a session and handle user input to control scrolling and stop the scan.
    /// </summary>
    /// <param name="session">The DLQ scan session providing snapshots, scan lifecycle, and schema information.</param>
    /// <param name="output">Console output handlers used to render the UI and read user input.</param>
    /// <param name="entityDescription">A short description of the entity being scanned, shown in the UI header.</param>
    /// <returns>A task that completes after the scanning phase finishes and the session has been stopped.</returns>
    public static async Task RunScanningPhaseAsync(
        this DlqScanSession session,
        IConsoleOutput output,
        string entityDescription)
    {
        DlqCategorySnapshot latestSnapshot = new([],
                                                 0,
                                                 false,
                                                 Schema:session.Schema);
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

    /// <summary>
    /// Displays available DLQ categories, prompts the user to choose one or more categories, and returns the selected messages for interactive processing.
    /// </summary>
    /// <param name="session">The current DLQ scan session providing caches, schema, resolver and snapshot access.</param>
    /// <param name="output">Console output and input handlers used to render the table and read user input.</param>
    /// <param name="mergeSimilar">If true, collapse similar categories together before presenting choices.</param>
    /// <param name="beforeTime">If provided, only include messages with timestamps earlier than this value.</param>
    /// <param name="actionVerb">Label used in the prompt describing the action (e.g., "inspect", "reprocess").</param>
    /// <returns>
    /// An InteractiveCategorySelection containing the messages and the number of selected categories, or null if the operation was cancelled,
    /// no categories exist, no valid categories were selected, or no messages match the selected categories.
    /// </returns>
    public static InteractiveCategorySelection? GetCategorySelection(
        this DlqScanSession session,
        IConsoleOutput output,
        bool mergeSimilar,
        DateTimeOffset? beforeTime,
        string actionVerb)
    {
        var finalSnapshot = DlqCategoryScanner.BuildCategorySnapshot(session.Cache,
                                                                     mergeSimilar,
                                                                     session.Schema,
                                                                     session.Resolver);

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
                                        output.Table,
                                        session.Schema);

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

    /// <summary>
    /// Constructs a renderable view representing the current DLQ scanning state for the specified entity.
    /// </summary>
    /// <param name="snapshot">Current DLQ category snapshot containing categories, totals, and schema.</param>
    /// <param name="entityDescription">Human-readable description of the entity being scanned shown in the header.</param>
    /// <param name="totalDlqCount">Optional overall DLQ message count used to display "peeked from" information when available.</param>
    /// <param name="scrollOffset">Zero-based row offset used to determine which table rows are visible (scroll position).</param>
    /// <returns>An <see cref="IRenderable"/> that displays either a scanning message when no categories exist or a paged table of category rows with totals and scrolling hints.</returns>
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

        var (headers, allRows) = DlqCategoryDisplay.GenerateTableData(snapshot.Categories, snapshot.Schema);
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
