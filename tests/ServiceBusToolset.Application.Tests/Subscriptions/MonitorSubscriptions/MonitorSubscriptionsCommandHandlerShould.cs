using System.Reactive.Linq;
using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NSubstitute;
using ServiceBusToolset.Application.Subscriptions.MonitorSubscriptions;
using ServiceBusToolset.Application.Tests.Common.Mocks;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.Subscriptions.MonitorSubscriptions;

public class MonitorSubscriptionsCommandHandlerShould
{
    private readonly MockServiceBusClientFactory _mockFactory;
    private readonly MonitorSubscriptionsCommandHandler _handler;

    public MonitorSubscriptionsCommandHandlerShould()
    {
        _mockFactory = MockServiceBusClientFactory.Create();
        _handler = new MonitorSubscriptionsCommandHandler(_mockFactory.Object);
    }

    [Fact]
    public async Task ReturnObservable_WhenHandlingCommand()
    {
        // Arrange
        SetupTopicsAndSubscriptions();

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      null,
                                                      TimeSpan.FromSeconds(1),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _ = result.Value.SubscriptionStatistics.ShouldNotBeNull();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task EmitStatistics_WhenSubscriptionsExist()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("orders-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "sub-1",
                                                                                10,
                                                                                5),
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "sub-2",
                                                                                20,
                                                                                3)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      null,
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(2);
        firstEmission.ShouldContain(s => s.SubscriptionName == "sub-1");
        firstEmission.ShouldContain(s => s.SubscriptionName == "sub-2");
    }

    [Fact]
    public async Task ApplyTopicFilter_WhenFilterProvided()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("orders-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "sub-1",
                                                                                10,
                                                                                5)
                                        }),
                                    ("payments-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("payments-topic",
                                                                                "sub-1",
                                                                                20,
                                                                                3)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      "orders*",
                                                      null,
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(1);
        firstEmission.Single().TopicName.ShouldBe("orders-topic");
    }

    [Fact]
    public async Task ApplySubscriptionFilter_WhenFilterProvided()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("orders-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "premium-sub",
                                                                                10,
                                                                                5),
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "standard-sub",
                                                                                20,
                                                                                3)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      "premium*",
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(1);
        firstEmission.Single().SubscriptionName.ShouldBe("premium-sub");
    }

    [Fact]
    public async Task ApplyDualFilters_WhenBothFiltersProvided()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("orders-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "premium-sub",
                                                                                10,
                                                                                5),
                                            CreateSubscriptionRuntimeProperties("orders-topic",
                                                                                "standard-sub",
                                                                                20,
                                                                                3)
                                        }),
                                    ("payments-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("payments-topic",
                                                                                "premium-sub",
                                                                                15,
                                                                                2)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      "orders*",
                                                      "premium*",
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.Count.ShouldBe(1);
        firstEmission.Single().TopicName.ShouldBe("orders-topic");
        firstEmission.Single().SubscriptionName.ShouldBe("premium-sub");
    }

    [Fact]
    public async Task SortByTopicThenSubscription()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("zebra-topic", new[] { CreateSubscriptionRuntimeProperties("zebra-topic", "sub-1", 1) }),
                                    ("alpha-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("alpha-topic", "beta-sub", 2),
                                            CreateSubscriptionRuntimeProperties("alpha-topic", "alpha-sub", 3)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      null,
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission[0].TopicName.ShouldBe("alpha-topic");
        firstEmission[0].SubscriptionName.ShouldBe("alpha-sub");
        firstEmission[1].TopicName.ShouldBe("alpha-topic");
        firstEmission[1].SubscriptionName.ShouldBe("beta-sub");
        firstEmission[2].TopicName.ShouldBe("zebra-topic");
    }

    [Fact]
    public async Task ReturnEmptyList_WhenNoTopicsExist()
    {
        // Arrange
        SetupTopicsAndSubscriptions();

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      null,
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        firstEmission.ShouldBeEmpty();
    }

    [Fact]
    public async Task IncludeMessageCounts_InStatistics()
    {
        // Arrange
        SetupTopicsAndSubscriptions(("test-topic", new[]
                                        {
                                            CreateSubscriptionRuntimeProperties("test-topic",
                                                                                "test-sub",
                                                                                100,
                                                                                50)
                                        }));

        using var cts = new CancellationTokenSource();

        var command = new MonitorSubscriptionsCommand("test.servicebus.windows.net",
                                                      null,
                                                      null,
                                                      TimeSpan.FromMilliseconds(100),
                                                      cts.Token);

        // Act
        var result = await _handler.Handle(command, TestContext.Current.CancellationToken);
        var firstEmission = await result.Value.SubscriptionStatistics.FirstAsync();
        await cts.CancelAsync();

        // Assert
        var subStats = firstEmission.Single();
        subStats.ActiveMessageCount.ShouldBe(100);
        subStats.DeadLetterMessageCount.ShouldBe(50);
    }

    private void SetupTopicsAndSubscriptions(params (string TopicName, SubscriptionRuntimeProperties[] Subscriptions)[] topics)
    {
        var topicProperties = topics.Select(t => CreateTopicProperties(t.TopicName)).ToArray();
        var topicsPageable = CreateAsyncPageable(topicProperties);
        _mockFactory.AdminClient.GetTopicsAsync(Arg.Any<CancellationToken>())
                    .ReturnsForAnyArgs(topicsPageable);

        foreach (var (topicName, subscriptions) in topics)
        {
            var subscriptionsPageable = CreateAsyncPageable(subscriptions);
            _mockFactory.AdminClient.GetSubscriptionsRuntimePropertiesAsync(topicName, Arg.Any<CancellationToken>())
                        .ReturnsForAnyArgs(subscriptionsPageable);
        }
    }

    private static TopicProperties CreateTopicProperties(string name) =>
        ServiceBusModelFactory.TopicProperties(name,
                                               defaultMessageTimeToLive:TimeSpan.FromDays(14),
                                               autoDeleteOnIdle:TimeSpan.FromDays(7),
                                               duplicateDetectionHistoryTimeWindow:TimeSpan.FromMinutes(10));

    private static SubscriptionRuntimeProperties CreateSubscriptionRuntimeProperties(
        string topicName,
        string subscriptionName,
        long activeMessageCount = 0,
        long deadLetterCount = 0) =>
        ServiceBusModelFactory.SubscriptionRuntimeProperties(topicName,
                                                             subscriptionName,
                                                             activeMessageCount,
                                                             deadLetterCount);

    private static AsyncPageable<T> CreateAsyncPageable<T>(T[] items) where T : notnull
    {
        var page = Page<T>.FromValues(items, null, Substitute.For<Response>());
        return AsyncPageable<T>.FromPages([page]);
    }
}
