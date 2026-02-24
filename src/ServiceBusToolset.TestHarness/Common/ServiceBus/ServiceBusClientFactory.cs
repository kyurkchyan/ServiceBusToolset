using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.TestHarness.Common.ServiceBus;

public interface IServiceBusClientFactory
{
    ServiceBusClient CreateClient(string fullyQualifiedNamespace);
}

public class ServiceBusClientFactory : IServiceBusClientFactory
{
    public ServiceBusClient CreateClient(string fullyQualifiedNamespace)
    {
        var credential = new DefaultAzureCredential();
        return new ServiceBusClient(fullyQualifiedNamespace, credential);
    }
}
