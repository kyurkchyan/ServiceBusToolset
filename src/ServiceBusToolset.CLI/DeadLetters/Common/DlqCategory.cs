namespace ServiceBusToolset.CLI.DeadLetters.Common;

public record DlqCategory(string Label, string DeadLetterReason, int Count);
