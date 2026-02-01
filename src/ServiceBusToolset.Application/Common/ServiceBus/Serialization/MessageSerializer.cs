using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ServiceBusMessage = ServiceBusToolset.Application.Common.ServiceBus.Models.ServiceBusMessage;

namespace ServiceBusToolset.Application.Common.ServiceBus.Serialization;

/// <summary>
/// Provides serialization operations for Service Bus messages.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Converts a Service Bus message to a ServiceBusMessage DTO.
    /// </summary>
    public static ServiceBusMessage ToDto(ServiceBusReceivedMessage message)
    {
        return new ServiceBusMessage
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            Subject = message.Subject,
            ContentType = message.ContentType,
            Body = MessageBodyDecoder.Decode(message),
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,
            EnqueuedTime = message.EnqueuedTime,
            ExpiresAt = message.ExpiresAt,
            SequenceNumber = message.SequenceNumber,
            SessionId = message.SessionId,
            PartitionKey = message.PartitionKey,
            To = message.To,
            ReplyTo = message.ReplyTo,
            ReplyToSessionId = message.ReplyToSessionId,
            TimeToLive = message.TimeToLive,
            ApplicationProperties = message.ApplicationProperties.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value)
        };
    }

    /// <summary>
    /// Converts a collection of Service Bus messages to ServiceBusMessage DTOs.
    /// </summary>
    public static List<ServiceBusMessage> ToDtoList(IEnumerable<ServiceBusReceivedMessage> messages)
        => messages.Select(ToDto).ToList();

    /// <summary>
    /// Writes messages to a JSON file.
    /// </summary>
    public static Task WriteJsonAsync(
        string filePath,
        IEnumerable<ServiceBusMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(messages, JsonOptions);
        return File.WriteAllTextAsync(filePath, json, cancellationToken);
    }
}
