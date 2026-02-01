using System.Reactive.Linq;
using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Queues.MonitorQueues;
using ServiceBusToolset.Application.Queues.MonitorQueues.Models;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;
using Spectre.Console;

namespace ServiceBusToolset.CLI.Queues.MonitorQueues;

public sealed class MonitorQueuesCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler(output)
{
    public async Task<int> ExecuteAsync(MonitorQueuesCliCommand cliCommand, CancellationToken cancellationToken = default)
    {
        var validationError = cliCommand.Validate();
        if (validationError != null)
        {
            Output.Error(validationError);
            return 1;
        }

        return await ExecuteWithExceptionHandling(async () =>
                                                  {
                                                      Output.Info($"Connecting to Service Bus namespace: {cliCommand.Namespace}");
                                                      if (!string.IsNullOrEmpty(cliCommand.Filter))
                                                      {
                                                          Output.Info($"Filter: {cliCommand.Filter}");
                                                      }

                                                      Output.Info($"Refresh interval: {cliCommand.RefreshInterval} seconds");
                                                      Output.Info("Press Ctrl+C to stop monitoring.");
                                                      Output.Info("");

                                                      var refreshInterval = TimeSpan.FromSeconds(cliCommand.RefreshInterval);

                                                      var command = new MonitorQueuesCommand(cliCommand.Namespace,
                                                                                             cliCommand.Filter,
                                                                                             refreshInterval,
                                                                                             cancellationToken);

                                                      var result = await mediator.Send(command, cancellationToken);

                                                      return HandleResult(result,
                                                                          async r =>
                                                                          {
                                                                              try
                                                                              {
                                                                                  await r.QueueStatistics.ForEachAsync(stats =>
                                                                                                                       {
                                                                                                                           Console.Clear();
                                                                                                                           var table = CreateTable(stats);
                                                                                                                           AnsiConsole.Write(table);

                                                                                                                           if (cliCommand.Verbose)
                                                                                                                           {
                                                                                                                               Output.Verbose($"Updated at {DateTimeOffset.Now:HH:mm:ss} - {stats.Count} queues",
                                                                                                                                              cliCommand.Verbose);
                                                                                                                           }
                                                                                                                       },
                                                                                                                       cancellationToken);
                                                                              }
                                                                              catch (OperationCanceledException)
                                                                              {
                                                                                  Output.Info("");
                                                                                  Output.Info("Monitoring stopped.");
                                                                              }
                                                                          });
                                                  },
                                                  cliCommand.Verbose);
    }

    private int HandleResult(Result<MonitorQueuesResult> result, Func<MonitorQueuesResult, Task> onSuccess)
    {
        if (result.IsSuccess)
        {
            onSuccess(result.Value).GetAwaiter().GetResult();
            return 0;
        }

        foreach (var error in result.Errors)
        {
            Output.Error(error);
        }

        return 1;
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
