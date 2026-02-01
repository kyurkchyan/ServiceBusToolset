using System.Text.Json.Nodes;

namespace ServiceBusToolset.CLI.DeadLetters.DianoseDlq;

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

public class ExceptionInfo
{
    public DateTimeOffset Timestamp { get; set; }
    public string? ProblemId { get; set; }
    public string? ExceptionType { get; set; }
    public string? OuterMessage { get; set; }
    public string? InnermostMessage { get; set; }
    public string? Details { get; set; }
}

public class TraceInfo
{
    public DateTimeOffset Timestamp { get; set; }
    public string? Message { get; set; }
    public int SeverityLevel { get; set; }
}

public class DependencyInfo
{
    public DateTimeOffset Timestamp { get; set; }
    public string? Type { get; set; }
    public string? Target { get; set; }
    public string? Name { get; set; }
    public string? Data { get; set; }
    public int ResultCode { get; set; }
    public bool Success { get; set; }
    public double DurationMs { get; set; }
}
