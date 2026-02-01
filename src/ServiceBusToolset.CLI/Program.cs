using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.Application;
using ServiceBusToolset.CLI;
using ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Queues.MonitorQueues;

var services = new ServiceCollection();
services.AddApplication();
services.AddCli();

await using var provider = services.BuildServiceProvider();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await Parser.Default.ParseArguments<PurgeDlqCliCommand, ResubmitDlqCliCommand, DumpDlqCliCommand, DiagnoseDlqCliCommand, MonitorQueuesCliCommand>(args)
                   .MapResult(async (DumpDlqCliCommand opts) =>
                              {
                                  using var scope = provider.CreateScope();
                                  var handler = scope.ServiceProvider.GetRequiredService<DumpDlqCommandHandler>();
                                  return await handler.ExecuteAsync(opts, cts.Token);
                              },
                              async (PurgeDlqCliCommand opts) =>
                              {
                                  using var scope = provider.CreateScope();
                                  var handler = scope.ServiceProvider.GetRequiredService<PurgeDlqCommandHandler>();
                                  return await handler.ExecuteAsync(opts, cts.Token);
                              },
                              async (ResubmitDlqCliCommand opts) =>
                              {
                                  using var scope = provider.CreateScope();
                                  var handler = scope.ServiceProvider.GetRequiredService<ResubmitDlqCommandHandler>();
                                  return await handler.ExecuteAsync(opts, cts.Token);
                              },
                              async (DiagnoseDlqCliCommand opts) =>
                              {
                                  using var scope = provider.CreateScope();
                                  var handler = scope.ServiceProvider.GetRequiredService<DiagnoseDlqCommandHandler>();
                                  return await handler.ExecuteAsync(opts, cts.Token);
                              },
                              async (MonitorQueuesCliCommand opts) =>
                              {
                                  using var scope = provider.CreateScope();
                                  var handler = scope.ServiceProvider.GetRequiredService<MonitorQueuesCommandHandler>();
                                  return await handler.ExecuteAsync(opts, cts.Token);
                              },
                              errors => Task.FromResult(HandleParseErrors(errors)));

static int HandleParseErrors(IEnumerable<Error> errors)
{
    var enumerable = errors as Error[] ?? errors.ToArray();
    if (enumerable.IsHelp() || enumerable.IsVersion())
    {
        return 0;
    }

    return 1;
}
