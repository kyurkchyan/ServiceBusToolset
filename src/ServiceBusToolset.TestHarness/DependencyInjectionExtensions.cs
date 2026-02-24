using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.TestHarness.Common.Commands;
using ServiceBusToolset.TestHarness.Common.Logging;
using ServiceBusToolset.TestHarness.Common.ServiceBus;
using ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

namespace ServiceBusToolset.TestHarness;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddTestHarness(this IServiceCollection services)
    {
        services.AddSingleton<IServiceBusClientFactory, ServiceBusClientFactory>();
        services.AddSingleton<IConsoleOutput, ConsoleOutput>();

        return services;
    }

    public static IServiceCollection AddCommandHandlers(this IServiceCollection services)
        => services
           .AddCommandHandler<GenerateDlqCliCommand, GenerateDlqCommandHandler>();

    private static IServiceCollection AddCommandHandler<TCommand, THandler>(this IServiceCollection services)
        where THandler : class, ICommandHandler<TCommand>
        where TCommand : class
        => services.AddScoped<ICommandHandler<TCommand>, THandler>();
}
