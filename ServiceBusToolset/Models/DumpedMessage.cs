using System.Text.Json.Nodes;

namespace ServiceBusToolset.Models;

public class DumpedMessage
{
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Subject { get; set; }
    public string? ContentType { get; set; }
    public JsonNode? Body { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }
    public DateTimeOffset EnqueuedTime { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long SequenceNumber { get; set; }
    public string? SessionId { get; set; }
    public string? PartitionKey { get; set; }
    public string? To { get; set; }
    public string? ReplyTo { get; set; }
    public string? ReplyToSessionId { get; set; }
    public TimeSpan TimeToLive { get; set; }
    public Dictionary<string, object?> ApplicationProperties { get; set; } = new();
}
