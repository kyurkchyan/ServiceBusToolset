using Azure.Messaging.ServiceBus;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.DeadLetters.Common;

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
