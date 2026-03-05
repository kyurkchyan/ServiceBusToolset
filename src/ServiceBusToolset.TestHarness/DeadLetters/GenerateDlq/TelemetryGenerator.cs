using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public sealed class TelemetryGenerator : IDisposable
{
    private static readonly Dictionary<string, (string TypeName, string Message)> ExceptionsBySubject = new()
    {
        ["OrderProcessor"] = ("System.InvalidOperationException", "Order state transition invalid: cannot move from 'Cancelled' to 'Shipped'"),
        ["PaymentHandler"] = ("System.Net.Http.HttpRequestException", "Payment gateway returned 502 Bad Gateway"),
        ["UserRegistration"] = ("System.ArgumentException", "Email format validation failed for input"),
        ["InventorySync"] = ("System.TimeoutException", "Inventory service did not respond within 30s"),
        ["NotificationService"] = ("System.InvalidOperationException", "Notification template 'order_confirm_v2' not found"),
        ["ShippingCalculator"] = ("System.ArithmeticException", "Negative weight value encountered during rate calculation"),
        ["InvoiceGenerator"] = ("System.FormatException", "Currency code 'XYZ' is not a valid ISO 4217 code"),
        ["ReportScheduler"] = ("System.UnauthorizedAccessException", "Service principal lacks 'Reports.ReadWrite' permission"),
        ["AuditLogger"] = ("System.IO.IOException", "Audit log partition is full, cannot append entry"),
        ["CacheInvalidator"] = ("System.TimeoutException", "Redis connection timed out after 5000ms")
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

    private readonly HttpClient _httpClient = new();
    private readonly string _ingestionEndpoint;
    private readonly string _instrumentationKey;
    private readonly Random _random;

    public TelemetryGenerator(string connectionString, int seed = 42)
    {
        var parts = ParseConnectionString(connectionString);
        _instrumentationKey = parts.InstrumentationKey;
        _ingestionEndpoint = parts.IngestionEndpoint.TrimEnd('/') + "/v2/track";
        _random = new Random(seed);
    }

    public async Task<(int ItemCount, int Errors)> GenerateTelemetryAsync(
        IReadOnlyList<(string TraceId, DeadLetterSpec Spec)> items)
    {
        var envelopes = new List<JsonObject>();

        foreach (var (traceId, spec) in items)
        {
            switch (spec.Profile)
            {
                case TelemetryProfile.ExceptionOnly:
                    envelopes.Add(BuildException(traceId, spec));
                    break;
                case TelemetryProfile.TraceOnly:
                    envelopes.AddRange(BuildTraces(traceId, spec));
                    break;
                case TelemetryProfile.FailedDependencyOnly:
                    envelopes.AddRange(BuildDependencies(traceId, spec));
                    break;
                case TelemetryProfile.FullTelemetry:
                    envelopes.Add(BuildException(traceId, spec));
                    envelopes.AddRange(BuildTraces(traceId, spec));
                    envelopes.AddRange(BuildDependencies(traceId, spec));
                    break;
            }
        }

        // Send as newline-delimited JSON
        var sb = new StringBuilder();
        foreach (var envelope in envelopes)
        {
            sb.AppendLine(envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }

        var content = new StringContent(sb.ToString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream");

        var response = await _httpClient.PostAsync(_ingestionEndpoint, content);
        var errors = 0;

        if (!response.IsSuccessStatusCode)
        {
            errors = envelopes.Count;
        }
        else
        {
            // Parse response to count individual item errors
            var responseBody = await response.Content.ReadAsStringAsync();
            try
            {
                var result = JsonDocument.Parse(responseBody);
                if (result.RootElement.TryGetProperty("errors", out var errorsArray))
                {
                    errors = errorsArray.GetArrayLength();
                }
            }
            catch
            {
                // Response parsing failed, assume success
            }
        }

        return (envelopes.Count, errors);
    }

    private JsonObject BuildException(string traceId, DeadLetterSpec spec)
    {
        var (typeName, message) = ExceptionsBySubject.GetValueOrDefault(spec.Subject,
                                                                        ("System.Exception", $"Unhandled error in {spec.Subject}"));

        return BuildEnvelope("Microsoft.ApplicationInsights.Exception",
                             traceId,
                             spec.Subject,
                             DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 30)),
                             "ExceptionData",
                             new JsonObject
                             {
                                 ["ver"] = 2,
                                 ["exceptions"] = new JsonArray
                                 {
                                     new JsonObject
                                     {
                                         ["typeName"] = typeName,
                                         ["message"] = message,
                                         ["hasFullStack"] = false
                                     }
                                 }
                             });
    }

    private List<JsonObject> BuildTraces(string traceId, DeadLetterSpec spec)
    {
        var result = new List<JsonObject>();
        var traceCount = _random.Next(2, 5);

        for (var i = 0; i < traceCount; i++)
        {
            var template = TraceMessages[_random.Next(TraceMessages.Length)];
            var message = string.Format(template, _random.Next(1, 20), _random.Next(500, 5000));
            var severityLevel = _random.Next(2, 5); // Warning, Error, Critical

            result.Add(BuildEnvelope("Microsoft.ApplicationInsights.Message",
                                     traceId,
                                     spec.Subject,
                                     DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 60)),
                                     "MessageData",
                                     new JsonObject
                                     {
                                         ["ver"] = 2,
                                         ["message"] = message,
                                         ["severityLevel"] = severityLevel
                                     }));
        }

        return result;
    }

    private List<JsonObject> BuildDependencies(string traceId, DeadLetterSpec spec)
    {
        var result = new List<JsonObject>();
        var depCount = _random.Next(1, 3);

        for (var i = 0; i < depCount; i++)
        {
            var template = DependencyTemplates[_random.Next(DependencyTemplates.Length)];
            var durationMs = _random.Next(100, 30000);
            var duration = TimeSpan.FromMilliseconds(durationMs);

            result.Add(BuildEnvelope("Microsoft.ApplicationInsights.RemoteDependency",
                                     traceId,
                                     spec.Subject,
                                     DateTimeOffset.UtcNow.AddSeconds(-_random.Next(5, 45)),
                                     "RemoteDependencyData",
                                     new JsonObject
                                     {
                                         ["ver"] = 2,
                                         ["name"] = $"{spec.Subject} dependency call",
                                         ["type"] = template.Type,
                                         ["target"] = template.Target,
                                         ["resultCode"] = template.ResultCode,
                                         ["success"] = false,
                                         ["duration"] = duration.ToString()
                                     }));
        }

        return result;
    }

    private JsonObject BuildEnvelope(string name, string traceId, string operationName,
                                     DateTimeOffset timestamp, string baseType, JsonObject baseData) =>
        new()
        {
            ["name"] = name,
            ["time"] = timestamp.UtcDateTime.ToString("O"),
            ["iKey"] = _instrumentationKey,
            ["tags"] = new JsonObject
            {
                ["ai.operation.id"] = traceId,
                ["ai.operation.name"] = operationName
            },
            ["data"] = new JsonObject
            {
                ["baseType"] = baseType,
                ["baseData"] = baseData
            }
        };

    private static (string InstrumentationKey, string IngestionEndpoint) ParseConnectionString(string connectionString)
    {
        string? instrumentationKey = null;
        string? ingestionEndpoint = null;

        foreach (var part in connectionString.Split(';'))
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length != 2)
            {
                continue;
            }

            var key = kvp[0].Trim();
            var value = kvp[1].Trim();

            if (key.Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase))
            {
                instrumentationKey = value;
            }
            else if (key.Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                ingestionEndpoint = value;
            }
        }

        if (string.IsNullOrEmpty(instrumentationKey))
        {
            throw new ArgumentException("Connection string must contain InstrumentationKey.");
        }

        // Default to global endpoint if not specified
        ingestionEndpoint ??= "https://dc.services.visualstudio.com";

        return (instrumentationKey, ingestionEndpoint);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
