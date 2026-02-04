using ServiceBusToolset.Application.DeadLetters.Common;
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
}
