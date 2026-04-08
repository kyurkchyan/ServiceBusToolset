using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;
using ServiceBusToolset.Application.DeadLetters.Common;

namespace ServiceBusToolset.Application.DeadLetters.DiagnoseDlq;

public sealed record DiagnoseDlqCommand(string FullyQualifiedNamespace,
                                        EntityTarget Target,
                                        string? AppInsightsResourceId,
                                        int MaxMessages,
                                        DateTimeOffset? BeforeTime = null,
                                        IReadOnlySet<DlqCategoryKey>? CategoryFilter = null,
                                        IProgress<int>? Progress = null,
                                        IProgress<(int Current, int Total)>? BatchProgress = null,
                                        CategorizationSchema? Schema = null) : ICommand<Result<DiagnoseDlqResult>>;
