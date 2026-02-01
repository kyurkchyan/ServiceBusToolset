using Ardalis.Result;
using Mediator;

namespace ServiceBusToolset.Application.Queues.MonitorQueues;

public sealed record MonitorQueuesCommand(string FullyQualifiedNamespace,
                                          string? QueueFilter,
                                          TimeSpan RefreshInterval,
                                          CancellationToken CancellationToken) : ICommand<Result<MonitorQueuesResult>>;
