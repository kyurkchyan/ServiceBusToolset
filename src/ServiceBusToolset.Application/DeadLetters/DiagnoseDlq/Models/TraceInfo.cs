namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

public class TraceInfo
{
    public DateTimeOffset Timestamp { get; set; }
    public string? Message { get; set; }
    public int SeverityLevel { get; set; }
}
