namespace ServiceBusToolset.Commands;

public interface ICommand<in TOptions>
{
    Task<int> ExecuteAsync(TOptions options, CancellationToken cancellationToken = default);
}
