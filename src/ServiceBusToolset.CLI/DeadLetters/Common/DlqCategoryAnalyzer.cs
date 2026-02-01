using Azure.Messaging.ServiceBus;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

public class DlqCategoryAnalyzer : IDlqCategoryAnalyzer
{
    private const int MaxBatchSize = 100;

    public async Task<List<DlqCategory>> AnalyzeCategoriesAsync(
        ServiceBusClient client,
        string? queue,
        string? topic,
        string? subscription,
        IConsoleOutput output,
        CancellationToken cancellationToken)
    {
        await using var receiver = CreateDlqReceiver(client, queue, topic, subscription);

        var categoryCounts = new Dictionary<(string Label, string Reason), int>();
        var totalPeeked = 0;
        long? fromSequenceNumber = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> messages;

            if (fromSequenceNumber.HasValue)
            {
                messages = await receiver.PeekMessagesAsync(MaxBatchSize, fromSequenceNumber.Value, cancellationToken);
            }
            else
            {
                messages = await receiver.PeekMessagesAsync(MaxBatchSize, cancellationToken: cancellationToken);
            }

            if (messages.Count == 0)
            {
                break;
            }

            foreach (var msg in messages)
            {
                var label = msg.Subject ?? "(none)";
                var reason = msg.DeadLetterReason ?? "(none)";
                var key = (label, reason);

                var count = categoryCounts.GetValueOrDefault(key, 0);
                categoryCounts[key] = count + 1;
            }

            totalPeeked += messages.Count;
            fromSequenceNumber = messages[^1].SequenceNumber + 1;

            output.Progress($"Peeked {totalPeeked} messages...");
        }

        Console.WriteLine();

        return categoryCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => new DlqCategory(kvp.Key.Label, kvp.Key.Reason, kvp.Value))
            .ToList();
    }

    private static ServiceBusReceiver CreateDlqReceiver(
        ServiceBusClient client,
        string? queueName,
        string? topicName,
        string? subscriptionName)
    {
        var options = new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        };

        if (!string.IsNullOrEmpty(queueName))
        {
            return client.CreateReceiver(queueName, options);
        }

        return client.CreateReceiver(topicName!, subscriptionName!, options);
    }
}
