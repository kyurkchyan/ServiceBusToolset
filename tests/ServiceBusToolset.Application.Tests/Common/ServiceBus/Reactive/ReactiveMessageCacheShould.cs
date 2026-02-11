using DynamicData;
using ServiceBusToolset.Application.Common.ServiceBus.Reactive;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.Common.ServiceBus.Reactive;

public class ReactiveMessageCacheShould
{
    private sealed record TestMessage(string Id, string Category);

    [Fact]
    public void ReturnEmptySnapshot_WhenNoItemsAdded()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);

        // Act
        var snapshot = cache.Snapshot();

        // Assert
        snapshot.ShouldBeEmpty();
    }

    [Fact]
    public void ContainItems_WhenAddOrUpdateCalled()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);
        var items = new[]
        {
            new TestMessage("1", "A"),
            new TestMessage("2", "B")
        };

        // Act
        cache.AddOrUpdate(items);

        // Assert
        cache.Count.ShouldBe(2);
        var snapshot = cache.Snapshot();
        snapshot.Count.ShouldBe(2);
    }

    [Fact]
    public void DeduplicateByKey_WhenSameKeyAddedTwice()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);

        // Act
        cache.AddOrUpdate([new TestMessage("1", "A")]);
        cache.AddOrUpdate([new TestMessage("1", "B")]);

        // Assert
        cache.Count.ShouldBe(1);
        var item = cache.Lookup("1");
        item.ShouldNotBeNull();
        item!.Category.ShouldBe("B");
    }

    [Fact]
    public async Task EmitChangeSet_WhenItemsAdded()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);
        var changeSets = new List<IChangeSet<TestMessage, string>>();

        using var subscription = cache.Connect().Subscribe(changeSets.Add);

        // Act
        cache.AddOrUpdate([new TestMessage("1", "A")]);

        // Allow subscription to process
        await Task.Delay(50);

        // Assert
        changeSets.ShouldNotBeEmpty();
        changeSets.SelectMany(cs => cs).ShouldContain(c => c.Reason == ChangeReason.Add);
    }

    [Fact]
    public void ReportCorrectCount_WhenItemsAdded()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);

        // Act
        cache.AddOrUpdate([new TestMessage("1", "A"), new TestMessage("2", "B"), new TestMessage("3", "C")]);

        // Assert
        cache.Count.ShouldBe(3);
    }

    [Fact]
    public void ReportIsComplete_WhenMarkCompleteCalled()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);

        // Act & Assert
        cache.IsComplete.ShouldBeFalse();
        cache.MarkComplete();
        cache.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void ReturnPointInTimeSnapshot_WhenSnapshotCalled()
    {
        // Arrange
        using var cache = new ReactiveMessageCache<TestMessage, string>(m => m.Id);
        cache.AddOrUpdate([new TestMessage("1", "A"), new TestMessage("2", "B")]);

        // Act
        var snapshot = cache.Snapshot();

        // Add more items after snapshot
        cache.AddOrUpdate([new TestMessage("3", "C")]);

        // Assert
        snapshot.Count.ShouldBe(2);
        cache.Count.ShouldBe(3);
    }
}
