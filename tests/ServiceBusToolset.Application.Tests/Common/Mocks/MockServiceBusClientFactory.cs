using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NSubstitute;
using ServiceBusToolset.Application.Common.ServiceBus.Abstractions;

namespace ServiceBusToolset.Application.Tests.Common.Mocks;

/// <summary>
/// Helper class for creating configured mocks of IServiceBusClientFactory and related types.
/// </summary>
public class MockServiceBusClientFactory
{
    private IServiceBusClientFactory Factory { get; }
    public ServiceBusClient Client { get; }
    private ServiceBusAdministrationClient AdminClient { get; }
    public ServiceBusReceiver Receiver { get; }
    public ServiceBusSender Sender { get; }

    private readonly List<ServiceBusReceivedMessage> _messagesToReturn = [];
    private int _peekCallCount;
    private int _receiveCallCount;
    private bool _receiverConfigured;
    private bool _senderConfigured;

    private MockServiceBusClientFactory()
    {
        Factory = Substitute.For<IServiceBusClientFactory>();
        Client = Substitute.For<ServiceBusClient>();
        AdminClient = Substitute.For<ServiceBusAdministrationClient>();
        Receiver = Substitute.For<ServiceBusReceiver>();
        Sender = Substitute.For<ServiceBusSender>();

        // Setup factory to return clients
        Factory.CreateClient(Arg.Any<string>()).Returns(Client);
        Factory.CreateAdministrationClient(Arg.Any<string>()).Returns(AdminClient);

        // Setup client dispose
        Client.DisposeAsync().Returns(ValueTask.CompletedTask);
    }

    /// <summary>
    /// Configures the receiver to return the specified messages when peeking.
    /// </summary>
    public MockServiceBusClientFactory WithMessagesToReturn(IEnumerable<ServiceBusReceivedMessage> messages)
    {
        _messagesToReturn.Clear();
        _messagesToReturn.AddRange(messages);
        ConfigureReceiver();
        return this;
    }

    /// <summary>
    /// Configures the receiver to return the specified messages when peeking.
    /// </summary>
    public MockServiceBusClientFactory WithMessagesToReturn(params ServiceBusReceivedMessage[] messages) => WithMessagesToReturn(messages.AsEnumerable());

    /// <summary>
    /// Configures the receiver to return empty results (no messages).
    /// </summary>
    public MockServiceBusClientFactory WithNoMessages()
    {
        _messagesToReturn.Clear();
        ConfigureReceiver();
        return this;
    }

    private void ConfigureReceiver()
    {
        if (_receiverConfigured)
        {
            return;
        }

        _receiverConfigured = true;

        // Setup receiver creation for all SubQueue scenarios
        Client.CreateReceiver(Arg.Any<string>(), Arg.Any<ServiceBusReceiverOptions>())
              .Returns(Receiver);
        Client.CreateReceiver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ServiceBusReceiverOptions>())
              .Returns(Receiver);

        // Setup receiver dispose
        Receiver.DisposeAsync().Returns(ValueTask.CompletedTask);

        // Reset counters for fresh configuration
        _peekCallCount = 0;
        _receiveCallCount = 0;

        // Setup PeekMessagesAsync - returns all messages on first call, empty on subsequent
        Receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var maxMessages = callInfo.ArgAt<int>(0);
                    _peekCallCount++;
                    if (_peekCallCount == 1)
                    {
                        var batch = _messagesToReturn.Take(maxMessages).ToList();
                        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch);
                    }

                    return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
                });

        // Setup ReceiveMessagesAsync - returns all messages on first call, empty on subsequent
        Receiver.ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var maxMessages = callInfo.ArgAt<int>(0);
                    _receiveCallCount++;
                    if (_receiveCallCount == 1)
                    {
                        var batch = _messagesToReturn.Take(maxMessages).ToList();
                        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch);
                    }

                    return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
                });

        // Setup message completion and abandonment
        Receiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        Receiver.AbandonMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Configures the sender for testing resubmit scenarios.
    /// </summary>
    public MockServiceBusClientFactory WithSender()
    {
        if (_senderConfigured)
        {
            return this;
        }

        _senderConfigured = true;

        Client.CreateSender(Arg.Any<string>()).Returns(Sender);

        Sender.DisposeAsync().Returns(ValueTask.CompletedTask);

        Sender.SendMessagesAsync(Arg.Any<IEnumerable<ServiceBusMessage>>(), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        Sender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        return this;
    }

    /// <summary>
    /// Gets the IServiceBusClientFactory mock object.
    /// </summary>
    public IServiceBusClientFactory Object => Factory;

    /// <summary>
    /// Creates a new configured mock factory.
    /// </summary>
    public static MockServiceBusClientFactory Create() => new();
}
