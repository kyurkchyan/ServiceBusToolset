namespace ServiceBusToolset.Application.DeadLetters.Common;

/// <summary>
/// Represents a unique key for categorizing DLQ messages by their label and dead letter reason.
/// </summary>
public sealed record DlqCategoryKey(string Label, string DeadLetterReason)
{
    /// <summary>
    /// Creates a category key from a message's subject and dead letter reason,
    /// using "(none)" for null values.
    /// </summary>
    public static DlqCategoryKey FromMessage(string? subject, string? deadLetterReason)
        => new(subject ?? "(none)", deadLetterReason ?? "(none)");
}
