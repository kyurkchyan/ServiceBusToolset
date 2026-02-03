using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

public interface IServiceBusClientFactory
{
    ServiceBusClient CreateClient(string fullyQualifiedNamespace);
    ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace);
}
