using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application;

public static class ApplicationDependencyInjectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        services.AddScoped<DlqMessageService>();

        return services;
    }
}
