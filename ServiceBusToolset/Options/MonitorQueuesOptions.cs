using CommandLine;

namespace ServiceBusToolset.Options;

[Verb("monitor-queues", HelpText = "Monitor Service Bus queue statistics in a live-updating console table")]
public class MonitorQueuesOptions
{
    [Option('n',
            "namespace",
            Required = true,
            HelpText = "Fully qualified Service Bus namespace (e.g., mynamespace.servicebus.windows.net)")]
    public required string Namespace { get; set; }

    [Option('f',
            "filter",
            HelpText = "Queue name filter (wildcards * and ? supported, or contains match)")]
    public string? Filter { get; set; }

    [Option('r',
            "refresh-interval",
            Default = 5,
            HelpText = "Refresh interval in seconds (minimum: 1)")]
    public int RefreshInterval { get; set; }

    [Option('v',
            "verbose",
            Default = false,
            HelpText = "Enable verbose output")]
    public bool Verbose { get; set; }

    public string? Validate() => RefreshInterval < 1 ? "Refresh interval must be at least 1 second." : null;
}
