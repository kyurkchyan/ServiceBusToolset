using System.Reactive.Linq;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Models;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;
using Spectre.Console;

namespace ServiceBusToolset.Commands;

public class MonitorQueuesCommand(IQueueMonitorService monitorService, IConsoleOutput output) : ICommand<MonitorQueuesOptions>
{
    public async Task<int> ExecuteAsync(MonitorQueuesOptions options, CancellationToken cancellationToken = default)
    {
        var validationError = options.Validate();
        if (validationError != null)
        {
            output.Error(validationError);
            return 1;
        }

        try
        {
            output.Info($"Connecting to Service Bus namespace: {options.Namespace}");
            if (!string.IsNullOrEmpty(options.Filter))
            {
                output.Info($"Filter: {options.Filter}");
            }

            output.Info($"Refresh interval: {options.RefreshInterval} seconds");
            output.Info("Press Ctrl+C to stop monitoring.");
            output.Info("");

            var refreshInterval = TimeSpan.FromSeconds(options.RefreshInterval);

            await monitorService
                  .ObserveQueues(options.Namespace,
                                 options.Filter,
                                 refreshInterval,
                                 cancellationToken)
                  .ForEachAsync(stats =>
                                {
                                    Console.Clear();
                                    var table = CreateTable(stats);
                                    AnsiConsole.Write(table);

                                    if (options.Verbose)
                                    {
                                        output.Verbose($"Updated at {DateTimeOffset.Now:HH:mm:ss} - {stats.Count} queues", options.Verbose);
                                    }
                                },
                                cancellationToken);

            return 0;
        }
        catch (AuthenticationFailedException ex)
        {
            output.Error($"Authentication failed: {ex.Message}");
            output.Error("Ensure you are logged in with 'az login' or have valid environment credentials.");
            return 1;
        }
        catch (ServiceBusException ex)
        {
            output.Error($"Service Bus error: {ex.Message}");
            output.Verbose($"Reason: {ex.Reason}", options.Verbose);
            return 1;
        }
        catch (OperationCanceledException)
        {
            output.Info("");
            output.Info("Monitoring stopped.");
            return 0;
        }
    }

    private static Table CreateTable(IReadOnlyList<QueueStatistics> statistics)
    {
        var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold blue]Service Bus Queue Monitor[/]")
                    .AddColumn(new TableColumn("[bold]Queue Name[/]").LeftAligned())
                    .AddColumn(new TableColumn("[bold]Active[/]").RightAligned())
                    .AddColumn(new TableColumn("[bold]DLQ[/]").RightAligned())
                    .AddColumn(new TableColumn("[bold]Scheduled[/]").RightAligned());

        long totalActive = 0;
        long totalDlq = 0;
        long totalScheduled = 0;

        foreach (var stat in statistics)
        {
            var activeStyle = stat.ActiveMessageCount > 1000 ? "[yellow]" : "[white]";
            var dlqStyle = stat.DeadLetterMessageCount > 0 ? "[red]" : "[white]";

            table.AddRow(stat.Name,
                         $"{activeStyle}{stat.ActiveMessageCount:N0}[/]",
                         $"{dlqStyle}{stat.DeadLetterMessageCount:N0}[/]",
                         $"[white]{stat.ScheduledMessageCount:N0}[/]");

            totalActive += stat.ActiveMessageCount;
            totalDlq += stat.DeadLetterMessageCount;
            totalScheduled += stat.ScheduledMessageCount;
        }

        if (statistics.Count > 0)
        {
            table.AddEmptyRow();

            var totalActiveStyle = totalActive > 1000 ? "[bold yellow]" : "[bold white]";
            var totalDlqStyle = totalDlq > 0 ? "[bold red]" : "[bold white]";

            table.AddRow("[bold]TOTAL[/]",
                         $"{totalActiveStyle}{totalActive:N0}[/]",
                         $"{totalDlqStyle}{totalDlq:N0}[/]",
                         $"[bold white]{totalScheduled:N0}[/]");
        }

        var timestamp = statistics.Count > 0 ? statistics[0].UpdatedAt : DateTimeOffset.Now;
        table.Caption($"Last updated: {timestamp:HH:mm:ss}");

        return table;
    }
}
