using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Queues.MonitorQueues;

namespace ServiceBusToolset.CLI;

public sealed class CommandExecutionService(CommandLineArguments cliArguments,
                                            IServiceProvider serviceProvider,
                                            IHostApplicationLifetime hostApplicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = Parser.Default.ParseArguments<
                DumpDlqCliCommand,
                PurgeDlqCliCommand,
                ResubmitDlqCliCommand,
                DiagnoseDlqCliCommand,
                MonitorQueuesCliCommand>(cliArguments.Args);

            await result
                  .WithCommandAsync<DumpDlqCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithCommandAsync<PurgeDlqCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithCommandAsync<ResubmitDlqCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithCommandAsync<DiagnoseDlqCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithCommandAsync<MonitorQueuesCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithNotParsedAsync(HandleParseErrors);
        }
        catch (Exception)
        {
            Environment.ExitCode = 1;
        }
        finally
        {
            hostApplicationLifetime.StopApplication();
        }
    }

    private async Task HandleCommandAsync<TCommand>(TCommand command, CancellationToken ct)
        where TCommand : class, ICliCommand
    {
        using var scope = serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand>>();
        var exitCode = await handler.ExecuteAsync(command, command.Verbose, ct);
        Environment.ExitCode = exitCode;
    }

    private static int HandleParseErrors(IEnumerable<Error> errors)
    {
        var enumerable = errors as Error[] ?? errors.ToArray();
        if (enumerable.IsHelp() || enumerable.IsVersion())
        {
            return 0;
        }

        return 1;
    }
}
