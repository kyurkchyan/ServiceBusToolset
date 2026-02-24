namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public record DeadLetterSpec(string Subject, string DeadLetterReason, string Body, TelemetryProfile Profile = TelemetryProfile.NoOperationId);

public class DeadLetterMessageFactory
{
    private static readonly string[] FixedSubjects =
    [
        "OrderProcessor",
        "PaymentHandler",
        "UserRegistration",
        "InventorySync",
        "NotificationService",
        "ShippingCalculator",
        "InvoiceGenerator",
        "ReportScheduler",
        "AuditLogger",
        "CacheInvalidator"
    ];

    private static readonly string[] FixedReasons =
    [
        "MaxDeliveryCountExceeded",
        "TTLExpiredException",
        "HeaderSizeExceeded",
        "MessageSizeExceeded",
        "SessionIdMismatch",
        "InvalidMessageFormat",
        "DeserializationError",
        "AuthorizationFailed",
        "DuplicateDetected",
        "ProcessingTimeout"
    ];

    private static readonly (string Template, string[] Values)[] ParameterizedSubjects =
    [
        ("Error processing order {0}", ["ORD-1001", "ORD-2002", "ORD-3003", "ORD-4004", "ORD-5005"]),
        ("Could not create user with ID {0}",
            ["3f2504e0-4f89-11d3-9a0c-0305e82c3301", "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
             "f47ac10b-58cc-4372-a567-0e02b2c3d479"]),
        ("Timeout for service {0}", ["InventoryAPI", "PaymentGateway", "UserProfileService", "NotificationHub"]),
        ("Connection refused for host {0}",
            ["db-primary.internal", "cache-01.internal", "queue-broker.internal", "search-node.internal"]),
        ("Failed to process entity {0} in region {1}",
            ["ENT-100|us-east-1", "ENT-200|eu-west-1", "ENT-300|ap-southeast-1"])
    ];

    private static readonly (string Template, string[] Values)[] ParameterizedReasons =
    [
        ("Retry exhausted after {0} attempts", ["3", "5", "10", "15"]),
        ("Timeout after {0}ms", ["5000", "10000", "30000"])
    ];

    private readonly Random _random;

    public DeadLetterMessageFactory(int seed = 42)
    {
        _random = new Random(seed);
    }

    public List<DeadLetterSpec> CreateSpecs(int totalCount)
    {
        var tier1Count = (int)(totalCount * 0.4);
        var tier2Count = (int)(totalCount * 0.4);
        var tier3Count = totalCount - tier1Count - tier2Count;

        var specs = new List<DeadLetterSpec>(totalCount);

        specs.AddRange(CreateTier1Specs(tier1Count));
        specs.AddRange(CreateTier2Specs(tier2Count));
        specs.AddRange(CreateTier3Specs(tier3Count));

        AssignTelemetryProfiles(specs);
        Shuffle(specs);

        return specs;
    }

    private List<DeadLetterSpec> CreateTier1Specs(int count)
    {
        var specs = new List<DeadLetterSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var subject = FixedSubjects[_random.Next(FixedSubjects.Length)];
            var reason = FixedReasons[_random.Next(FixedReasons.Length)];
            var body = $"{{\"source\":\"{subject}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":1}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    private List<DeadLetterSpec> CreateTier2Specs(int count)
    {
        var specs = new List<DeadLetterSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var template = ParameterizedSubjects[_random.Next(ParameterizedSubjects.Length)];
            var value = template.Values[_random.Next(template.Values.Length)];

            string subject;
            if (template.Template.Contains("{1}"))
            {
                var parts = value.Split('|');
                subject = string.Format(template.Template, parts[0], parts[1]);
            }
            else
            {
                subject = string.Format(template.Template, value);
            }

            var reason = FixedReasons[_random.Next(FixedReasons.Length)];
            var body = $"{{\"subject\":\"{subject}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":2}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    private List<DeadLetterSpec> CreateTier3Specs(int count)
    {
        var specs = new List<DeadLetterSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var subject = FixedSubjects[_random.Next(FixedSubjects.Length)];
            var template = ParameterizedReasons[_random.Next(ParameterizedReasons.Length)];
            var value = template.Values[_random.Next(template.Values.Length)];
            var reason = string.Format(template.Template, value);
            var body = $"{{\"source\":\"{subject}\",\"reason\":\"{reason}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":3}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    private void AssignTelemetryProfiles(List<DeadLetterSpec> specs)
    {
        var totalCount = specs.Count;
        var noOpIdCount = (int)(totalCount * 0.20);
        var noTelemetryCount = (int)(totalCount * 0.30);
        var exceptionCount = (int)(totalCount * 0.125);
        var traceCount = (int)(totalCount * 0.125);
        var failedDepCount = (int)(totalCount * 0.125);
        // FullTelemetry gets the remainder
        var fullCount = totalCount - noOpIdCount - noTelemetryCount - exceptionCount - traceCount - failedDepCount;

        var profileQueue = new List<TelemetryProfile>(totalCount);
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.NoOperationId, noOpIdCount));
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.NoTelemetry, noTelemetryCount));
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.ExceptionOnly, exceptionCount));
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.TraceOnly, traceCount));
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.FailedDependencyOnly, failedDepCount));
        profileQueue.AddRange(Enumerable.Repeat(TelemetryProfile.FullTelemetry, fullCount));

        for (var i = 0; i < specs.Count; i++)
        {
            specs[i] = specs[i] with { Profile = profileQueue[i] };
        }
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
