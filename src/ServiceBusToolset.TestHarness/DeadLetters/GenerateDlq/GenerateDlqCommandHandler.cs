using Azure.Messaging.ServiceBus;
using ServiceBusToolset.TestHarness.Common.Commands;
using ServiceBusToolset.TestHarness.Common.Logging;
using ServiceBusToolset.TestHarness.Common.ServiceBus;

namespace ServiceBusToolset.TestHarness.DeadLetters.GenerateDlq;

public sealed class GenerateDlqCommandHandler(IServiceBusClientFactory clientFactory, IConsoleOutput output)
    : BaseCommandHandler<GenerateDlqCliCommand>(output)
{
    private const int BatchSize = 100;

    /// <summary>
    /// Generates the specified number of messages and dead-letters them in the target Service Bus queue, optionally generating correlated Application Insights telemetry.
    /// </summary>
    /// <param name="command">Configuration for generation, including Namespace, Queue, Count, and AppInsightsConnectionString.</param>
    /// <param name="verbose">If true, emits per-batch verbose output.</param>
    /// <param name="cancellationToken">Token to cancel the generation and dead-lettering process.</param>
    /// <returns>0 on success.</returns>
    protected override async Task<int> ExecuteCoreAsync(GenerateDlqCliCommand command, bool verbose,
                                                        CancellationToken cancellationToken = default)
    {
        Output.Info($"Generating {command.Count} dead-letter messages in '{command.Queue}'...");

        var factory = new DeadLetterMessageFactory();
        var specs = factory.CreateSpecs(command.Count);

        var hasTelemetry = !string.IsNullOrEmpty(command.AppInsightsConnectionString);
        using var telemetryGenerator = hasTelemetry
                                           ? new TelemetryGenerator(command.AppInsightsConnectionString!)
                                           : null;

        if (hasTelemetry)
        {
            Output.Info("Application Insights telemetry generation enabled.");
        }

        var random = new Random(42);
        var traceIdMap = new Dictionary<int, (string TraceId, DeadLetterSpec Spec)>();

        for (var i = 0; i < specs.Count; i++)
        {
            if (specs[i].Profile != TelemetryProfile.NoOperationId)
            {
                var traceId = GenerateHexString(random, 32);
                traceIdMap[i] = (traceId, specs[i]);
            }
        }

        await using var client = clientFactory.CreateClient(command.Namespace);
        await using var sender = client.CreateSender(command.Queue);
        await using var receiver = client.CreateReceiver(command.Queue);

        var totalProcessed = 0;
        var telemetryItems = new List<(string TraceId, DeadLetterSpec Spec)>();

        for (var batchStart = 0; batchStart < specs.Count; batchStart += BatchSize)
        {
            var batch = specs.Skip(batchStart).Take(BatchSize).ToList();

            // Build messages with unique IDs and Diagnostic-Id mappings
            var messages = new List<ServiceBusMessage>();
            var specByMessageId = new Dictionary<string, DeadLetterSpec>();
            var diagnosticIdByMessageId = new Dictionary<string, string>();

            for (var i = 0; i < batch.Count; i++)
            {
                var spec = batch[i];
                var messageId = Guid.NewGuid().ToString();
                var msg = new ServiceBusMessage(spec.Body)
                {
                    Subject = spec.Subject,
                    MessageId = messageId
                };

                specByMessageId[messageId] = spec;

                var globalIndex = batchStart + i;
                if (traceIdMap.TryGetValue(globalIndex, out var entry))
                {
                    var spanId = GenerateHexString(random, 16);
                    diagnosticIdByMessageId[messageId] = $"00-{entry.TraceId}-{spanId}-01";
                }

                messages.Add(msg);
            }

            await sender.SendMessagesAsync(messages, cancellationToken);

            // Receive with retry loop to drain all sent messages
            var pending = new HashSet<string>(specByMessageId.Keys);
            var maxAttempts = 3;

            for (var attempt = 0; attempt < maxAttempts && pending.Count > 0; attempt++)
            {
                var received = await receiver.ReceiveMessagesAsync(pending.Count,
                                                                   TimeSpan.FromSeconds(10),
                                                                   cancellationToken);
                if (received.Count == 0)
                {
                    break;
                }

                var deadLetterTasks = received.Select(msg =>
                {
                    var reason = specByMessageId.TryGetValue(msg.MessageId, out var spec)
                                     ? spec.DeadLetterReason
                                     : "TestGenerated";

                    var task = diagnosticIdByMessageId.TryGetValue(msg.MessageId, out var diagnosticId)
                                   ? receiver.DeadLetterMessageAsync(msg,
                                                                     new Dictionary<string, object> { ["Diagnostic-Id"] = diagnosticId },
                                                                     reason,
                                                                     "Generated by TestHarness for testing",
                                                                     cancellationToken)
                                   : receiver.DeadLetterMessageAsync(msg,
                                                                     reason,
                                                                     "Generated by TestHarness for testing",
                                                                     cancellationToken);

                    return (Task:task, msg.MessageId);
                }).ToList();

                await Task.WhenAll(deadLetterTasks.Select(t => t.Task));

                foreach (var (_, messageId) in deadLetterTasks)
                {
                    pending.Remove(messageId);
                    totalProcessed++;
                }

                Output.Progress($"Processed {totalProcessed}/{specs.Count} messages...");
            }

            // Collect telemetry items for this batch (flush later)
            if (telemetryGenerator != null)
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    var globalIndex = batchStart + i;
                    if (traceIdMap.TryGetValue(globalIndex, out var entry) &&
                        entry.Spec.Profile is not (TelemetryProfile.NoOperationId or TelemetryProfile.NoTelemetry))
                    {
                        telemetryItems.Add(entry);
                    }
                }
            }

            Output.Verbose($"  Batch {(batchStart / BatchSize) + 1}: sent {batch.Count}, dead-lettered {batch.Count - pending.Count}",
                           verbose);
        }

        // Flush all telemetry at once
        if (telemetryGenerator != null && telemetryItems.Count > 0)
        {
            Output.Info($"Sending {telemetryItems.Count} correlated telemetry items to Application Insights...");
            var (itemCount, errors) = await telemetryGenerator.GenerateTelemetryAsync(telemetryItems);
            if (errors > 0)
            {
                Output.Error($"App Insights ingestion reported {errors} errors out of {itemCount} items.");
            }
            else
            {
                Output.Success($"Successfully sent {itemCount} telemetry items to Application Insights.");
            }
        }

        Console.WriteLine();
        Output.Success($"Successfully dead-lettered {totalProcessed} messages.");
        Console.WriteLine();

        PrintSummary(specs);

        if (hasTelemetry)
        {
            PrintTelemetryProfileDistribution(specs);
            Console.WriteLine();
            Output.Warning("Note: Application Insights telemetry may take 2-5 minutes to become available for querying.");
        }

        return 0;
    }

    private void PrintSummary(List<DeadLetterSpec> specs)
    {
        var categories = specs
                         .GroupBy(s => new
                         {
                             s.Subject,
                             s.DeadLetterReason
                         })
                         .OrderByDescending(g => g.Count())
                         .Take(20)
                         .Select(g => new[]
                         {
                             g.Key.Subject,
                             g.Key.DeadLetterReason,
                             g.Count().ToString()
                         });

        Output.Table(["Subject", "Dead Letter Reason", "Count"], categories);
    }

    private void PrintTelemetryProfileDistribution(List<DeadLetterSpec> specs)
    {
        var distribution = specs
                           .GroupBy(s => s.Profile)
                           .OrderBy(g => g.Key)
                           .Select(g => new[]
                           {
                               g.Key.ToString(),
                               g.Count().ToString(),
                               $"{100.0 * g.Count() / specs.Count:F1}%"
                           });

        Output.Table(["Telemetry Profile", "Count", "Percentage"], distribution);
    }

    private static string GenerateHexString(Random random, int length)
    {
        Span<byte> bytes = stackalloc byte[length / 2];
        random.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
