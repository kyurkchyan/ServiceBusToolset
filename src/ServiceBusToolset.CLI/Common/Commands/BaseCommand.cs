using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.Common.Commands;

public abstract class BaseCommand<TOptions>
{
    protected readonly IServiceBusClientFactory ClientFactory;
    protected readonly IConsoleOutput Output;

    protected BaseCommand(IServiceBusClientFactory clientFactory, IConsoleOutput output)
    {
        ClientFactory = clientFactory;
        Output = output;
    }

    protected ServiceBusReceiver CreateDlqReceiver(
        ServiceBusClient client,
        string? queueName,
        string? topicName,
        string? subscriptionName,
        ServiceBusReceiveMode receiveMode)
    {
        var options = new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = receiveMode
        };

        if (!string.IsNullOrEmpty(queueName))
        {
            return client.CreateReceiver(queueName, options);
        }

        return client.CreateReceiver(topicName!, subscriptionName!, options);
    }

    protected string GetEntityDescription(string? queueName, string? topicName, string? subscriptionName)
    {
        if (!string.IsNullOrEmpty(queueName))
        {
            return $"queue '{queueName}'";
        }

        return $"topic '{topicName}' subscription '{subscriptionName}'";
    }
}
