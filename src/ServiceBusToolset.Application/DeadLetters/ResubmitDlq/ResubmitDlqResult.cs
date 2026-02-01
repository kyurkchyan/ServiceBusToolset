namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record ResubmitDlqResult(int ResubmittedCount, int SkippedCount);
