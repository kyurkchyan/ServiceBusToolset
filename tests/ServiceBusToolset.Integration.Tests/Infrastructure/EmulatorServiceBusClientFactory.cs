using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class EmulatorServiceBusClientFactory(string connectionString) : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
        => new(connectionString);

    public ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace)
        => new(connectionString);
}
