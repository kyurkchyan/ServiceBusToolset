using ServiceBusToolset.Application.Common.ServiceBus.Helpers;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Helpers;

public class WildcardFilterHelperShould
{
    [Fact]
    public void ReturnAlwaysTruePredicate_WhenFilterIsNull()
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate(null);
        predicate("anything").ShouldBeTrue();
        predicate("").ShouldBeTrue();
    }

    [Fact]
    public void ReturnAlwaysTruePredicate_WhenFilterIsEmpty()
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate("");
        predicate("anything").ShouldBeTrue();
    }

    [Fact]
    public void ReturnAlwaysTruePredicate_WhenFilterIsWhitespace()
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate("   ");
        predicate("anything").ShouldBeTrue();
    }

    [Theory]
    [InlineData("test", "test-queue", true)]
    [InlineData("test", "my-test-service", true)]
    [InlineData("test", "testing", true)]
    [InlineData("test", "queue", false)]
    [InlineData("test", "TEST-QUEUE", true)]
    [InlineData("TEST", "test-queue", true)]
    public void MatchContains_WhenFilterIsPlainText(string filter, string name, bool expected)
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate(filter);
        predicate(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("test*", "test-queue", true)]
    [InlineData("test*", "testing", true)]
    [InlineData("test*", "test", true)]
    [InlineData("test*", "my-test", false)]
    [InlineData("*queue", "test-queue", true)]
    [InlineData("*queue", "queue", true)]
    [InlineData("*queue", "queue-name", false)]
    [InlineData("*test*", "my-test-queue", true)]
    [InlineData("*test*", "test", true)]
    [InlineData("*test*", "queue", false)]
    public void MatchPattern_WhenFilterHasAsteriskWildcard(string filter, string name, bool expected)
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate(filter);
        predicate(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("test?", "test1", true)]
    [InlineData("test?", "testA", true)]
    [InlineData("test?", "test", false)]
    [InlineData("test?", "test12", false)]
    [InlineData("t?st", "test", true)]
    [InlineData("t?st", "tast", true)]
    [InlineData("t?st", "toast", false)]
    public void MatchSingleCharacter_WhenFilterHasQuestionMarkWildcard(string filter, string name, bool expected)
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate(filter);
        predicate(name).ShouldBe(expected);
    }

    [Fact]
    public void BeCaseInsensitive_WhenMatchingWildcardPattern()
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate("TEST*");
        predicate("test-queue").ShouldBeTrue();
        predicate("TEST-QUEUE").ShouldBeTrue();
        predicate("Test-Queue").ShouldBeTrue();
    }

    [Theory]
    [InlineData("*", "any-queue-name", true)]
    [InlineData("*", "", true)]
    [InlineData("*", "x", true)]
    public void MatchEverything_WhenFilterIsSingleAsterisk(string filter, string name, bool expected)
    {
        var predicate = WildcardFilterHelper.CreateFilterPredicate(filter);
        predicate(name).ShouldBe(expected);
    }
}
