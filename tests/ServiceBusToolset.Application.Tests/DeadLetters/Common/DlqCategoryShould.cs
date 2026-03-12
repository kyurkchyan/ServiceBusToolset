using System.Collections.Immutable;
using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqCategoryShould
{
    [Fact]
    public void CreateWithBackwardCompatConstructor()
    {
        // Arrange & Act
        var category = new DlqCategory("OrderProcessor", "MaxDeliveryCountExceeded", 42);

        // Assert
        category.Label.ShouldBe("OrderProcessor");
        category.DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        category.Count.ShouldBe(42);
        category.Values.Length.ShouldBe(2);
    }

    [Fact]
    public void CreateWithImmutableArrayConstructor()
    {
        // Arrange & Act
        var category = new DlqCategory(["val1", "val2", "val3"], 10);

        // Assert
        category.Values.Length.ShouldBe(3);
        category.Values[0].ShouldBe("val1");
        category.Values[1].ShouldBe("val2");
        category.Values[2].ShouldBe("val3");
        category.Count.ShouldBe(10);
    }

    /// <summary>
    /// Verifies that when a DlqCategory is created with an empty Values collection, both Label and DeadLetterReason are "(none)".
    /// </summary>
    [Fact]
    public void ReturnNoneForLabel_WhenValuesEmpty()
    {
        // Arrange & Act
        var category = new DlqCategory(ImmutableArray<string>.Empty, 1);

        // Assert
        category.Label.ShouldBe("(none)");
        category.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void ReturnNoneForReason_WhenSingleValue()
    {
        // Arrange & Act
        var category = new DlqCategory(["OnlyLabel"], 5);

        // Assert
        category.Label.ShouldBe("OnlyLabel");
        category.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void ConvertToKey_WithToKey()
    {
        // Arrange
        var category = new DlqCategory("Label", "Reason", 10);

        // Act
        var key = category.ToKey();

        // Assert
        key.Values.Length.ShouldBe(2);
        key.Label.ShouldBe("Label");
        key.DeadLetterReason.ShouldBe("Reason");
    }

    [Fact]
    public void ConvertToKey_WithNDimensions()
    {
        // Arrange
        var category = new DlqCategory(["a", "b", "c"], 7);

        // Act
        var key = category.ToKey();

        // Assert
        key.Values.Length.ShouldBe(3);
        key.Values[0].ShouldBe("a");
        key.Values[1].ShouldBe("b");
        key.Values[2].ShouldBe("c");
    }

    [Fact]
    public void CreateFromKey_WithFromKey()
    {
        // Arrange
        var key = new DlqCategoryKey("Label", "Reason");

        // Act
        var category = DlqCategory.FromKey(key, 25);

        // Assert
        category.Label.ShouldBe("Label");
        category.DeadLetterReason.ShouldBe("Reason");
        category.Count.ShouldBe(25);
    }

    [Fact]
    public void RoundTrip_ThroughToKeyAndFromKey()
    {
        // Arrange
        var original = new DlqCategory(["x", "y", "z"], 99);

        // Act
        var key = original.ToKey();
        var restored = DlqCategory.FromKey(key, original.Count);

        // Assert
        restored.Values.SequenceEqual(original.Values).ShouldBeTrue();
        restored.Count.ShouldBe(original.Count);
    }
}
