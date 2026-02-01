using System.Text.Json.Nodes;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

public class DiagnosticResult
{
    public string? MessageId { get; set; }
    public string? Subject { get; set; }
    public string? OperationId { get; set; }
    public DateTimeOffset EnqueuedTime { get; set; }
    public string? DeadLetterReason { get; set; }
    public JsonNode? Body { get; set; }
    public List<ExceptionInfo> Exceptions { get; set; } = [];
    public List<TraceInfo> Traces { get; set; } = [];
    public List<DependencyInfo> FailedDependencies { get; set; } = [];
}
