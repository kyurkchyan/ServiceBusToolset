namespace ServiceBusToolset.CLI.Common.Commands;

public interface ICommandHandler<in TCommand>
{
    Task<int> ExecuteAsync(TCommand command,
                           bool verbose = false,
                           CancellationToken cancellationToken = default);
}
