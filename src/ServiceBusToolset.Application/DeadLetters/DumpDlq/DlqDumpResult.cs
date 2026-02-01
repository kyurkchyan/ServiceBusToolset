namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed record DlqDumpResult(int MessageCount, string OutputFilePath);
