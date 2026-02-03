namespace ServiceBusToolset.CLI.Common.Commands;

public interface ICliCommand
{
    bool Verbose { get; }
    string? Validate();
}
