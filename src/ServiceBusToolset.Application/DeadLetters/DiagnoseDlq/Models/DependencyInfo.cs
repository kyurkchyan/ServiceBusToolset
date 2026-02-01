namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

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
