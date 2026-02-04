using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategorySelectionShould
{
    [Fact]
    public void Build_WithSelectedIndices()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("OrderProcessor", "MaxDeliveryCountExceeded", 10),
            new("PaymentHandler", "TimeoutExceeded", 5),
            new("NotificationService", "InvalidMessage", 3)
        };
        var selectedIndices = new[]
        {
            0,
            2
        };

        // Act
        var result = CategorySelection.Build(categories, selectedIndices);

        // Assert
        result.Categories.ShouldBe(categories);
        result.SelectedCategoryCount.ShouldBe(2);
        result.SelectedCount.ShouldBe(13); // 10 + 3
        result.SelectedKeys.Count.ShouldBe(2);
        result.SelectedKeys.ShouldContain(new DlqCategoryKey("OrderProcessor", "MaxDeliveryCountExceeded"));
        result.SelectedKeys.ShouldContain(new DlqCategoryKey("NotificationService", "InvalidMessage"));
    }

    [Fact]
    public void Build_WithEmptySelectedIndices()
    {
        // Arrange
        var categories = new List<DlqCategory> { new("OrderProcessor", "MaxDeliveryCountExceeded", 10) };
        var selectedIndices = Array.Empty<int>();

        // Act
        var result = CategorySelection.Build(categories, selectedIndices);

        // Assert
        result.SelectedCategoryCount.ShouldBe(0);
        result.SelectedCount.ShouldBe(0);
        result.SelectedKeys.ShouldBeEmpty();
    }

    [Fact]
    public void Build_WithAllIndicesSelected()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Cat1", "Reason1", 5),
            new("Cat2", "Reason2", 10),
            new("Cat3", "Reason3", 15)
        };
        var selectedIndices = new[]
        {
            0,
            1,
            2
        };

        // Act
        var result = CategorySelection.Build(categories, selectedIndices);

        // Assert
        result.SelectedCategoryCount.ShouldBe(3);
        result.SelectedCount.ShouldBe(30); // 5 + 10 + 15
        result.SelectedKeys.Count.ShouldBe(3);
    }

    [Fact]
    public void Build_WithSingleIndex()
    {
        // Arrange
        var categories = new List<DlqCategory> { new("SingleCategory", "SingleReason", 42) };
        var selectedIndices = new[] { 0 };

        // Act
        var result = CategorySelection.Build(categories, selectedIndices);

        // Assert
        result.SelectedCategoryCount.ShouldBe(1);
        result.SelectedCount.ShouldBe(42);
        result.SelectedKeys.Single().ShouldBe(new DlqCategoryKey("SingleCategory", "SingleReason"));
    }
}
