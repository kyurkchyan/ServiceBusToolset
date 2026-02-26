using Xunit;

namespace ServiceBusToolset.CLI.Integration.Tests.Infrastructure;

/// <summary>
/// Tests using Spectre.Console interactive mode must run sequentially
/// because Spectre.Console enforces a global exclusivity lock on live displays.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class InteractiveTestCollection
{
    public const string Name = "Interactive";
}
