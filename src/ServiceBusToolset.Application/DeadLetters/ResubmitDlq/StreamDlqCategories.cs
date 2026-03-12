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
                                                bool MergeSimilar = false,
                                                CategorizationSchema? Schema = null) : ICommand<Result<DlqResubmitSession>>;

public sealed class StreamDlqCategoriesCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<StreamDlqCategoriesCommand, Result<DlqResubmitSession>>
{
    /// <summary>
    /// Creates a DLQ resubmission session for the specified target, starts a background task that feeds the message cache and produces category snapshots, and returns the initialized session wrapped in a success result.
    /// </summary>
    /// <param name="command">Command containing the fully qualified namespace, target entity, merge-similar flag, and optional categorization schema.</param>
    /// <param name="cancellationToken">Cancellation token used to cancel the handler's background cache-feeding task; it will be linked with the session's internal scan cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the created <see cref="DlqResubmitSession"/> on success.</returns>
    public ValueTask<Result<DlqResubmitSession>> Handle(
        StreamDlqCategoriesCommand command,
        CancellationToken cancellationToken)
    {
        var schema = command.Schema ?? CategorizationSchema.Default;
        var resolver = new CategoryPropertyResolver();
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);
        var tracker = new ResubmitTracker();

        var categoryStream = cache.Connect()
                                  .Sample(TimeSpan.FromSeconds(1))
                                  .Select(_ => DlqCategoryScanner.BuildCategorySnapshot(cache,
                                                                                        command.MergeSimilar,
                                                                                        schema,
                                                                                        resolver))
                                  .StartWith(new DlqCategorySnapshot([],
                                                                     0,
                                                                     false,
                                                                     Schema:schema));

        var session = new DlqResubmitSession(cache,
                                             categoryStream,
                                             tracker,
                                             schema,
                                             resolver);

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
