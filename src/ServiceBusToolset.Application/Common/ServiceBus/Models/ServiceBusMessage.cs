using System.Text.Json.Nodes;

namespace ServiceBusToolset.Application.Common.ServiceBus.Models;

public sealed class ServiceBusMessage
{
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? ContentType { get; init; }
    public JsonNode? Body { get; init; }
    public string? DeadLetterReason { get; init; }
    public string? DeadLetterErrorDescription { get; init; }
    public DateTimeOffset EnqueuedTime { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long SequenceNumber { get; init; }
    public string? SessionId { get; init; }
    public string? PartitionKey { get; init; }
    public string? To { get; init; }
    public string? ReplyTo { get; init; }
    public string? ReplyToSessionId { get; init; }
    public TimeSpan TimeToLive { get; init; }
    public Dictionary<string, object?> ApplicationProperties { get; init; } = new();
}
