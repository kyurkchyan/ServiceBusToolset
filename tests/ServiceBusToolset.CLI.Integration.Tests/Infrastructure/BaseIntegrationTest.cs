using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.IntegrationTesting;

namespace ServiceBusToolset.CLI.Integration.Tests.Infrastructure;

public abstract class BaseIntegrationTest(ServiceBusEmulatorFixture fixture,
                                          Action<IServiceCollection>? configureServices = null)
    : BaseServiceBusIntegrationTest(fixture, configureServices);
