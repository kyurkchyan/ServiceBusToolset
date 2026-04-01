namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed record PeekDlqResult(
    IReadOnlyList<PeekedMessage> Messages,
    int TotalPeeked,
    int SkippedNoOperationId);

public sealed record PeekedMessage(
    string? MessageId,
    string? Subject,
    string OperationId,
    DateTimeOffset EnqueuedTime,
    string? DeadLetterReason);
