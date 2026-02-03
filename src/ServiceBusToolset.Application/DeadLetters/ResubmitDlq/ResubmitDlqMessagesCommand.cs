using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record ResubmitDlqMessagesCommand(string FullyQualifiedNamespace,
                                                EntityTarget Target,
                                                string TargetEntity,
                                                DateTimeOffset? BeforeTime = null,
                                                IReadOnlySet<DlqCategoryKey>? CategoryFilter = null,
                                                IProgress<(int Resubmitted, int Skipped)>? Progress = null) : ICommand<Result<ResubmitDlqResult>>;
