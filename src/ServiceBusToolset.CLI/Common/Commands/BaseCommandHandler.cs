using Ardalis.Result;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.CLI.Common.Logging;

namespace ServiceBusToolset.CLI.Common.Commands;

public abstract class BaseCommandHandler(IConsoleOutput output)
{
    protected IConsoleOutput Output => output;

    protected async Task<int> ExecuteWithExceptionHandling(
        Func<Task<int>> action,
        bool verbose = false)
    {
        try
        {
            return await action();
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
    }

    protected int HandleResult<T>(Result<T> result, Action<T> onSuccess)
    {
        if (result.IsSuccess)
        {
            onSuccess(result.Value);
            return 0;
        }

        foreach (var error in result.Errors)
        {
            Output.Error(error);
        }

        return 1;
    }

    protected IProgress<int> CreateProgressReporter(string messageTemplate)
    {
        return new Progress<int>(count =>
        {
            Output.Progress(string.Format(messageTemplate, count));
        });
    }
}
