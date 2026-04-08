using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Common.AppInsights;

namespace ServiceBusToolset.Application;

public static class ApplicationDependencyInjectionExtensions
{
    public static IServiceCollection AddServiceBusToolsetApplication(this IServiceCollection services)
    {
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });

        services.AddScoped<DlqMessageService>();
        services.AddScoped<IAppInsightsService, AppInsightsService>();

        return services;
    }
}
