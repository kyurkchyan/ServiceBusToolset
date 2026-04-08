namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed record PeekDlqBatchResult(IReadOnlyList<PeekedMessage> Messages,
                                        int PeekedInBatch,
                                        int SkippedNoOperationId,
                                        long? LastSequenceNumber,
                                        bool HasMoreMessages,
                                        long TotalDeadLetterCount);

public sealed record PeekedMessage(string? MessageId,
                                   string? Subject,
                                   string OperationId,
                                   DateTimeOffset EnqueuedTime,
                                   string? DeadLetterReason);
