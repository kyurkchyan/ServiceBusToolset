using System.Reactive.Linq;
using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions;
using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions.Models;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Extensions;
using ServiceBusToolset.CLI.Common.Logging;
using Spectre.Console;
using Unit = Mediator.Unit;

namespace ServiceBusToolset.CLI.Subscriptions.MonitorSubscriptions;

public sealed class MonitorSubscriptionsCommandHandler(ISender mediator, IConsoleOutput output)
    : BaseCommandHandler<MonitorSubscriptionsCliCommand, Unit>(output)
{
    protected override async Task<Result<Unit>> ExecuteCoreAsync(
        MonitorSubscriptionsCliCommand command,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        Output.Info($"Connecting to Service Bus namespace: {command.Namespace}");
        if (!string.IsNullOrEmpty(command.TopicFilter))
        {
            Output.Info($"Topic filter: {command.TopicFilter}");
        }

        if (!string.IsNullOrEmpty(command.SubscriptionFilter))
        {
            Output.Info($"Subscription filter: {command.SubscriptionFilter}");
        }

        Output.Info($"Refresh interval: {command.RefreshInterval} seconds");
        Output.Info("Press Ctrl+C to stop monitoring.");
        Output.Info("");

        var refreshInterval = TimeSpan.FromSeconds(command.RefreshInterval);

        var mediatorCommand = new MonitorSubscriptionsCommand(command.Namespace,
                                                              command.TopicFilter,
                                                              command.SubscriptionFilter,
                                                              refreshInterval,
                                                              cancellationToken);

        var result = await mediator.Send(mediatorCommand, cancellationToken);

        if (!result.IsSuccess)
        {
            return result.ToErrorResult<Unit>();
        }

        try
        {
            await result.Value.SubscriptionStatistics.ForEachAsync(stats =>
                                                                   {
                                                                       Console.Clear();
                                                                       var table = CreateTable(stats);
                                                                       AnsiConsole.Write(table);

                                                                       if (verbose)
                                                                       {
                                                                           Output.Verbose($"Updated at {DateTimeOffset.Now:HH:mm:ss} - {stats.Count} subscriptions",
                                                                                          verbose);
                                                                       }
                                                                   },
                                                                   cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Output.Info("");
            Output.Info("Monitoring stopped.");
        }

        return Result.Success(Unit.Value);
    }

    private static Table CreateTable(IReadOnlyList<SubscriptionStatistics> statistics)
    {
        var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold blue]Service Bus Subscription Monitor[/]")
                    .AddColumn(new TableColumn("[bold]Topic[/]").LeftAligned())
                    .AddColumn(new TableColumn("[bold]Subscription[/]").LeftAligned())
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

            table.AddRow(stat.TopicName,
                         stat.SubscriptionName,
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
                         "",
                         $"{totalActiveStyle}{totalActive:N0}[/]",
                         $"{totalDlqStyle}{totalDlq:N0}[/]",
                         $"[bold white]{totalScheduled:N0}[/]");
        }

        var timestamp = statistics.Count > 0 ? statistics[0].UpdatedAt : DateTimeOffset.Now;
        table.Caption($"Last updated: {timestamp:HH:mm:ss}");

        return table;
    }
}
