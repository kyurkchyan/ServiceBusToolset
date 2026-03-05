using System.Reactive.Linq;
using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed record StreamDlqCommand(string FullyQualifiedNamespace,
                                      EntityTarget Target,
                                      bool MergeSimilar = false) : ICommand<Result<DlqScanSession>>;

public sealed class StreamDlqCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<StreamDlqCommand, Result<DlqScanSession>>
{
    public ValueTask<Result<DlqScanSession>> Handle(
        StreamDlqCommand command,
        CancellationToken cancellationToken)
    {
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

        var categoryStream = cache.Connect()
                                  .Sample(TimeSpan.FromSeconds(1))
                                  .Select(_ => DlqCategoryScanner.BuildCategorySnapshot(cache, command.MergeSimilar))
                                  .StartWith(new DlqCategorySnapshot([], 0, false));

        var session = new DlqScanSession(cache, categoryStream);

        _ = Task.Run(async () =>
                     {
                         using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ScanCancellationToken);
                         await DlqCategoryScanner.FeedCacheAsync(clientFactory,
                                                                 command.FullyQualifiedNamespace,
                                                                 command.Target,
                                                                 cache,
                                                                 session,
                                                                 cancellationToken:linkedCts.Token);
                     },
                     cancellationToken);

        return new ValueTask<Result<DlqScanSession>>(Result.Success(session));
    }
}
