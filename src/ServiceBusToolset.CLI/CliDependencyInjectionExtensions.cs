using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.Common.Queues;
using ServiceBusToolset.CLI.Common.ServiceBus;
using ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;
using IServiceBusClientFactory = ServiceBusToolset.Application.Common.ServiceBus.Abstractions.IServiceBusClientFactory;

namespace ServiceBusToolset.CLI;

public static class CliDependencyInjectionExtensions
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        // Infrastructure services
        services.AddSingleton<IServiceBusClientFactory, ServiceBusClientFactory>();
        services.AddSingleton<IConsoleOutput, ConsoleOutput>();
        services.AddSingleton<IQueueMonitorService, QueueMonitorService>();

        // Command handlers
        services.AddScoped<DumpDlqCommandHandler>();
        services.AddScoped<DiagnoseDlqCommandHandler>();
        services.AddScoped<PurgeDlqCommandHandler>();
        services.AddScoped<ResubmitDlqCommandHandler>();

        return services;
    }
}
