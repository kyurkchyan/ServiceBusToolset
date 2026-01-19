using CommandLine;

namespace ServiceBusToolset.Options;

[Verb("resubmit-dlq", HelpText = "Resubmit messages from a dead letter queue back to the main queue")]
public class ResubmitDlqOptions
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

    [Option("before", HelpText = "Only resubmit messages enqueued before this UTC datetime (ISO 8601 format)")]
    public DateTime? BeforeEnqueueTime { get; set; }

    [Option("dry-run", Default = false, HelpText = "Preview message count without resubmitting")]
    public bool DryRun { get; set; }

    [Option('v',
            "verbose",
            Default = false,
            HelpText = "Enable verbose output")]
    public bool Verbose { get; set; }

    [Option('i',
            "interactive",
            Default = false,
            HelpText = "Interactive mode: show categories and select which to resubmit")]
    public bool Interactive { get; set; }

    [Option("target-queue", HelpText = "Target queue to resubmit messages to (defaults to source queue)")]
    public string? TargetQueue { get; set; }

    [Option("target-topic", HelpText = "Target topic to resubmit messages to (defaults to source topic)")]
    public string? TargetTopic { get; set; }

    public bool IsQueueMode => !string.IsNullOrEmpty(Queue);
    public bool IsSubscriptionMode => !string.IsNullOrEmpty(Topic) && !string.IsNullOrEmpty(Subscription);

    /// <summary>
    /// Gets the effective target entity name for resubmission.
    /// Returns target-queue/target-topic if specified, otherwise falls back to source queue/topic.
    /// </summary>
    public string EffectiveTarget => TargetQueue ?? TargetTopic ?? Queue ?? Topic!;

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

        if (!string.IsNullOrEmpty(TargetQueue) && !string.IsNullOrEmpty(TargetTopic))
        {
            return "Cannot specify both --target-queue and --target-topic.";
        }

        return null;
    }
}
