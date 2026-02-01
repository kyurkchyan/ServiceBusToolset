using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed class ResubmitDlqMessagesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<ResubmitDlqMessagesCommand, Result<ResubmitDlqResult>>
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromSeconds(5);
    private const int EmptyBatchThreshold = 3;

    public async ValueTask<Result<ResubmitDlqResult>> Handle(
        ResubmitDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var client = clientFactory.CreateClient(command.FullyQualifiedNamespace);

        var hasFilter = command.BeforeTime.HasValue || command.CategoryFilter is { Count: > 0 };

        if (hasFilter)
        {
            return await ResubmitWithFilterAsync(client, command, cancellationToken);
        }

        return await ResubmitAllAsync(client, command, cancellationToken);
    }

    private async Task<Result<ResubmitDlqResult>> ResubmitAllAsync(
        ServiceBusClient client,
        ResubmitDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target);
        await using var sender = client.CreateSender(command.TargetEntity);

        var totalResubmitted = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;

            var newMessages = messages.Select(CreateResubmitMessage).ToList();
            await sender.SendMessagesAsync(newMessages, cancellationToken);

            var completeTasks = messages.Select(m => receiver.CompleteMessageAsync(m, cancellationToken));
            await Task.WhenAll(completeTasks);

            totalResubmitted += messages.Count;

            command.Progress?.Report((totalResubmitted, 0));
        }

        return Result.Success(new ResubmitDlqResult(totalResubmitted, 0));
    }

    private async Task<Result<ResubmitDlqResult>> ResubmitWithFilterAsync(
        ServiceBusClient client,
        ResubmitDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target);
        await using var sender = client.CreateSender(command.TargetEntity);

        var totalResubmitted = 0;
        var totalSkipped = 0;
        var emptyBatches = 0;

        while (!cancellationToken.IsCancellationRequested && emptyBatches < EmptyBatchThreshold)
        {
            var messages = await receiver.ReceiveMessagesAsync(MaxBatchSize,
                                                               MaxWaitTime,
                                                               cancellationToken);

            if (messages.Count == 0)
            {
                emptyBatches++;
                continue;
            }

            emptyBatches = 0;

            var toResubmit = new List<(ServiceBusReceivedMessage Original, ServiceBusMessage New)>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (ShouldResubmit(message, command))
                {
                    toResubmit.Add((message, CreateResubmitMessage(message)));
                }
                else
                {
                    toAbandon.Add(message);
                }
            }

            if (toResubmit.Count > 0)
            {
                await sender.SendMessagesAsync(toResubmit.Select(x => x.New).ToList(), cancellationToken);
                var completeTasks = toResubmit.Select(x => receiver.CompleteMessageAsync(x.Original, cancellationToken));
                await Task.WhenAll(completeTasks);
                totalResubmitted += toResubmit.Count;
            }

            if (toAbandon.Count > 0)
            {
                var abandonTasks = toAbandon.Select(m => receiver.AbandonMessageAsync(m, cancellationToken:cancellationToken));
                await Task.WhenAll(abandonTasks);
                totalSkipped += toAbandon.Count;
            }

            command.Progress?.Report((totalResubmitted, totalSkipped));
        }

        return Result.Success(new ResubmitDlqResult(totalResubmitted, totalSkipped));
    }

    private static bool ShouldResubmit(ServiceBusReceivedMessage message, ResubmitDlqMessagesCommand command)
    {
        if (command.BeforeTime.HasValue && message.EnqueuedTime >= command.BeforeTime.Value)
        {
            return false;
        }

        if (command.CategoryFilter is { Count: > 0 })
        {
            var key = DlqCategoryKey.FromMessage(message.Subject, message.DeadLetterReason);
            if (!command.CategoryFilter.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    private static ServiceBusMessage CreateResubmitMessage(ServiceBusReceivedMessage original)
    {
        var message = new ServiceBusMessage(original.Body)
        {
            ContentType = original.ContentType,
            Subject = original.Subject,
            MessageId = original.MessageId,
            CorrelationId = original.CorrelationId,
            To = original.To,
            ReplyTo = original.ReplyTo,
            ReplyToSessionId = original.ReplyToSessionId,
            SessionId = original.SessionId,
            PartitionKey = original.PartitionKey,
            TransactionPartitionKey = original.TransactionPartitionKey,
            TimeToLive = original.TimeToLive
        };

        foreach (var prop in original.ApplicationProperties)
        {
            message.ApplicationProperties[prop.Key] = prop.Value;
        }

        return message;
    }
}
