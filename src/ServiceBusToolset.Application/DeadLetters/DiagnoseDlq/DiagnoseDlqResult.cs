using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed record DiagnoseDlqResult(IReadOnlyList<DiagnosticResult> Results,
                                       int TotalProcessed,
                                       int SkippedNoOperationId,
                                       int ResultsWithTelemetry);
