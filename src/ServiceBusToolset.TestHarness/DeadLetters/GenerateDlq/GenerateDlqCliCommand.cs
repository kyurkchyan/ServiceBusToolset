using CommandLine;
using ServiceBusToolset.TestHarness.Common.Commands;

namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

[Verb("generate-dlq", HelpText = "Generate dead-letter messages in a Service Bus queue for testing")]
public class GenerateDlqCliCommand : ICliCommand
{
    [Option('n',
            "namespace",
            Required = true,
            HelpText = "Fully qualified Service Bus namespace (e.g., mynamespace.servicebus.windows.net)")]
    public required string Namespace { get; set; }

    [Option('q',
            "queue",
            Required = true,
            HelpText = "Queue name")]
    public required string Queue { get; set; }

    [Option('c',
            "count",
            Default = 100,
            HelpText = "Total number of DLQ messages to generate")]
    public int Count { get; set; }

    [Option('v',
            "verbose",
            Default = false,
            HelpText = "Enable verbose output")]
    public bool Verbose { get; set; }

    [Option('a',
            "app-insights-connection-string",
            Required = false,
            HelpText = "Application Insights connection string for generating correlated telemetry")]
    public string? AppInsightsConnectionString { get; set; }

    public string? Validate()
    {
        if (Count <= 0)
        {
            return "--count must be greater than 0.";
        }

        return null;
    }
}
