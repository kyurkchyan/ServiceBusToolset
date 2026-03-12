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

            var newMessages = messages.Select(MessageResubmitHelper.CreateResubmitMessage).ToList();
            await sender.SendMessagesAsync(newMessages, cancellationToken);

            var completeTasks = messages.Select(m => receiver.CompleteMessageAsync(m, cancellationToken));
            await Task.WhenAll(completeTasks);

            totalResubmitted += messages.Count;

            command.Progress?.Report((totalResubmitted, 0));
        }

        return Result.Success(new ResubmitDlqResult(totalResubmitted, 0));
    }

    /// <summary>
    /// Resubmits messages from the dead-letter queue for the target entity, applying the command's time and category filters and tracking skipped messages.
    /// </summary>
    /// <param name="command">Contains target entity info, optional BeforeTime and CategoryFilter for filtering, and an optional Progress reporter to receive (resubmittedCount, skippedCount) updates.</param>
    /// <param name="cancellationToken">Cancellation token to stop processing.</param>
    /// <returns>A <see cref="ResubmitDlqResult"/> with the total number of messages resubmitted and the number of messages skipped due to filtering.</returns>
    private async Task<Result<ResubmitDlqResult>> ResubmitWithFilterAsync(
        ServiceBusClient client,
        ResubmitDlqMessagesCommand command,
        CancellationToken cancellationToken)
    {
        await using var receiver = ReceiverFactory.CreateDlqReceiver(client,
                                                                     command.Target);
        await using var sender = client.CreateSender(command.TargetEntity);

        var resolver = new CategoryPropertyResolver();
        var totalResubmitted = 0;
        var skippedSequenceNumbers = new HashSet<long>();
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

            var toResubmit = new List<(ServiceBusReceivedMessage Original, ServiceBusMessage New)>();
            var toAbandon = new List<ServiceBusReceivedMessage>();

            foreach (var message in messages)
            {
                if (ShouldResubmit(message, command, resolver))
                {
                    toResubmit.Add((message, MessageResubmitHelper.CreateResubmitMessage(message)));
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
                foreach (var m in toAbandon)
                {
                    skippedSequenceNumbers.Add(m.SequenceNumber);
                }
            }

            if (toResubmit.Count > 0)
            {
                emptyBatches = 0;
            }
            else
            {
                emptyBatches++;
            }

            command.Progress?.Report((totalResubmitted, skippedSequenceNumbers.Count));
        }

        return Result.Success(new ResubmitDlqResult(totalResubmitted, skippedSequenceNumbers.Count));
    }

    /// <summary>
    /// Determines whether a DLQ message meets the command's filters and should be resubmitted.
    /// </summary>
    /// <param name="message">The received dead-letter message to evaluate.</param>
    /// <param name="command">The resubmission command containing optional BeforeTime, CategoryFilter, and Schema.</param>
    /// <param name="resolver">Resolver used to read category-related properties from the message when filtering by category.</param>
    /// <returns>`true` if the message satisfies the time and category filters in the command and is eligible for resubmission, `false` otherwise.</returns>
    private static bool ShouldResubmit(ServiceBusReceivedMessage message,
                                       ResubmitDlqMessagesCommand command,
                                       CategoryPropertyResolver resolver)
    {
        if (command.BeforeTime.HasValue && message.EnqueuedTime >= command.BeforeTime.Value)
        {
            return false;
        }

        if (command.CategoryFilter is { Count: > 0 })
        {
            var schema = command.Schema ?? CategorizationSchema.Default;
            var key = DlqCategoryKey.FromMessage(message, schema, resolver);
            if (!command.CategoryFilter.Contains(key))
            {
                return false;
            }
        }

        return true;
    }
}
