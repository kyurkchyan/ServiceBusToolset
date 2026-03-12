using System.Collections.Immutable;
using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqCategoryKeyShould
{
    [Fact]
    public void CreateKey_WhenSubjectAndReasonProvided()
    {
        var key = DlqCategoryKey.FromMessage("OrderProcessor", "MaxDeliveryCountExceeded");

        key.Label.ShouldBe("OrderProcessor");
        key.DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
    }

    [Fact]
    public void UseNonePlaceholder_WhenSubjectIsNull()
    {
        var key = DlqCategoryKey.FromMessage(null, "SomeReason");

        key.Label.ShouldBe("(none)");
        key.DeadLetterReason.ShouldBe("SomeReason");
    }

    [Fact]
    public void UseNonePlaceholder_WhenReasonIsNull()
    {
        var key = DlqCategoryKey.FromMessage("SomeLabel", null);

        key.Label.ShouldBe("SomeLabel");
        key.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void UseBothNonePlaceholders_WhenBothAreNull()
    {
        var key = DlqCategoryKey.FromMessage(null, null);

        key.Label.ShouldBe("(none)");
        key.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void SupportEquality_WhenUsedAsRecord()
    {
        var key1 = DlqCategoryKey.FromMessage("Label", "Reason");
        var key2 = DlqCategoryKey.FromMessage("Label", "Reason");
        var key3 = DlqCategoryKey.FromMessage("Label", "DifferentReason");

        key1.ShouldBe(key2);
        key1.ShouldNotBe(key3);
    }

    [Fact]
    public void WorkInHashSet_WhenUsedAsKey()
    {
        var keys = new HashSet<DlqCategoryKey>
        {
            DlqCategoryKey.FromMessage("Label1", "Reason1"),
            DlqCategoryKey.FromMessage("Label1", "Reason1"), // Duplicate
            DlqCategoryKey.FromMessage("Label1", "Reason2"),
            DlqCategoryKey.FromMessage(null, null),
            DlqCategoryKey.FromMessage(null, null) // Duplicate
        };

        keys.Count.ShouldBe(3);
    }

    // --- N-dimensional key tests ---

    [Fact]
    public void CreateKey_WithImmutableArrayConstructor()
    {
        // Arrange & Act
        ImmutableArray<string> values = ["val1", "val2", "val3"];
        var key = new DlqCategoryKey(values);

        // Assert
        key.Values.Length.ShouldBe(3);
        key.Values[0].ShouldBe("val1");
        key.Values[1].ShouldBe("val2");
        key.Values[2].ShouldBe("val3");
    }

    [Fact]
    public void CreateKey_WithParamsConstructor()
    {
        // Arrange & Act
        var key = new DlqCategoryKey("a", "b", "c");

        // Assert
        key.Values.Length.ShouldBe(3);
        key.Label.ShouldBe("a");
        key.DeadLetterReason.ShouldBe("b");
    }

    [Fact]
    public void ReturnNoneForLabel_WhenValuesEmpty()
    {
        // Arrange & Act
        ImmutableArray<string> empty = [];
        var key = new DlqCategoryKey(empty);

        // Assert
        key.Label.ShouldBe("(none)");
        key.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void ReturnNoneForReason_WhenSingleValue()
    {
        // Arrange & Act
        var key = new DlqCategoryKey("OnlyLabel");

        // Assert
        key.Label.ShouldBe("OnlyLabel");
        key.DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void SupportEquality_ForNDimensionalKeys()
    {
        // Arrange
        var key1 = new DlqCategoryKey("a", "b", "c");
        var key2 = new DlqCategoryKey("a", "b", "c");
        var key3 = new DlqCategoryKey("a", "b", "d");

        // Assert
        key1.ShouldBe(key2);
        key1.ShouldNotBe(key3);
    }

    [Fact]
    public void NotBeEqual_WhenDifferentDimensionCount()
    {
        // Arrange
        var key2d = new DlqCategoryKey("a", "b");
        var key3d = new DlqCategoryKey("a", "b", "c");

        // Assert
        key2d.ShouldNotBe(key3d);
    }

    [Fact]
    public void WorkInHashSet_ForNDimensionalKeys()
    {
        // Arrange & Act
        var keys = new HashSet<DlqCategoryKey>
        {
            new("a", "b", "c"),
            new("a", "b", "c"), // duplicate
            new("a", "b", "d"),
            new("x", "y")
        };

        // Assert
        keys.Count.ShouldBe(3);
    }

    [Fact]
    public void ProduceConsistentHashCode_ForEqualKeys()
    {
        // Arrange
        var key1 = new DlqCategoryKey("a", "b", "c");
        var key2 = new DlqCategoryKey("a", "b", "c");

        // Assert
        key1.GetHashCode().ShouldBe(key2.GetHashCode());
    }

    [Fact]
    public void CreateKeyFromMessage_WithSchemaAndResolver()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithSubject("OrderProcessor")
                                                      .WithJsonBody(new
                                                      {
                                                          tier = 1,
                                                          error = new { code = "E001" }
                                                      })
                                                      .Build();
        var schema = CategorizationSchema.Parse(["#Subject", "$tier", "$error.code"]);
        var resolver = new CategoryPropertyResolver();

        // Act
        var key = DlqCategoryKey.FromMessage(message, schema, resolver);

        // Assert
        key.Values.Length.ShouldBe(3);
        key.Values[0].ShouldBe("OrderProcessor");
        key.Values[1].ShouldBe("1");
        key.Values[2].ShouldBe("E001");
    }

    [Fact]
    public void CreateKeyWithNone_WhenBodyPropertyMissing()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithSubject("Test")
                                                      .WithJsonBody(new { name = "test" })
                                                      .Build();
        var schema = CategorizationSchema.Parse(["#Subject", "$nonexistent"]);
        var resolver = new CategoryPropertyResolver();

        // Act
        var key = DlqCategoryKey.FromMessage(message, schema, resolver);

        // Assert
        key.Values[0].ShouldBe("Test");
        key.Values[1].ShouldBe("(none)");
    }

    [Fact]
    public void FormatToString_WithPipeSeparator()
    {
        // Arrange
        var key = new DlqCategoryKey("OrderProcessor", "MaxDelivery", "E001");

        // Act & Assert
        key.ToString().ShouldBe("OrderProcessor | MaxDelivery | E001");
    }
}
