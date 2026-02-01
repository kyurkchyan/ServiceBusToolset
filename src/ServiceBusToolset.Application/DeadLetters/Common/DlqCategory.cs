namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record DlqCategory(string Label, string DeadLetterReason, int Count);
