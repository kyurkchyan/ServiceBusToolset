using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset;
using ServiceBusToolset.Application;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.CLI;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.Common.Queues;
using ServiceBusToolset.CLI.DeadLetters.Common;
using ServiceBusToolset.CLI.DeadLetters.DianoseDlq;
using ServiceBusToolset.CLI.DeadLetters.DianoseDlq.AppInsights;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlqMessages;
using ServiceBusToolset.CLI.Queues.MonitorQueues;

var services = new ServiceCollection();
services.AddApplication();
services.AddCli();

using var provider = services.BuildServiceProvider();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await Parser.Default.ParseArguments<PurgeDlqCliCommand, ResubmitDlqCliCommand, DumpDlqCliCommand, DiagnoseDlqCliCommand, MonitorQueuesCliCommand>(args)
                   .MapResult(
                       async (DumpDlqCliCommand opts) =>
                       {
                           using var scope = provider.CreateScope();
                           var handler = scope.ServiceProvider.GetRequiredService<DumpDlqCommandHandler>();
                           return await handler.ExecuteAsync(opts, cts.Token);
                       },
                       (PurgeDlqCliCommand opts) =>
                       {
                           var clientFactory = provider.GetRequiredService<IServiceBusClientFactory>();
                           var output = provider.GetRequiredService<IConsoleOutput>();
                           var categoryAnalyzer = provider.GetRequiredService<IDlqCategoryAnalyzer>();
                           var command = new PurgeDlqCommand(clientFactory, output, categoryAnalyzer);
                           return command.ExecuteAsync(opts, cts.Token);
                       },
                       (ResubmitDlqCliCommand opts) =>
                       {
                           var clientFactory = provider.GetRequiredService<IServiceBusClientFactory>();
                           var output = provider.GetRequiredService<IConsoleOutput>();
                           var categoryAnalyzer = provider.GetRequiredService<IDlqCategoryAnalyzer>();
                           var command = new ResubmitDlqCommand(clientFactory, output, categoryAnalyzer);
                           return command.ExecuteAsync(opts, cts.Token);
                       },
                       (DiagnoseDlqCliCommand opts) =>
                       {
                           var clientFactory = provider.GetRequiredService<IServiceBusClientFactory>();
                           var output = provider.GetRequiredService<IConsoleOutput>();
                           var categoryAnalyzer = provider.GetRequiredService<IDlqCategoryAnalyzer>();
                           var appInsightsService = provider.GetRequiredService<IAppInsightsService>();
                           var command = new DiagnoseDlqCommand(clientFactory, output, categoryAnalyzer, appInsightsService);
                           return command.ExecuteAsync(opts, cts.Token);
                       },
                       (MonitorQueuesCliCommand opts) =>
                       {
                           var queueMonitorService = provider.GetRequiredService<IQueueMonitorService>();
                           var output = provider.GetRequiredService<IConsoleOutput>();
                           var command = new MonitorQueuesCommand(queueMonitorService, output);
                           return command.ExecuteAsync(opts, cts.Token);
                       },
                       errors => Task.FromResult(HandleParseErrors(errors)));

static int HandleParseErrors(IEnumerable<Error> errors)
{
    if (errors.IsHelp() || errors.IsVersion())
    {
        return 0;
    }

    return 1;
}
