using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.Common.ServiceBus.Helpers;

/// <summary>
/// Factory methods for creating Service Bus receivers.
/// </summary>
public static class ReceiverFactory
{
    /// <summary>
    /// Creates a receiver for the specified entity target.
    /// </summary>
    public static ServiceBusReceiver CreateReceiver(
        ServiceBusClient client,
        EntityTarget target,
        ServiceBusReceiverOptions? options = null)
    {
        options ??= new ServiceBusReceiverOptions();

        if (target.IsQueueMode)
        {
            return client.CreateReceiver(target.Queue!, options);
        }

        return client.CreateReceiver(target.Topic!, target.Subscription!, options);
    }

    /// <summary>
    /// Creates a receiver for the dead letter subqueue of the specified entity target.
    /// </summary>
    public static ServiceBusReceiver CreateDlqReceiver(
        ServiceBusClient client,
        EntityTarget target)
    {
        var options = new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        };

        return CreateReceiver(client, target, options);
    }
}
