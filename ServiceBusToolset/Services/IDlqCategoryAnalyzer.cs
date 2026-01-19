using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Models;

namespace ServiceBusToolset.Services;

public interface IDlqCategoryAnalyzer
{
    Task<List<DlqCategory>> AnalyzeCategoriesAsync(
        ServiceBusClient client,
        string? queue,
        string? topic,
        string? subscription,
        IConsoleOutput output,
        CancellationToken cancellationToken);
}
