using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceBusToolset.TestHarness;
using ServiceBusToolset.TestHarness.Common.Commands;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

builder.Services
       .AddSingleton(new CommandLineArguments(args))
       .AddTestHarness()
       .AddCommandHandlers()
       .AddHostedService<CommandExecutionService>();

await builder.Build().RunAsync();
