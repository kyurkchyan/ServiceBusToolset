using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategorySelectionParserShould
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnNull_WhenInputIsNullOrWhitespace(string? input)
    {
        var result = CategorySelectionParser.Parse(input, 10);
        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("q")]
    [InlineData("Q")]
    [InlineData("quit")]
    [InlineData("QUIT")]
    public void ReturnNull_WhenInputIsQuitCommand(string input)
    {
        var result = CategorySelectionParser.Parse(input, 10);
        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("a")]
    [InlineData("A")]
    public void ReturnAllIndices_WhenInputIsAllCommand(string input)
    {
        var result = CategorySelectionParser.Parse(input, 5);

        result.ShouldNotBeNull();
        result.ShouldBe(new[]
        {
            0,
            1,
            2,
            3,
            4
        });
    }

    [Fact]
    public void ReturnZeroBasedIndex_WhenInputIsSingleNumber()
    {
        var result = CategorySelectionParser.Parse("1", 5);

        result.ShouldNotBeNull();
        result.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void ReturnZeroBasedIndices_WhenInputIsCommaSeparatedNumbers()
    {
        var result = CategorySelectionParser.Parse("1,3,5", 10);

        result.ShouldNotBeNull();
        result.ShouldBe(new[]
        {
            0,
            2,
            4
        });
    }

    [Fact]
    public void ReturnAllIndicesInRange_WhenInputIsRange()
    {
        var result = CategorySelectionParser.Parse("1-3", 10);

        result.ShouldNotBeNull();
        result.ShouldBe(new[]
        {
            0,
            1,
            2
        });
    }

    [Fact]
    public void ReturnAllIndices_WhenInputIsMixedRangeAndNumbers()
    {
        var result = CategorySelectionParser.Parse("1,3-5,7", 10);

        result.ShouldNotBeNull();
        result.ShouldBe(new[]
        {
            0,
            2,
            3,
            4,
            6
        });
    }

    [Fact]
    public void IgnoreInvalidIndices_WhenNumbersAreOutOfRange()
    {
        var result = CategorySelectionParser.Parse("1,6,10", 5);

        result.ShouldNotBeNull();
        result.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void ReturnUniqueIndices_WhenInputHasDuplicates()
    {
        var result = CategorySelectionParser.Parse("1,1,2,2,3", 5);

        result.ShouldNotBeNull();
        result.ShouldBe(new[]
        {
            0,
            1,
            2
        });
    }

    [Fact]
    public void ReturnEmptyList_WhenInputIsInvalid()
    {
        var result = CategorySelectionParser.Parse("abc,xyz", 5);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }
}
