using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ServiceBusToolset.Application;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;
using EntityTarget = ServiceBusToolset.Application.Common.ServiceBus.Models.EntityTarget;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public abstract class BaseIntegrationTest : IAsyncDisposable
{
    private readonly ServiceBusEmulatorFixture _fixture;
    private readonly ServiceProvider _serviceProvider;

    protected string ConnectionString => _fixture.ConnectionString;
    private string AdministrationConnectionString => _fixture.AdministrationConnectionString;

    protected string TestId { get; } = Guid.NewGuid().ToString("N")[..8];

    private readonly List<string> _createdQueues = [];
    private readonly List<(string Topic, string Subscription)> _createdSubscriptions = [];
    private readonly List<string> _createdTopics = [];
    private readonly List<string> _tempFiles = [];

    protected string GetQueue(string baseName) => $"{baseName}-{TestId}";
    protected string GetTopic(string baseName) => $"{baseName}-{TestId}";
    protected string GetSubscription(string baseName) => $"{baseName}-{TestId}";

    protected BaseIntegrationTest(
        ServiceBusEmulatorFixture fixture,
        Action<IServiceCollection>? configureServices = null)
    {
        _fixture = fixture;

        var services = new ServiceCollection();

        services.AddApplication();

        services.AddSingleton<IServiceBusClientFactory>(new EmulatorServiceBusClientFactory(fixture.ConnectionString, fixture.AdministrationConnectionString));

        configureServices?.Invoke(services);

        _serviceProvider = services.BuildServiceProvider();
    }

    protected ISender CreateSender()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISender>();
    }

    protected async Task CreateQueueAsync(string queueName)
    {
        var adminClient = new ServiceBusAdministrationClient(AdministrationConnectionString);
        await adminClient.CreateQueueAsync(queueName);
        _createdQueues.Add(queueName);
    }

    protected async Task CreateTopicAsync(string topicName)
    {
        var adminClient = new ServiceBusAdministrationClient(AdministrationConnectionString);
        await adminClient.CreateTopicAsync(topicName);
        _createdTopics.Add(topicName);
    }

    protected async Task CreateSubscriptionAsync(string topicName, string subscriptionName)
    {
        var adminClient = new ServiceBusAdministrationClient(AdministrationConnectionString);
        await adminClient.CreateSubscriptionAsync(topicName, subscriptionName);
        _createdSubscriptions.Add((topicName, subscriptionName));
    }

    protected async Task DeadLetterMessageAsync(
        EntityTarget target,
        ServiceBusMessage message,
        string reason = "TestReason",
        string description = "Integration test dead-letter")
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);

        var senderEntity = target.IsQueueMode ? target.Queue! : target.Topic!;
        await using var sender = client.CreateSender(senderEntity);
        await sender.SendMessageAsync(message);

        await using var receiver = target.IsQueueMode
                                       ? client.CreateReceiver(target.Queue!)
                                       : client.CreateReceiver(target.Topic!, target.Subscription!);

        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        await receiver.DeadLetterMessageAsync(received, reason, description);
    }

    protected async Task PopulateActiveMessagesAsync(
        string queueName,
        IEnumerable<ServiceBusMessage> messages)
    {
        await using var client = new ServiceBusClient(_fixture.ConnectionString);
        await using var sender = client.CreateSender(queueName);

        foreach (var message in messages)
        {
            await sender.SendMessageAsync(message);
        }
    }

    protected string TempFilePath(string extension = ".json")
    {
        var path = Path.Combine(Path.GetTempPath(), $"integration-{TestId}-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        var adminClient = new ServiceBusAdministrationClient(AdministrationConnectionString);

        foreach (var (topic, subscription) in _createdSubscriptions)
        {
            try { await adminClient.DeleteSubscriptionAsync(topic, subscription); }
            catch
            {
                /* entity may already be gone */
            }
        }

        foreach (var topic in _createdTopics)
        {
            try { await adminClient.DeleteTopicAsync(topic); }
            catch
            {
                /* entity may already be gone */
            }
        }

        foreach (var queue in _createdQueues)
        {
            try { await adminClient.DeleteQueueAsync(queue); }
            catch
            {
                /* entity may already be gone */
            }
        }

        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); }
            catch
            {
                /* best effort */
            }
        }

        await _serviceProvider.DisposeAsync();
    }
}
