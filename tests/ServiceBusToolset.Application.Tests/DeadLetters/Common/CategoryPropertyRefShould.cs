using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategoryPropertyRefShould
{
    [Fact]
    public void ParseSystemProperty_WhenPrefixIsHash()
    {
        // Arrange & Act
        var result = CategoryPropertyRef.Parse("#Subject");

        // Assert
        result.Source.ShouldBe(PropertySource.System);
        result.PropertyPath.ShouldBe("Subject");
    }

    [Fact]
    public void ParseBodyProperty_WhenPrefixIsDollar()
    {
        // Arrange & Act
        var result = CategoryPropertyRef.Parse("$ErrorCode");

        // Assert
        result.Source.ShouldBe(PropertySource.Body);
        result.PropertyPath.ShouldBe("ErrorCode");
    }

    [Fact]
    public void ParseNestedBodyProperty_WhenPathContainsDots()
    {
        // Arrange & Act
        var result = CategoryPropertyRef.Parse("$error.severity");

        // Assert
        result.Source.ShouldBe(PropertySource.Body);
        result.PropertyPath.ShouldBe("error.severity");
    }

    [Fact]
    public void ParseDeeplyNestedPath_WhenMultipleDotsPresent()
    {
        // Arrange & Act
        var result = CategoryPropertyRef.Parse("$context.deployment.region");

        // Assert
        result.Source.ShouldBe(PropertySource.Body);
        result.PropertyPath.ShouldBe("context.deployment.region");
    }

    [Fact]
    public void TrimWhitespace_WhenReferenceHasSpaces()
    {
        // Arrange & Act
        var result = CategoryPropertyRef.Parse("  #Subject  ");

        // Assert
        result.Source.ShouldBe(PropertySource.System);
        result.PropertyPath.ShouldBe("Subject");
    }

    [Fact]
    public void ReturnSystemDisplayName_WhenSourceIsSystem()
    {
        // Arrange
        var prop = new CategoryPropertyRef(PropertySource.System, "DeadLetterReason");

        // Act & Assert
        prop.DisplayName.ShouldBe("#DeadLetterReason");
    }

    [Fact]
    public void ReturnBodyDisplayName_WhenSourceIsBody()
    {
        // Arrange
        var prop = new CategoryPropertyRef(PropertySource.Body, "error.code");

        // Act & Assert
        prop.DisplayName.ShouldBe("$error.code");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void ThrowArgumentException_WhenReferenceIsEmpty(string? reference)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => CategoryPropertyRef.Parse(reference!));
    }

    [Fact]
    public void ThrowArgumentException_WhenReferenceTooShort()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => CategoryPropertyRef.Parse("#"));
    }

    [Fact]
    public void ThrowArgumentException_WhenPrefixIsInvalid()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => CategoryPropertyRef.Parse("@Subject"));
    }

    [Fact]
    public void ThrowArgumentException_WhenNoPrefixProvided()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => CategoryPropertyRef.Parse("Subject"));
    }

    [Fact]
    public void SupportRecordEquality_WhenValuesMatch()
    {
        // Arrange
        var ref1 = CategoryPropertyRef.Parse("#Subject");
        var ref2 = CategoryPropertyRef.Parse("#Subject");
        var ref3 = CategoryPropertyRef.Parse("$Subject");

        // Assert
        ref1.ShouldBe(ref2);
        ref1.ShouldNotBe(ref3);
    }
}
