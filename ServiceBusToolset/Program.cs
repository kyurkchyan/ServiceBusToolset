using CommandLine;
using ServiceBusToolset.Commands;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;

var clientFactory = new ServiceBusClientFactory();
var output = new ConsoleOutput();
var categoryAnalyzer = new DlqCategoryAnalyzer();
var appInsightsService = new AppInsightsService();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await Parser.Default.ParseArguments<PurgeDlqOptions, ResubmitDlqOptions, DumpDlqOptions, DiagnoseDlqOptions>(args)
                   .MapResult((PurgeDlqOptions opts) =>
                              {
                                  var command = new PurgeDlqCommand(clientFactory, output, categoryAnalyzer);
                                  return command.ExecuteAsync(opts, cts.Token);
                              },
                              (ResubmitDlqOptions opts) =>
                              {
                                  var command = new ResubmitDlqCommand(clientFactory, output, categoryAnalyzer);
                                  return command.ExecuteAsync(opts, cts.Token);
                              },
                              (DumpDlqOptions opts) =>
                              {
                                  var command = new DumpDlqCommand(clientFactory, output, categoryAnalyzer);
                                  return command.ExecuteAsync(opts, cts.Token);
                              },
                              (DiagnoseDlqOptions opts) =>
                              {
                                  var command = new DiagnoseDlqCommand(clientFactory,
                                                                       output,
                                                                       categoryAnalyzer,
                                                                       appInsightsService);
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
