using Microsoft.Extensions.DependencyInjection;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public abstract class BaseIntegrationTest(ServiceBusEmulatorFixture fixture,
                                          Action<IServiceCollection>? configureServices = null)
    : BaseServiceBusIntegrationTest(fixture, configureServices);
