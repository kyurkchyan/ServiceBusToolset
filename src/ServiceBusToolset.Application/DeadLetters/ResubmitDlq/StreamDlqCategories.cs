using System.Reactive.Linq;
using Ardalis.Result;
using Azure.Messaging.ServiceBus;
using Mediator;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using ServiceBusToolset.Application.DeadLetters.Common;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Application.DeadLetters.ResubmitDlq;

public sealed record StreamDlqCategoriesCommand(string FullyQualifiedNamespace,
                                                EntityTarget Target,
                                                bool MergeSimilar = false) : ICommand<Result<DlqResubmitSession>>;

public sealed class StreamDlqCategoriesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<StreamDlqCategoriesCommand, Result<DlqResubmitSession>>
{
    public ValueTask<Result<DlqResubmitSession>> Handle(
        StreamDlqCategoriesCommand command,
        CancellationToken cancellationToken)
    {
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var tracker = new ResubmitTracker();

        var categoryStream = cache.Connect()
                                  .Sample(TimeSpan.FromSeconds(1))
                                  .Select(_ => DlqCategoryScanner.BuildCategorySnapshot(cache, command.MergeSimilar))
                                  .StartWith(new DlqCategorySnapshot([], 0, false));

        var session = new DlqResubmitSession(cache, categoryStream, tracker);

        _ = Task.Run(async () =>
                     {
                         using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ScanCancellationToken);
                         await DlqCategoryScanner.FeedCacheAsync(clientFactory,
                                                                 command.FullyQualifiedNamespace,
                                                                 command.Target,
                                                                 cache,
                                                                 session,
                                                                 m => !tracker.WasResubmitted(m.MessageId),
                                                                 linkedCts.Token);
                     },
                     cancellationToken);

        return new ValueTask<Result<DlqResubmitSession>>(Result.Success(session));
    }
}
