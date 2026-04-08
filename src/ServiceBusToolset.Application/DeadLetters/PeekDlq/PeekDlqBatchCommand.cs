using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed record PeekDlqBatchCommand(string FullyQualifiedNamespace,
                                         EntityTarget Target,
                                         int BatchSize = 500,
                                         long? FromSequenceNumber = null,
                                         long? KnownDeadLetterCount = null) : ICommand<Result<PeekDlqBatchResult>>;
