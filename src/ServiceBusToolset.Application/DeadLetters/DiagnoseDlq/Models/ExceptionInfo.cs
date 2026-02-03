namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

public class ExceptionInfo
{
    public DateTimeOffset Timestamp { get; set; }
    public string? ProblemId { get; set; }
    public string? ExceptionType { get; set; }
    public string? OuterMessage { get; set; }
    public string? InnermostMessage { get; set; }
    public string? Details { get; set; }
}
