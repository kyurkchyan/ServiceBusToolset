using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace ServiceBusToolset.Services;

public class ServiceBusClientFactory : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
    {
        var credential = new DefaultAzureCredential();
        return new ServiceBusClient(fullyQualifiedNamespace, credential);
    }

    public ServiceBusAdministrationClient CreateAdministrationClient(string fullyQualifiedNamespace)
    {
        var credential = new DefaultAzureCredential();
        return new ServiceBusAdministrationClient(fullyQualifiedNamespace, credential);
    }
}
