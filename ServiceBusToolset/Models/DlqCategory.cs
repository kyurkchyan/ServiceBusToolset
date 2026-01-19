namespace ServiceBusToolset.Models;

public record DlqCategory(string Label, string DeadLetterReason, int Count);
