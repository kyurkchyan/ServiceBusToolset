using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceBusToolset.TestHarness.Common.Commands;
using ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

namespace ServiceBusToolset.TestHarness;

public sealed class CommandExecutionService(CommandLineArguments cliArguments,
                                            IServiceProvider serviceProvider,
                                            IHostApplicationLifetime hostApplicationLifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = Parser.Default.ParseArguments(cliArguments.Args,
                                                       typeof(GenerateDlqCliCommand));

            await result
                  .WithCommandAsync<GenerateDlqCliCommand>(cmd => HandleCommandAsync(cmd, stoppingToken))
                  .WithNotParsedAsync(HandleParseErrors);
        }
        catch (Exception ex)
        {
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Unhandled error: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            Console.ResetColor();
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
