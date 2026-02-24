namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public enum TelemetryProfile
{
    NoOperationId,
    NoTelemetry,
    ExceptionOnly,
    TraceOnly,
    FailedDependencyOnly,
    FullTelemetry
}
