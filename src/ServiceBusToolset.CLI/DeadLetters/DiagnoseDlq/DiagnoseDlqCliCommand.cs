using CommandLine;
using ServiceBusToolset.CLI.Common.Commands;

namespace ServiceBusToolset.CLI.DeadLetters.DiagnoseDlq;

[Verb("diagnose-dlq", HelpText = "Diagnose DLQ messages by correlating with Application Insights telemetry")]
public class DiagnoseDlqCliCommand : ICliCommand
{
    [Option('n',
            "namespace",
            Required = true,
            HelpText = "Fully qualified Service Bus namespace (e.g., mynamespace.servicebus.windows.net)")]
    public required string Namespace { get; set; }

    [Option('q',
            "queue",
            SetName = "queue",
            HelpText = "Queue name")]
    public string? Queue { get; set; }

    [Option('t',
            "topic",
            SetName = "subscription",
            HelpText = "Topic name")]
    public string? Topic { get; set; }

    [Option('s',
            "subscription",
            SetName = "subscription",
            HelpText = "Subscription name")]
    public string? Subscription { get; set; }

    [Option('a',
            "app-insights",
            HelpText = "Application Insights resource ID (e.g., /subscriptions/.../resourceGroups/.../providers/microsoft.insights/components/...). If omitted, basic diagnostic using dead letter reasons is performed.")]
    public string? AppInsightsResourceId { get; set; }

    [Option('o',
            "output",
            HelpText = "Output JSON file path (optional, prints to console if not specified)")]
    public string? OutputFile { get; set; }

    [Option("before", HelpText = "Only include messages enqueued before this UTC datetime (ISO 8601 format)")]
    public DateTime? BeforeEnqueueTime { get; set; }

    [Option("max-messages",
            Default = 1000,
            HelpText = "Maximum number of messages to diagnose")]
    public int MaxMessages { get; set; }

    [Option('i',
            "interactive",
            Default = false,
            HelpText = "Interactive mode: show categories and select which to diagnose")]
    public bool Interactive { get; set; }

    [Option("merge-similar",
            Default = false,
            HelpText = "Merge similar DLQ categories using LCS-based clustering (interactive mode only)")]
    public bool MergeSimilar { get; set; }

    [Option("categorize-by",
            Separator = ',',
            HelpText = "Properties to categorize by. #Prop for system (falls back to application properties), $Prop for body. Default: #Subject,#DeadLetterReason")]
    public IEnumerable<string>? CategorizeBy { get; set; }

    [Option('v',
            "verbose",
            Default = false,
            HelpText = "Enable verbose output")]
    public bool Verbose { get; set; }

    public bool IsQueueMode => !string.IsNullOrEmpty(Queue);
    public bool IsSubscriptionMode => !string.IsNullOrEmpty(Topic) && !string.IsNullOrEmpty(Subscription);

    public string? Validate()
    {
        if (!IsQueueMode && !IsSubscriptionMode)
        {
            return "Either --queue or both --topic and --subscription must be specified.";
        }

        if (!string.IsNullOrEmpty(Topic) && string.IsNullOrEmpty(Subscription))
        {
            return "When --topic is specified, --subscription is also required.";
        }

        if (!string.IsNullOrEmpty(Subscription) && string.IsNullOrEmpty(Topic))
        {
            return "When --subscription is specified, --topic is also required.";
        }

        if (MaxMessages <= 0)
        {
            return "--max-messages must be greater than 0.";
        }

        if (CategorizeBy != null)
        {
            foreach (var token in CategorizeBy)
            {
                if (string.IsNullOrWhiteSpace(token) || token.Trim().Length < 2 || (token.Trim()[0] != '#' && token.Trim()[0] != '$'))
                {
                    return $"Invalid --categorize-by token '{token}'. Each token must start with '#' (system) or '$' (body) followed by a property name.";
                }
            }
        }

        return null;
    }
}
