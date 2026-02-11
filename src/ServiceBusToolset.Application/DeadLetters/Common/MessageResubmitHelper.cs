using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public static class MessageResubmitHelper
{
    public static ServiceBusMessage CreateResubmitMessage(ServiceBusReceivedMessage original)
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
