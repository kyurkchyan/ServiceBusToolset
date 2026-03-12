using CommandLine;
using ServiceBusToolset.CLI.Common.Commands;

namespace ServiceBusToolset.CLI.DeadLetters.PurgeDlq;

[Verb("purge-dlq", HelpText = "Purge messages from a dead letter queue")]
public class PurgeDlqCliCommand : ICliCommand
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

    [Option("before", HelpText = "Only purge messages enqueued before this UTC datetime (ISO 8601 format)")]
    public DateTime? BeforeEnqueueTime { get; set; }

    [Option("dry-run", Default = false, HelpText = "Preview message count without purging")]
    public bool DryRun { get; set; }

    [Option('v',
            "verbose",
            Default = false,
            HelpText = "Enable verbose output")]
    public bool Verbose { get; set; }

    [Option('i',
            "interactive",
            Default = false,
            HelpText = "Interactive mode: show categories and select which to purge")]
    public bool Interactive { get; set; }

    [Option("merge-similar",
            Default = false,
            HelpText = "Merge similar DLQ categories using LCS-based clustering (interactive mode only)")]
    public bool MergeSimilar { get; set; }

    [Option("categorize-by",
            Separator = ',',
            HelpText = "Properties to categorize by. #Prop for system (falls back to application properties), $Prop for body. Default: #Subject,#DeadLetterReason")]
    public IEnumerable<string>? CategorizeBy { get; set; }

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
