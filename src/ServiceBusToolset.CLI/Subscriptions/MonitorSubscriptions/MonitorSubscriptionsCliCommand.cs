using CommandLine;
using ServiceBusToolset.CLI.Common.Commands;

namespace ServiceBusToolset.CLI.Subscriptions.MonitorSubscriptions;

[Verb("monitor-subscriptions", HelpText = "Monitor Service Bus topic subscription statistics in a live-updating console table")]
public class MonitorSubscriptionsCliCommand : ICliCommand
{
    [Option('n',
            "namespace",
            Required = true,
            HelpText = "Fully qualified Service Bus namespace (e.g., mynamespace.servicebus.windows.net)")]
    public required string Namespace { get; set; }

    [Option('t',
            "topic",
            HelpText = "Topic name filter (wildcards * and ? supported, or contains match)")]
    public string? TopicFilter { get; set; }

    [Option('s',
            "subscription",
            HelpText = "Subscription name filter (wildcards * and ? supported, or contains match)")]
    public string? SubscriptionFilter { get; set; }

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
