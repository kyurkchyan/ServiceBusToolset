namespace ServiceBusToolset.Application.DeadLetters.PurgeDlq;

public sealed record PurgeDlqResult(int PurgedCount, int SkippedCount);
