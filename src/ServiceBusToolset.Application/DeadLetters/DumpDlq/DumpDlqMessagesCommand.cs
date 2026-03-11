using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DumpDlq;

public sealed record DumpDlqMessagesCommand(string FullyQualifiedNamespace,
                                            EntityTarget Target,
                                            string OutputFilePath,
                                            DateTimeOffset? BeforeTime = null,
                                            IReadOnlySet<DlqCategoryKey>? CategoryFilter = null,
                                            IProgress<int>? Progress = null,
                                            CategorizationSchema? Schema = null) : ICommand<Result<DlqDumpResult>>;
