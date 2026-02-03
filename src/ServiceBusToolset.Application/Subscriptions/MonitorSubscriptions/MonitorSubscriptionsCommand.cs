using Ardalis.Result;
using Mediator;

namespace ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions;

public sealed record MonitorSubscriptionsCommand(string FullyQualifiedNamespace,
                                                 string? TopicFilter,
                                                 string? SubscriptionFilter,
                                                 TimeSpan RefreshInterval,
                                                 CancellationToken CancellationToken) : ICommand<Result<MonitorSubscriptionsResult>>;
