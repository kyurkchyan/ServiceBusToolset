using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public sealed class TelemetryGenerator : IDisposable
{
    private static readonly Dictionary<string, Func<Exception>> ExceptionsBySubject = new()
    {
        ["OrderProcessor"] = () => new InvalidOperationException("Order state transition invalid: cannot move from 'Cancelled' to 'Shipped'"),
        ["PaymentHandler"] = () => new HttpRequestException("Payment gateway returned 502 Bad Gateway"),
        ["UserRegistration"] = () => new ArgumentException("Email format validation failed for input"),
        ["InventorySync"] = () => new TimeoutException("Inventory service did not respond within 30s"),
        ["NotificationService"] = () => new InvalidOperationException("Notification template 'order_confirm_v2' not found"),
        ["ShippingCalculator"] = () => new ArithmeticException("Negative weight value encountered during rate calculation"),
        ["InvoiceGenerator"] = () => new FormatException("Currency code 'XYZ' is not a valid ISO 4217 code"),
        ["ReportScheduler"] = () => new UnauthorizedAccessException("Service principal lacks 'Reports.ReadWrite' permission"),
        ["AuditLogger"] = () => new IOException("Audit log partition is full, cannot append entry"),
        ["CacheInvalidator"] = () => new TimeoutException("Redis connection timed out after 5000ms")
    };

    private static readonly string[] TraceMessages =
    [
        "Retry attempt {0} of 3 failed, backing off for {1}ms",
        "Circuit breaker opened after {0} consecutive failures",
        "Rate limit exceeded: {0} requests in the last 60s",
        "Fallback handler invoked due to primary service unavailability",
        "Message processing deadline exceeded by {0}ms",
        "Connection pool exhausted, {0} waiters queued",
        "Schema validation warning: unexpected field '{0}' in payload",
        "Degraded mode active: serving stale data from cache"
    ];

    private static readonly (string Type, string Target, string ResultCode)[] DependencyTemplates =
    [
        ("SQL", "db-primary.database.windows.net | orders-db", "Timeout"),
        ("SQL", "db-replica.database.windows.net | users-db", "-1"),
        ("HTTP", "https://api.payment-provider.com/v2/charge", "500"),
        ("HTTP", "https://inventory-service.internal/api/stock", "503"),
        ("HTTP", "https://notification-hub.internal/api/send", "408"),
        ("Redis", "cache-primary.redis.cache.windows.net:6380", "-1"),
        ("Redis", "cache-session.redis.cache.windows.net:6380", "Timeout"),
        ("Azure Service Bus", "orders-topic/subscriptions/processor", "ServiceBusy")
    ];

    private readonly TelemetryClient _client;
    private readonly Random _random;

    public TelemetryGenerator(string connectionString, int seed = 42)
    {
        var config = new TelemetryConfiguration { ConnectionString = connectionString };
        _client = new TelemetryClient(config);
        _random = new Random(seed);
    }

    public void GenerateTelemetry(IReadOnlyList<(string TraceId, DeadLetterSpec Spec)> items)
    {
        foreach (var (traceId, spec) in items)
        {
            switch (spec.Profile)
            {
                case TelemetryProfile.ExceptionOnly:
                    SendException(traceId, spec);
                    break;
                case TelemetryProfile.TraceOnly:
                    SendTraces(traceId, spec);
                    break;
                case TelemetryProfile.FailedDependencyOnly:
                    SendFailedDependencies(traceId, spec);
                    break;
                case TelemetryProfile.FullTelemetry:
                    SendException(traceId, spec);
                    SendTraces(traceId, spec);
                    SendFailedDependencies(traceId, spec);
                    break;
            }
        }

        _client.Flush();
    }

    private void SendException(string traceId, DeadLetterSpec spec)
    {
        var exceptionFactory = ExceptionsBySubject.GetValueOrDefault(spec.Subject)
                               ?? (() => new Exception($"Unhandled error in {spec.Subject}"));

        var telemetry = new ExceptionTelemetry(exceptionFactory())
        {
            Timestamp = DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 30))
        };
        telemetry.Context.Operation.Id = traceId;
        telemetry.Context.Operation.Name = spec.Subject;

        _client.TrackException(telemetry);
    }

    private void SendTraces(string traceId, DeadLetterSpec spec)
    {
        var traceCount = _random.Next(2, 5);
        for (var i = 0; i < traceCount; i++)
        {
            var template = TraceMessages[_random.Next(TraceMessages.Length)];
            var message = string.Format(template, _random.Next(1, 20), _random.Next(500, 5000));

            var severity = (SeverityLevel)_random.Next(2, 5); // Warning, Error, Critical
            var telemetry = new TraceTelemetry(message, severity)
            {
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 60))
            };
            telemetry.Context.Operation.Id = traceId;
            telemetry.Context.Operation.Name = spec.Subject;

            _client.TrackTrace(telemetry);
        }
    }

    private void SendFailedDependencies(string traceId, DeadLetterSpec spec)
    {
        var depCount = _random.Next(1, 3);
        for (var i = 0; i < depCount; i++)
        {
            var template = DependencyTemplates[_random.Next(DependencyTemplates.Length)];

            var telemetry = new DependencyTelemetry(
                template.Type,
                template.Target,
                $"{spec.Subject} dependency call",
                data: null,
                DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 45)),
                TimeSpan.FromMilliseconds(_random.Next(100, 30000)),
                template.ResultCode,
                success: false);
            telemetry.Context.Operation.Id = traceId;
            telemetry.Context.Operation.Name = spec.Subject;

            _client.TrackDependency(telemetry);
        }
    }

    public void Dispose()
    {
        _client.Flush();
        // Allow time for the telemetry to be transmitted
        Task.Delay(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }
}
