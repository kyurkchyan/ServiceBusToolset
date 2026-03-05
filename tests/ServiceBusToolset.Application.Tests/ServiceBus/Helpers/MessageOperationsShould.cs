using Azure.Messaging.ServiceBus;
using NSubstitute;
using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Helpers;

public class MessageOperationsShould
{
    private readonly ServiceBusReceiver _receiver;
    private int _peekCallCount;

    public MessageOperationsShould()
    {
        _receiver = Substitute.For<ServiceBusReceiver>();
    }

    [Fact]
    public async Task PeekAllMessages_WhenQueueHasMessages()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-1").Build(),
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-2").Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        var result = await MessageOperations.PeekAllAsync(_receiver, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(2);
        result[0].MessageId.ShouldBe("msg-1");
        result[1].MessageId.ShouldBe("msg-2");
    }

    [Fact]
    public async Task PeekAllMessages_WhenQueueIsEmpty()
    {
        // Arrange
        SetupPeekToReturnEmpty();

        // Act
        var result = await MessageOperations.PeekAllAsync(_receiver, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task PeekAllMessages_WhenMultipleBatchesNeeded()
    {
        // Arrange
        var batch1 = Enumerable.Range(1, 100)
                               .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                            .WithMessageId($"msg-{i}")
                                                                            .Build())
                               .ToArray();

        var batch2 = Enumerable.Range(101, 50)
                               .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                            .WithMessageId($"msg-{i}")
                                                                            .Build())
                               .ToArray();

        SetupMultipleBatches(batch1, batch2);

        // Act
        var result = await MessageOperations.PeekAllAsync(_receiver, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(150);
        result.First().MessageId.ShouldBe("msg-1");
        result.Last().MessageId.ShouldBe("msg-150");
    }

    [Fact]
    public async Task StopPeeking_WhenEmptyBatchThresholdReached()
    {
        // Arrange
        var messages = new[] { ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-1").Build() };

        // Return messages once, then empty batches
        var callCount = 0;
        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     callCount++;
                     if (callCount == 1)
                     {
                         return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(messages);
                     }

                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
                 });

        // Act
        var result = await MessageOperations.PeekAllAsync(_receiver, emptyBatchThreshold:3, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(1);
        callCount.ShouldBe(4); // 1 batch with data + 3 empty batches
    }

    [Fact]
    public async Task ReportProgress_WhenPeekingMessages()
    {
        // Arrange
        var progressReports = new List<int>();
        var progress = new Progress<int>(count => progressReports.Add(count));

        var batch1 = new[]
        {
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-1").Build(),
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-2").Build()
        };

        var batch2 = new[] { ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-3").Build() };

        SetupMultipleBatches(batch1, batch2);

        // Act
        await MessageOperations.PeekAllAsync(_receiver, progress:progress, cancellationToken:TestContext.Current.CancellationToken);

        // Assert - wait for progress to be reported
        await Task.Delay(50, TestContext.Current.CancellationToken);
        progressReports.ShouldContain(2);
        progressReports.ShouldContain(3);
    }

    [Fact]
    public async Task StopPeeking_WhenCancellationRequested()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var callCount = 0;

        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     callCount++;
                     if (callCount == 2)
                     {
                         cts.Cancel();
                     }

                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([ServiceBusReceivedMessageBuilder.Create().WithMessageId($"msg-{callCount}").Build()]);
                 });

        // Act
        var result = await MessageOperations.PeekAllAsync(_receiver, cancellationToken:cts.Token);

        // Assert
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PeekMessages_WhenMaxLimitProvided()
    {
        // Arrange
        var messages = Enumerable.Range(1, 100)
                                 .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                              .WithMessageId($"msg-{i}")
                                                                              .Build())
                                 .ToArray();

        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     var requestedCount = callInfo.ArgAt<int>(0);
                     var batch = messages.Take(requestedCount).ToList();
                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch);
                 });

        // Act
        var result = await MessageOperations.PeekAsync(_receiver, 50, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(50);
    }

    [Fact]
    public async Task PeekMessages_WhenFewerMessagesThanMax()
    {
        // Arrange
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-1").Build(),
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-2").Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        var result = await MessageOperations.PeekAsync(_receiver, 100, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PeekMessages_WhenQueueIsEmpty()
    {
        // Arrange
        SetupPeekToReturnEmpty();

        // Act
        var result = await MessageOperations.PeekAsync(_receiver, 50, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdjustBatchSize_WhenApproachingMaxLimit()
    {
        // Arrange
        var capturedBatchSizes = new List<int>();

        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     var requestedCount = callInfo.ArgAt<int>(0);
                     capturedBatchSizes.Add(requestedCount);

                     // Return exactly what was requested to simulate filling up
                     var messages = Enumerable.Range(1, requestedCount)
                                              .Select(i => ServiceBusReceivedMessageBuilder.Create()
                                                                                           .WithMessageId($"msg-{i}")
                                                                                           .Build())
                                              .ToList();
                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(messages);
                 });

        // Act
        await MessageOperations.PeekAsync(_receiver,
                                          150,
                                          cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        capturedBatchSizes.ShouldContain(100); // First batch
        capturedBatchSizes.ShouldContain(50); // Second batch (150-100=50 remaining)
    }

    [Fact]
    public async Task CountWithTimeFilter_WhenMessagesMatchFilter()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("old-1")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-2))
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("old-2")
                                            .WithEnqueuedTime(cutoffTime.AddHours(-1))
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("new-1")
                                            .WithEnqueuedTime(cutoffTime.AddHours(1))
                                            .Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        var result = await MessageOperations.CountWithTimeFilterAsync(_receiver, cutoffTime, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.ShouldBe(3);
        result.FilteredCount.ShouldBe(2); // Only old messages match (before cutoff)
    }

    [Fact]
    public async Task CountWithTimeFilter_WhenNoMessagesMatch()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(-5);

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithMessageId("new-1")
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow)
                                            .Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        var result = await MessageOperations.CountWithTimeFilterAsync(_receiver, cutoffTime, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.ShouldBe(1);
        result.FilteredCount.ShouldBe(0);
    }

    [Fact]
    public async Task CountWithTimeFilter_WhenQueueIsEmpty()
    {
        // Arrange
        SetupPeekToReturnEmpty();

        // Act
        var result = await MessageOperations.CountWithTimeFilterAsync(_receiver, DateTimeOffset.UtcNow, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.ShouldBe(0);
        result.FilteredCount.ShouldBe(0);
    }

    [Fact]
    public async Task CountWithTimeFilter_ReportProgress()
    {
        // Arrange
        var progressReports = new List<int>();
        var progress = new Progress<int>(count => progressReports.Add(count));

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-1))
                                            .Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        await MessageOperations.CountWithTimeFilterAsync(_receiver,
                                                         DateTimeOffset.UtcNow,
                                                         progress:progress,
                                                         cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        await Task.Delay(50, TestContext.Current.CancellationToken);
        progressReports.ShouldContain(1);
    }

    [Fact]
    public async Task CountWithTimeFilter_WhenAllMessagesMatchFilter()
    {
        // Arrange
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(1);

        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-2))
                                            .Build(),
            ServiceBusReceivedMessageBuilder.Create()
                                            .WithEnqueuedTime(DateTimeOffset.UtcNow.AddHours(-1))
                                            .Build()
        };

        SetupPeekToReturnThenEmpty(messages);

        // Act
        var result = await MessageOperations.CountWithTimeFilterAsync(_receiver, cutoffTime, cancellationToken:TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.ShouldBe(2);
        result.FilteredCount.ShouldBe(2);
    }

    private void SetupPeekToReturnThenEmpty(IReadOnlyList<ServiceBusReceivedMessage> messages)
    {
        _peekCallCount = 0;
        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     _peekCallCount++;
                     if (_peekCallCount == 1)
                     {
                         return Task.FromResult(messages);
                     }

                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
                 });
    }

    private void SetupPeekToReturnEmpty()
    {
        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]));
    }

    private void SetupMultipleBatches(params IReadOnlyList<ServiceBusReceivedMessage>[] batches)
    {
        var batchIndex = 0;
        _receiver.PeekMessagesAsync(Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
                 .Returns(callInfo =>
                 {
                     if (batchIndex < batches.Length)
                     {
                         var batch = batches[batchIndex];
                         batchIndex++;
                         return Task.FromResult(batch);
                     }

                     return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>([]);
                 });
    }
}
