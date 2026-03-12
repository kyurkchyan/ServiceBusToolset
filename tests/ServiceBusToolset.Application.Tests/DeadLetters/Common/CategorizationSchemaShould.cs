using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategorizationSchemaShould
{
    [Fact]
    public void HaveDefaultWithSubjectAndDeadLetterReason()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Default;

        // Assert
        schema.DimensionCount.ShouldBe(2);
        schema.Properties[0].Source.ShouldBe(PropertySource.System);
        schema.Properties[0].PropertyPath.ShouldBe("Subject");
        schema.Properties[1].Source.ShouldBe(PropertySource.System);
        schema.Properties[1].PropertyPath.ShouldBe("DeadLetterReason");
    }

    [Fact]
    public void ReturnDefault_WhenParsingNull()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(null);

        // Assert
        schema.ShouldBeSameAs(CategorizationSchema.Default);
    }

    [Fact]
    public void ReturnDefault_WhenParsingEmptyList()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse([]);

        // Assert
        schema.ShouldBeSameAs(CategorizationSchema.Default);
    }

    [Fact]
    public void ParseSingleSystemProperty()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["#DeadLetterReason"]);

        // Assert
        schema.DimensionCount.ShouldBe(1);
        schema.Properties[0].Source.ShouldBe(PropertySource.System);
        schema.Properties[0].PropertyPath.ShouldBe("DeadLetterReason");
    }

    [Fact]
    public void ParseMixedProperties()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["#DeadLetterReason", "$ErrorCode"]);

        // Assert
        schema.DimensionCount.ShouldBe(2);
        schema.Properties[0].Source.ShouldBe(PropertySource.System);
        schema.Properties[0].PropertyPath.ShouldBe("DeadLetterReason");
        schema.Properties[1].Source.ShouldBe(PropertySource.Body);
        schema.Properties[1].PropertyPath.ShouldBe("ErrorCode");
    }

    [Fact]
    public void ParseThreeDimensions()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["$tier", "#Subject", "#DeadLetterReason"]);

        // Assert
        schema.DimensionCount.ShouldBe(3);
        schema.Properties[0].PropertyPath.ShouldBe("tier");
        schema.Properties[1].PropertyPath.ShouldBe("Subject");
        schema.Properties[2].PropertyPath.ShouldBe("DeadLetterReason");
    }

    [Fact]
    public void SetUsesBodyProperties_WhenBodyPropertyPresent()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["#Subject", "$ErrorCode"]);

        // Assert
        schema.UsesBodyProperties.ShouldBeTrue();
    }

    [Fact]
    public void NotSetUsesBodyProperties_WhenOnlySystemProperties()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["#Subject", "#DeadLetterReason"]);

        // Assert
        schema.UsesBodyProperties.ShouldBeFalse();
    }

    [Fact]
    public void SetUsesBodyProperties_WhenOnlyBodyProperties()
    {
        // Arrange & Act
        var schema = CategorizationSchema.Parse(["$tier", "$error.code"]);

        // Assert
        schema.UsesBodyProperties.ShouldBeTrue();
    }

    [Fact]
    public void ThrowArgumentException_WhenPropertiesListIsEmpty()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new CategorizationSchema([]));
    }

    /// <summary>
    /// Verifies that the default CategorizationSchema has UsesBodyProperties set to false.
    /// </summary>
    [Fact]
    public void DefaultUsesBodyPropertiesShouldBeFalse()
    {
        // Assert
        CategorizationSchema.Default.UsesBodyProperties.ShouldBeFalse();
    }
}
