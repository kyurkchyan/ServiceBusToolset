using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.CLI.Common.Commands;
using ServiceBusToolset.CLI.Common.Logging;
using ServiceBusToolset.CLI.Common.ServiceBus;
using ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;
using ServiceBusToolset.CLI.DeadLetters.DumpDlq;
using ServiceBusToolset.CLI.DeadLetters.PurgeDlq;
using ServiceBusToolset.CLI.DeadLetters.ResubmitDlq;
using ServiceBusToolset.CLI.Queues.MonitorQueues;
using ServiceBusToolset.CLI.Subscriptions.MonitorSubscriptions;
using IServiceBusClientFactory = ServiceBusToolset.Application.Common.ServiceBus.Abstractions.IServiceBusClientFactory;

namespace ServiceBusToolset.CLI;

public static class CliDependencyInjectionExtensions
{
    public static IServiceCollection AddCli(this IServiceCollection services)
    {
        services.AddSingleton<IServiceBusClientFactory, ServiceBusClientFactory>();
        services.AddSingleton<IConsoleOutput, ConsoleOutput>();

        return services;
    }

    public static IServiceCollection AddCommandHandlers(this IServiceCollection services)
        => services
           .AddCommandHandler<DumpDlqCliCommand, DumpDlqCommandHandler>()
           .AddCommandHandler<PurgeDlqCliCommand, PurgeDlqCommandHandler>()
           .AddCommandHandler<ResubmitDlqCliCommand, ResubmitDlqCommandHandler>()
           .AddCommandHandler<DiagnoseDlqCliCommand, DiagnoseDlqCommandHandler>()
           .AddCommandHandler<MonitorQueuesCliCommand, MonitorQueuesCommandHandler>()
           .AddCommandHandler<MonitorSubscriptionsCliCommand, MonitorSubscriptionsCommandHandler>();

    private static IServiceCollection AddCommandHandler<TCommand, THandler>(this IServiceCollection services)
        where THandler : class, ICommandHandler<TCommand>
        where TCommand : class
        => services.AddScoped<ICommandHandler<TCommand>, THandler>();
}
