using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

namespace ServiceBusToolset.IntegrationTesting;

public sealed class EmulatorServiceBusClientFactory(string connectionString,
                                                    string administrationConnectionString) : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
        => new(connectionString);

    public ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace)
        => new(administrationConnectionString);
}
