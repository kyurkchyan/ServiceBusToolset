using CommandLine;
using ServiceBusToolset.Commands;
using ServiceBusToolset.Options;
using ServiceBusToolset.Services;

var clientFactory = new ServiceBusClientFactory();
var output = new ConsoleOutput();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await Parser.Default.ParseArguments<PurgeDlqOptions>(args)
                   .MapResult(async opts =>
                              {
                                  var command = new PurgeDlqCommand(clientFactory, output);
                                  return await command.ExecuteAsync(opts, cts.Token);
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
