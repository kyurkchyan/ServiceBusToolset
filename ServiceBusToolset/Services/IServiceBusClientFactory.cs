using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace ServiceBusToolset.Services;

public interface IServiceBusClientFactory
{
    ServiceBusClient CreateClient(string fullyQualifiedNamespace);
    ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace);
}
