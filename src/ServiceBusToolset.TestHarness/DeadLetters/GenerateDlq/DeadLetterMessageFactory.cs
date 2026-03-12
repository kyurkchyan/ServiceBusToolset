namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public record DeadLetterSpec(string Subject,
                             string DeadLetterReason,
                             string Body,
                             TelemetryProfile Profile = TelemetryProfile.NoOperationId);

public class DeadLetterMessageFactory
{
    private static readonly string[] ErrorCodes = ["E001", "E002", "E003", "E004", "E005"];
    private static readonly string[] Severities = ["critical", "warning", "info"];
    private static readonly string[] Environments = ["production", "staging", "development"];
    private static readonly string[] Regions = ["us-east-1", "eu-west-1", "ap-southeast-1"];

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
            [
                "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "6ba7b810-9dad-11d1-80b4-00c04fd430c8",
                "f47ac10b-58cc-4372-a567-0e02b2c3d479"
            ]),
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

    /// <summary>
    /// Generates the requested number of tier-1 DeadLetterSpec instances whose subjects and reasons are chosen from the fixed pools and whose body contains source, timestamp, tier = 1, and nested error/context properties.
    /// </summary>
    /// <param name="count">The number of tier-1 specs to create.</param>
    /// <returns>A list of DeadLetterSpec instances whose Body contains source, timestamp, tier = 1, and nested error/context properties.</returns>
    private List<DeadLetterSpec> CreateTier1Specs(int count)
    {
        var specs = new List<DeadLetterSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var subject = FixedSubjects[_random.Next(FixedSubjects.Length)];
            var reason = FixedReasons[_random.Next(FixedReasons.Length)];
            var nested = BuildNestedProperties();
            var body =
                $"{{\"source\":\"{subject}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":1,{nested}}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    /// <summary>
    /// Generates the specified number of tier-2 dead-letter specifications.
    /// </summary>
    /// <param name="count">The number of tier-2 specs to generate.</param>
    /// <returns>A list of <see cref="DeadLetterSpec"/> where each entry has a subject derived from parameterized subject templates, a reason chosen from fixed reasons, and a JSON body containing the subject, an ISO 8601 UTC timestamp, a tier value of 2, and nested error/context properties. The returned specs retain the record's default telemetry profile.</returns>
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
            var nested = BuildNestedProperties();
            var body =
                $"{{\"subject\":\"{subject}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":2,{nested}}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    /// <summary>
    /// Creates a list of tier-3 dead-letter specifications whose reasons are produced from parameterized templates and whose bodies include nested error and context properties.
    /// </summary>
    /// <param name="count">The number of tier-3 specifications to generate.</param>
    /// <returns>A list of <see cref="DeadLetterSpec"/> instances with tier set to 3, each containing a subject, a formatted reason, a timestamp, and nested error/context properties in the body.</returns>
    private List<DeadLetterSpec> CreateTier3Specs(int count)
    {
        var specs = new List<DeadLetterSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var subject = FixedSubjects[_random.Next(FixedSubjects.Length)];
            var template = ParameterizedReasons[_random.Next(ParameterizedReasons.Length)];
            var value = template.Values[_random.Next(template.Values.Length)];
            var reason = string.Format(template.Template, value);
            var nested = BuildNestedProperties();
            var body =
                $"{{\"source\":\"{subject}\",\"reason\":\"{reason}\",\"timestamp\":\"{DateTime.UtcNow:O}\",\"tier\":3,{nested}}}";
            specs.Add(new DeadLetterSpec(subject, reason, body));
        }

        return specs;
    }

    /// <summary>
    /// Builds a JSON fragment containing "error" and "context" properties with values chosen from the factory's pools.
    /// </summary>
    /// <returns>A JSON-like string fragment in the form: "error":{"code":"&lt;errorCode&gt;","severity":"&lt;severity&gt;"},"context":{"environment":"&lt;environment&gt;","region":"&lt;region&gt;"} where each placeholder is selected at random from the corresponding static arrays.</returns>
    private string BuildNestedProperties()
    {
        var errorCode = ErrorCodes[_random.Next(ErrorCodes.Length)];
        var severity = Severities[_random.Next(Severities.Length)];
        var environment = Environments[_random.Next(Environments.Length)];
        var region = Regions[_random.Next(Regions.Length)];

        return $"\"error\":{{\"code\":\"{errorCode}\",\"severity\":\"{severity}\"}},\"context\":{{\"environment\":\"{environment}\",\"region\":\"{region}\"}}";
    }

    /// <summary>
    /// Assigns telemetry profiles to each DeadLetterSpec in the provided list according to fixed percentage buckets.
    /// </summary>
    /// <param name="specs">The list of specs to modify; each element's Profile is replaced with a profile from the computed distribution.</param>
    /// <remarks>
    /// Distribution (approximate): NoOperationId 20%, NoTelemetry 30%, ExceptionOnly 12.5%, TraceOnly 12.5%, FailedDependencyOnly 12.5%, and FullTelemetry receives the remaining items. Assignment preserves list length and replaces each spec using an immutable update (`with`). The method mutates the list contents in place.
    /// </remarks>
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
