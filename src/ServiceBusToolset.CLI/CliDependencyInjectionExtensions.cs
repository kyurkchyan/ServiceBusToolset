using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.Common.Queues;
using ServiceBusToolset.CLI.Common.ServiceBus;
using ServiceBusToolset.CLI.DeadLetters.Common;
using ServiceBusToolset.CLI.DeadLetters.DianoseDlq.AppInsights;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
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
        services.AddSingleton<IAppInsightsService, AppInsightsService>();

        // Command handlers
        services.AddScoped<DumpDlqCommandHandler>();

        // Legacy services (for other commands not yet migrated)
        services.AddSingleton<IDlqCategoryAnalyzer, DlqCategoryAnalyzer>();

        return services;
    }
}
