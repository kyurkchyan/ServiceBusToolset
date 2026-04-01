using Ardalis.Result;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Models;

namespace ServiceBusToolset.Application.DeadLetters.PeekDlq;

public sealed record PeekDlqCommand(
    string FullyQualifiedNamespace,
    EntityTarget Target,
    int MaxMessages = int.MaxValue,
    DateTimeOffset? BeforeTime = null) : ICommand<Result<PeekDlqResult>>;
