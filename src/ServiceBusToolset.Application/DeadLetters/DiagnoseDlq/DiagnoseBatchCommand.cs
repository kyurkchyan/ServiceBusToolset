using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.DeadLetters.DiagnoseDlq.Models;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed record DiagnoseBatchCommand(string AppInsightsResourceId,
                                          IReadOnlyList<OperationInfo> Operations) : ICommand<Result<IReadOnlyList<DiagnosticResult>>>;

public sealed record OperationInfo(string OperationId,
                                   DateTimeOffset EnqueuedTime,
                                   string? MessageId,
                                   string? Subject,
                                   string? DeadLetterReason);
