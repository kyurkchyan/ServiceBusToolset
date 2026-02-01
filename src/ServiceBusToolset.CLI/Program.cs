using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceBusToolset.Application;
using ServiceBusToolset.CLI;
using ServiceBusToolset.CLI.Common.Commands;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

builder.Services
       .AddSingleton(new CommandLineArguments(args))
       .AddApplication()
       .AddCli()
       .AddCommandHandlers()
       .AddHostedService<CommandExecutionService>();

await builder.Build().RunAsync();
