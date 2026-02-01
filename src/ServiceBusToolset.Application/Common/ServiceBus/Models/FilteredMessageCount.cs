namespace ServiceBusToolset.Application.Common.ServiceBus.Models;

/// <summary>
/// Represents the count of messages after applying a filter.
/// </summary>
public sealed record FilteredMessageCount(long FilteredCount, long TotalCount);
