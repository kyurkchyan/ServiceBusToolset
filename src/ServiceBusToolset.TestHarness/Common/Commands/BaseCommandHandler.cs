using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.TestHarness.Common.Logging;

namespace ServiceBusToolset.TestHarness.Common.Commands;

public abstract class BaseCommandHandler<TCommand>(IConsoleOutput output) : ICommandHandler<TCommand>
    where TCommand : ICliCommand
{
    protected IConsoleOutput Output => output;

    public async Task<int> ExecuteAsync(TCommand command,
                                        bool verbose = false,
                                        CancellationToken cancellationToken = default)
    {
        var validationError = command.Validate();
        if (validationError != null)
        {
            Output.Error(validationError);
            return 1;
        }

        try
        {
            return await ExecuteCoreAsync(command, verbose, cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            Output.Error($"Authentication failed: {ex.Message}");
            Output.Error("Ensure you are logged in with 'az login' or have valid environment credentials.");
            return 1;
        }
        catch (ServiceBusException ex)
        {
            Output.Error($"Service Bus error: {ex.Message}");
            Output.Verbose($"Reason: {ex.Reason}", verbose);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Output.Warning("\nOperation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Output.Error($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
            Output.Verbose(ex.ToString(), verbose);
            return 1;
        }
    }

    protected abstract Task<int> ExecuteCoreAsync(TCommand command, bool verbose, CancellationToken cancellationToken = default);
}
