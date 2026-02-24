namespace ServiceBusToolset.TestHarness.Common.Commands;

public interface ICliCommand
{
    bool Verbose { get; }
    string? Validate();
}
