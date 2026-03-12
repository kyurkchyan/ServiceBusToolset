using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.PurgeDlq;

public sealed record PurgeDlqMessagesCommand(string FullyQualifiedNamespace,
                                             EntityTarget Target,
                                             DateTimeOffset? BeforeTime = null,
                                             IReadOnlySet<DlqCategoryKey>? CategoryFilter = null,
                                             IProgress<(int Purged, int Skipped)>? Progress = null,
                                             CategorizationSchema? Schema = null) : ICommand<Result<PurgeDlqResult>>;
