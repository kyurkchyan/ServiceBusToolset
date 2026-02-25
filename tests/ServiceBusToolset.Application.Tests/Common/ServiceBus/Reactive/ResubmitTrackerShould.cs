using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.Common.ServiceBus.Reactive;

public class ResubmitTrackerShould
{
    [Fact]
    public void ReturnFalse_WhenMessageNotTracked()
    {
        // Arrange
        var tracker = new ResubmitTracker();

        // Act & Assert
        tracker.WasResubmitted("unknown-id").ShouldBeFalse();
    }

    [Fact]
    public void ReturnTrue_WhenMessageWasMarkedResubmitted()
    {
        // Arrange
        var tracker = new ResubmitTracker();

        // Act
        tracker.MarkResubmitted("msg-1");

        // Assert
        tracker.WasResubmitted("msg-1").ShouldBeTrue();
    }

    [Fact]
    public void TrackMultipleIds_WhenBatchMarked()
    {
        // Arrange
        var tracker = new ResubmitTracker();

        // Act
        tracker.MarkResubmitted(["msg-1", "msg-2", "msg-3"]);

        // Assert
        tracker.WasResubmitted("msg-1").ShouldBeTrue();
        tracker.WasResubmitted("msg-2").ShouldBeTrue();
        tracker.WasResubmitted("msg-3").ShouldBeTrue();
        tracker.WasResubmitted("msg-4").ShouldBeFalse();
    }

    [Fact]
    public async Task BeThreadSafe_WhenAccessedConcurrently()
    {
        // Arrange
        var tracker = new ResubmitTracker();
        var tasks = new List<Task>();

        // Act
        for (var i = 0; i < 100; i++)
        {
            var id = $"msg-{i}";
            tasks.Add(Task.Run(() => tracker.MarkResubmitted(id), TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        // Assert
        for (var i = 0; i < 100; i++)
        {
            tracker.WasResubmitted($"msg-{i}").ShouldBeTrue();
        }
    }
}
