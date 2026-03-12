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
                                      bool MergeSimilar = false,
                                      CategorizationSchema? Schema = null) : ICommand<Result<DlqScanSession>>;

public sealed class StreamDlqCommandHandler(IServiceBusClientFactory clientFactory)
    : ICommandHandler<StreamDlqCommand, Result<DlqScanSession>>
{
    /// <summary>
    /// Initializes a DLQ scan session for the specified Service Bus target and starts background processing to populate its message cache and category stream.
    /// </summary>
    /// <param name="command">Configuration for the scan including the fully qualified namespace, target entity, whether to merge similar categories, and an optional categorization schema.</param>
    /// <param name="cancellationToken">Token used to cancel the background scanning task.</param>
    /// <returns>A <see cref="Result{DlqScanSession}"/> containing the initialized <see cref="DlqScanSession"/>; background cache feeding and category scanning are started for the session.</returns>
    public ValueTask<Result<DlqScanSession>> Handle(
        StreamDlqCommand command,
        CancellationToken cancellationToken)
    {
        var schema = command.Schema ?? CategorizationSchema.Default;
        var resolver = new CategoryPropertyResolver();
        var cache = new ReactiveMessageCache<ServiceBusReceivedMessage, long>(m => m.SequenceNumber);

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

        var session = new DlqScanSession(cache,
                                         categoryStream,
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
                                                                 cancellationToken:linkedCts.Token);
                     },
                     cancellationToken);

        return new ValueTask<Result<DlqScanSession>>(Result.Success(session));
    }
}
