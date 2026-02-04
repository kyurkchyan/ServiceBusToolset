using Azure.Core.Amqp;
using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.Tests.Common.Builders;

/// <summary>
/// Builder for creating test ServiceBusReceivedMessage instances using ServiceBusModelFactory.
/// </summary>
public class ServiceBusReceivedMessageBuilder
{
    private BinaryData _body = BinaryData.FromString("{}");
    private string _messageId = Guid.NewGuid().ToString();
    private string? _correlationId;
    private string? _subject;
    private string? _contentType;
    private string? _deadLetterReason;
    private string? _deadLetterErrorDescription;
    private string? _deadLetterSource;
    private DateTimeOffset _enqueuedTime = DateTimeOffset.UtcNow;
    private long _sequenceNumber = 1;
    private string? _sessionId;
    private string? _partitionKey;
    private string? _to;
    private string? _replyTo;
    private string? _replyToSessionId;
    private TimeSpan _timeToLive = TimeSpan.FromDays(14);
    private int _deliveryCount = 1;
    private string _lockToken = Guid.NewGuid().ToString();
    private DateTimeOffset _lockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
    private readonly Dictionary<string, object> _applicationProperties = new();

    public ServiceBusReceivedMessageBuilder WithBody(string body)
    {
        _body = BinaryData.FromString(body);
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithBody(BinaryData body)
    {
        _body = body;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithJsonBody(object obj)
    {
        _body = BinaryData.FromObjectAsJson(obj);
        _contentType = "application/json";
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithMessageId(string messageId)
    {
        _messageId = messageId;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithCorrelationId(string correlationId)
    {
        _correlationId = correlationId;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithSubject(string subject)
    {
        _subject = subject;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithContentType(string contentType)
    {
        _contentType = contentType;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithDeadLetterReason(string reason)
    {
        _deadLetterReason = reason;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithDeadLetterErrorDescription(string description)
    {
        _deadLetterErrorDescription = description;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithDeadLetterSource(string source)
    {
        _deadLetterSource = source;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithEnqueuedTime(DateTimeOffset enqueuedTime)
    {
        _enqueuedTime = enqueuedTime;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithSequenceNumber(long sequenceNumber)
    {
        _sequenceNumber = sequenceNumber;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithSessionId(string sessionId)
    {
        _sessionId = sessionId;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithPartitionKey(string partitionKey)
    {
        _partitionKey = partitionKey;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithTo(string to)
    {
        _to = to;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithReplyTo(string replyTo)
    {
        _replyTo = replyTo;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithReplyToSessionId(string replyToSessionId)
    {
        _replyToSessionId = replyToSessionId;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithTimeToLive(TimeSpan timeToLive)
    {
        _timeToLive = timeToLive;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithDeliveryCount(int deliveryCount)
    {
        _deliveryCount = deliveryCount;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithLockToken(string lockToken)
    {
        _lockToken = lockToken;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithLockedUntil(DateTimeOffset lockedUntil)
    {
        _lockedUntil = lockedUntil;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithApplicationProperty(string key, object value)
    {
        _applicationProperties[key] = value;
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithDiagnosticId(string traceId)
    {
        _applicationProperties["Diagnostic-Id"] = $"00-{traceId}-0000000000000001-01";
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithTraceparent(string traceId)
    {
        _applicationProperties["traceparent"] = $"00-{traceId}-0000000000000001-01";
        return this;
    }

    public ServiceBusReceivedMessageBuilder WithOperationId(string operationId)
    {
        _applicationProperties["Operation-Id"] = operationId;
        return this;
    }

    public ServiceBusReceivedMessage Build()
    {
        // Create AMQP message with dead letter properties in message annotations
        var amqpMessage = new AmqpAnnotatedMessage(AmqpMessageBody.FromData([_body.ToMemory()])) { Properties = {
            // Set basic properties
            MessageId = new AmqpMessageId(_messageId) } };

        if (_correlationId != null)
        {
            amqpMessage.Properties.CorrelationId = new AmqpMessageId(_correlationId);
        }

        if (_subject != null)
        {
            amqpMessage.Properties.Subject = _subject;
        }

        if (_to != null)
        {
            amqpMessage.Properties.To = new AmqpAddress(_to);
        }

        if (_contentType != null)
        {
            amqpMessage.Properties.ContentType = _contentType;
        }

        if (_replyTo != null)
        {
            amqpMessage.Properties.ReplyTo = new AmqpAddress(_replyTo);
        }

        // Set application properties
        foreach (var (key, value) in _applicationProperties)
        {
            amqpMessage.ApplicationProperties[key] = value;
        }

        // Set dead letter properties in application properties (this is where Service Bus SDK expects them)
        if (_deadLetterReason != null)
        {
            amqpMessage.ApplicationProperties["DeadLetterReason"] = _deadLetterReason;
        }

        if (_deadLetterErrorDescription != null)
        {
            amqpMessage.ApplicationProperties["DeadLetterErrorDescription"] = _deadLetterErrorDescription;
        }

        if (_deadLetterSource != null)
        {
            amqpMessage.MessageAnnotations["x-opt-dead-letter-source"] = _deadLetterSource;
        }

        // Set header properties
        amqpMessage.Header.DeliveryCount = (uint)_deliveryCount;
        amqpMessage.Header.TimeToLive = _timeToLive;

        // Set message annotations for Service Bus properties
        // AMQP requires DateTime in UTC format, not DateTimeOffset
        amqpMessage.MessageAnnotations["x-opt-enqueued-time"] = _enqueuedTime.UtcDateTime;
        amqpMessage.MessageAnnotations["x-opt-sequence-number"] = _sequenceNumber;
        amqpMessage.MessageAnnotations["x-opt-locked-until"] = _lockedUntil.UtcDateTime;

        if (_partitionKey != null)
        {
            amqpMessage.MessageAnnotations["x-opt-partition-key"] = _partitionKey;
        }

        if (_sessionId != null)
        {
            amqpMessage.Properties.GroupId = _sessionId;
        }

        if (_replyToSessionId != null)
        {
            amqpMessage.Properties.ReplyToGroupId = _replyToSessionId;
        }

        // Use the factory method that takes AMQP message
        return ServiceBusReceivedMessage.FromAmqpMessage(amqpMessage,
                                                         BinaryData.FromBytes(Guid.TryParse(_lockToken, out var guid) ? guid.ToByteArray() : Guid.NewGuid().ToByteArray()));
    }

    /// <summary>
    /// Creates a new builder with default values.
    /// </summary>
    public static ServiceBusReceivedMessageBuilder Create() => new();

    /// <summary>
    /// Creates a dead letter message with the specified reason.
    /// </summary>
    public static ServiceBusReceivedMessageBuilder CreateDeadLetter(string reason = "MaxDeliveryCountExceeded") =>
        new ServiceBusReceivedMessageBuilder()
            .WithDeadLetterReason(reason)
            .WithDeadLetterSource("test-queue")
            .WithDeliveryCount(10);
}
