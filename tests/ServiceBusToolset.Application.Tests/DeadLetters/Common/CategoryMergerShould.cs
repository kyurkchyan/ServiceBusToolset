using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategoryMergerShould
{
    private const string Reason = "MaxDeliveryCountExceeded";

    // Category A: Single fixed-length parameter

    [Fact]
    public void MergeIntoOneCategory_WhenSingleParamAtEnd()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error for user A", Reason, 1),
            new("Error for user B", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error for user *");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenSingleParamAtStart()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("ABC caused error", Reason, 1),
            new("DEF caused error", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("* caused error");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenSingleParamInMiddle()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error 123 occurred", Reason, 1),
            new("Error 456 occurred", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error * occurred");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenGuidParams()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Could not create user with ID 3cefe1dd-91a0-490d-adfe-dc569472f6e9", Reason, 1),
            new("Could not create user with ID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Reason, 1),
            new("Could not create user with ID 11111111-2222-3333-4444-555555555555", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Could not create user with ID *");
        result.MergedCategories[0].DeadLetterReason.ShouldBe(Reason);
        result.MergedCategories[0].Count.ShouldBe(3);
    }

    // Category B: Variable-length parameters

    [Fact]
    public void MergeIntoOneCategory_WhenVariableLengthParamInMiddle()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("User 'John Smith' is not valid", Reason, 1),
            new("User 'Bob' is not valid", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("User * is not valid");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenVariableLengthParamAtEnd()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error occurred processing Bob", Reason, 1),
            new("Error occurred processing Alice Jane Smith", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error occurred processing *");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenVariableLengthParamAtStart()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("John Smith caused the error", Reason, 1),
            new("Bob caused the error", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("* caused the error");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenMixedFixedAndVariableParams()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error 1 for user 'John Smith' in region us-east", Reason, 1),
            new("Error 2 for user 'Bob' in region eu-west", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error * for user * in region *");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    // Category C: Multiple parameters

    [Fact]
    public void MergeIntoOneCategory_WhenTwoParameters()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error 123 for user ABC", Reason, 1),
            new("Error 456 for user DEF", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error * for user *");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenThreeParameters()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error 1 for user A in region X", Reason, 1),
            new("Error 2 for user B in region Y", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error * for user * in region *");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void MergeIntoOneCategory_WhenAdjacentParameters()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("at 10 200 ms", Reason, 1),
            new("at 20 400 ms", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        // LCS of ["at","10","200","ms"] and ["at","20","400","ms"] = ["at","ms"]
        // Both "10 200" and "20 400" are between "at" and "ms" → single gap
        result.MergedCategories[0].Label.ShouldBe("at * ms");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    // Category D: No merging (safety / dissimilarity)

    [Fact]
    public void KeepCategoriesSeparate_WhenSingleTokensDiffer()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("OrderProcessor", Reason, 1),
            new("PaymentHandler", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
    }

    [Fact]
    public void KeepCategoriesSeparate_WhenCompletelyDifferent()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Hello world", Reason, 1),
            new("Foo bar baz", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
    }

    [Fact]
    public void KeepCategoriesSeparate_WhenBelowThreshold()
    {
        // Arrange — only "Error" in common, 1/5 = 0.2 < 0.5
        var categories = new List<DlqCategory>
        {
            new("Error A B C D", Reason, 1),
            new("Error X Y Z W", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
    }

    [Fact]
    public void PreserveCategoryAsIs_WhenSingleCategory()
    {
        // Arrange
        var categories = new List<DlqCategory> { new("Error for user A", Reason, 5) };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error for user A");
        result.MergedCategories[0].Count.ShouldBe(5);
    }

    // Category E: Separate template formation (splitting)

    [Fact]
    public void FormSeparateTemplates_WhenDistinctPatterns()
    {
        // Arrange — templates differ in two words, so LCS score < 0.5 between groups
        var categories = new List<DlqCategory>
        {
            new("Error processing user A", Reason, 1),
            new("Error processing user B", Reason, 1),
            new("Error completing order C", Reason, 1),
            new("Error completing order D", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
        var labels = result.MergedCategories.Select(c => c.Label).OrderBy(l => l).ToList();
        labels.ShouldContain("Error completing order *");
        labels.ShouldContain("Error processing user *");
        result.MergedCategories.ShouldAllBe(c => c.Count == 2);
    }

    [Fact]
    public void FormSeparateTemplates_WhenMixOfMergeableAndNonMergeable()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error for user A", Reason, 1),
            new("Error for user B", Reason, 1),
            new("PaymentHandler", Reason, 1),
            new("OrderProcessor", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(3);
        result.MergedCategories.ShouldContain(c => c.Label == "Error for user *" && c.Count == 2);
        result.MergedCategories.ShouldContain(c => c.Label == "PaymentHandler" && c.Count == 1);
        result.MergedCategories.ShouldContain(c => c.Label == "OrderProcessor" && c.Count == 1);
    }

    [Fact]
    public void FormThreeSeparateTemplates_WhenThreeDistinctPatterns()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error for user A", Reason, 1),
            new("Error for user B", Reason, 1),
            new("Error for user C", Reason, 1),
            new("Timeout for service X", Reason, 1),
            new("Timeout for service Y", Reason, 1),
            new("Timeout for service Z", Reason, 1),
            new("Connection to host alpha", Reason, 1),
            new("Connection to host beta", Reason, 1),
            new("Connection to host gamma", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(3);
        result.MergedCategories.ShouldContain(c => c.Label == "Error for user *" && c.Count == 3);
        result.MergedCategories.ShouldContain(c => c.Label == "Timeout for service *" && c.Count == 3);
        result.MergedCategories.ShouldContain(c => c.Label == "Connection to host *" && c.Count == 3);
    }

    // Category F: Count aggregation and sorting

    [Fact]
    public void SumCounts_WhenCategoriesMerge()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", Reason, 5),
            new("Error B", Reason, 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Count.ShouldBe(8);
    }

    [Fact]
    public void SortByCountDescending_WhenMultipleTemplatesExist()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error for user A", Reason, 1),
            new("Error for user B", Reason, 2),
            new("Timeout for service X", Reason, 10),
            new("Timeout for service Y", Reason, 20)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
        result.MergedCategories[0].Count.ShouldBe(30);
        result.MergedCategories[1].Count.ShouldBe(3);
    }

    // Category G: MergeMap and ExpandKeys

    [Fact]
    public void BuildCorrectMergeMap_WhenCategoriesMerge()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", Reason, 1),
            new("Error B", Reason, 2),
            new("Error C", Reason, 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        var mergedKey = new DlqCategoryKey("Error *", Reason);
        result.MergeMap.ShouldContainKey(mergedKey);
        result.MergeMap[mergedKey].Count.ShouldBe(3);
        result.MergeMap[mergedKey].ShouldContain(new DlqCategoryKey("Error A", Reason));
        result.MergeMap[mergedKey].ShouldContain(new DlqCategoryKey("Error B", Reason));
        result.MergeMap[mergedKey].ShouldContain(new DlqCategoryKey("Error C", Reason));
    }

    [Fact]
    public void ReturnAllOriginalKeys_WhenMergedKeyExpanded()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", Reason, 1),
            new("Error B", Reason, 2),
            new("Error C", Reason, 3)
        };
        var result = CategoryMerger.Merge(categories);
        var mergedKey = new DlqCategoryKey("Error *", Reason);

        // Act
        var expanded = result.ExpandKeys(new HashSet<DlqCategoryKey> { mergedKey });

        // Assert
        expanded.Count.ShouldBe(3);
        expanded.ShouldContain(new DlqCategoryKey("Error A", Reason));
        expanded.ShouldContain(new DlqCategoryKey("Error B", Reason));
        expanded.ShouldContain(new DlqCategoryKey("Error C", Reason));
    }

    [Fact]
    public void ReturnKeyAsIs_WhenKeyNotInMergeMap()
    {
        // Arrange
        var result = CategoryMerger.Merge([]);
        var unknownKey = new DlqCategoryKey("Unknown", Reason);

        // Act
        var expanded = result.ExpandKeys(new HashSet<DlqCategoryKey> { unknownKey });

        // Assert
        expanded.Count.ShouldBe(1);
        expanded.ShouldContain(unknownKey);
    }

    [Fact]
    public void ReturnExpandedKeySet_WhenMixedMergedAndNonMergedSelected()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", Reason, 1),
            new("Error B", Reason, 2),
            new("Standalone", Reason, 5)
        };
        var result = CategoryMerger.Merge(categories);
        var mergedKey = new DlqCategoryKey("Error *", Reason);
        var standaloneKey = new DlqCategoryKey("Standalone", Reason);

        // Act
        var expanded = result.ExpandKeys(new HashSet<DlqCategoryKey>
        {
            mergedKey,
            standaloneKey
        });

        // Assert
        expanded.Count.ShouldBe(3);
        expanded.ShouldContain(new DlqCategoryKey("Error A", Reason));
        expanded.ShouldContain(new DlqCategoryKey("Error B", Reason));
        expanded.ShouldContain(new DlqCategoryKey("Standalone", Reason));
    }

    // Category H: Reason field handling

    [Fact]
    public void KeepCategoriesSeparate_WhenSameLabelsButDifferentReasons()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", "Timeout", 5),
            new("Error A", "InvalidMsg", 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(2);
    }

    [Fact]
    public void MergeBothFields_WhenBothLabelAndReasonHaveParams()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error 1", "Retry 100", 1),
            new("Error 2", "Retry 200", 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error *");
        result.MergedCategories[0].DeadLetterReason.ShouldBe("Retry *");
    }

    [Fact]
    public void PreserveConstantReason_WhenOnlyLabelVaries()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", "MaxDelivery", 1),
            new("Error B", "MaxDelivery", 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error *");
        result.MergedCategories[0].DeadLetterReason.ShouldBe("MaxDelivery");
    }

    // Category I: Edge cases

    [Fact]
    public void ReturnEmptyResult_WhenNoCategoriesProvided()
    {
        // Act
        var result = CategoryMerger.Merge([]);

        // Assert
        result.MergedCategories.ShouldBeEmpty();
        result.MergeMap.ShouldBeEmpty();
    }

    [Fact]
    public void MergeIdenticalCategories_WhenNoneSentinelLabel()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("(none)", Reason, 3),
            new("(none)", Reason, 2)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("(none)");
        result.MergedCategories[0].Count.ShouldBe(5);
    }

    [Fact]
    public void MergeWithWildcardLabel_WhenNoneSentinelReason()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", "(none)", 1),
            new("Error B", "(none)", 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error *");
        result.MergedCategories[0].DeadLetterReason.ShouldBe("(none)");
    }

    [Fact]
    public void MergeIdenticalCategories_WhenEmptyLabel()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("", Reason, 5),
            new("", Reason, 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("");
        result.MergedCategories[0].Count.ShouldBe(8);
    }

    [Fact]
    public void MergeAndSumCounts_WhenIdenticalCategories()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Error A", "Reason", 5),
            new("Error A", "Reason", 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Error A");
        result.MergedCategories[0].DeadLetterReason.ShouldBe("Reason");
        result.MergedCategories[0].Count.ShouldBe(8);
    }

    // Category J: Real-world-like scenarios

    [Fact]
    public void CorrectlyGroupCategories_WhenRealisticDlqData()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Could not create user with ID 3cefe1dd-91a0-490d-adfe-dc569472f6e9", Reason, 1),
            new("Could not create user with ID aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Reason, 1),
            new("Could not create user with ID 11111111-2222-3333-4444-555555555555", Reason, 1),
            new("Timeout processing order 12345", "TimeoutExceeded", 3),
            new("Timeout processing order 67890", "TimeoutExceeded", 2),
            new("Timeout processing order 11111", "TimeoutExceeded", 1),
            new("OrderProcessor", Reason, 47),
            new("PaymentHandler", "TimeoutExceeded", 23),
            new("Connection refused for host db-primary.internal", "ConnectionError", 5),
            new("Connection refused for host db-replica.internal", "ConnectionError", 3),
            new("Unexpected error", Reason, 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.ShouldContain(c =>
                                                  c.Label == "Could not create user with ID *" && c.Count == 3);
        result.MergedCategories.ShouldContain(c =>
                                                  c.Label == "Timeout processing order *" && c.Count == 6);
        result.MergedCategories.ShouldContain(c =>
                                                  c.Label == "Connection refused for host *" && c.Count == 8);
        result.MergedCategories.ShouldContain(c => c.Label == "OrderProcessor" && c.Count == 47);
        result.MergedCategories.ShouldContain(c => c.Label == "PaymentHandler" && c.Count == 23);
        result.MergedCategories.ShouldContain(c => c.Label == "Unexpected error" && c.Count == 1);
    }

    [Fact]
    public void CollapseIntoOneCategory_WhenHighCardinalityParameterized()
    {
        // Arrange — 50 categories all from same template but unique IDs
        var categories = Enumerable.Range(1, 50)
                                   .Select(i => new DlqCategory($"Failed to process entity {i:D8}", Reason, 1))
                                   .ToList();

        // Act
        var result = CategoryMerger.Merge(categories);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Label.ShouldBe("Failed to process entity *");
        result.MergedCategories[0].Count.ShouldBe(50);
    }

    // Category K: N-dimensional merging with custom schema

    [Fact]
    public void MergeNDimensionalCategories_WhenSchemaHasThreeDimensions()
    {
        // Arrange
        var schema = CategorizationSchema.Parse(["#Subject", "#DeadLetterReason", "$tier"]);
        var categories = new List<DlqCategory>
        {
            new(["Error A", Reason, "1"], 1),
            new(["Error B", Reason, "1"], 1)
        };

        // Act
        var result = CategoryMerger.Merge(categories, schema);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Values[0].ShouldBe("Error *");
        result.MergedCategories[0].Values[1].ShouldBe(Reason);
        result.MergedCategories[0].Values[2].ShouldBe("1");
        result.MergedCategories[0].Count.ShouldBe(2);
    }

    [Fact]
    public void KeepSeparate_WhenOneDimensionDiffersSignificantly()
    {
        // Arrange — same label pattern but different third dimension
        var schema = CategorizationSchema.Parse(["#Subject", "#DeadLetterReason", "$tier"]);
        var categories = new List<DlqCategory>
        {
            new(["Error A", Reason, "production"], 5),
            new(["Error B", Reason, "staging"], 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories, schema);

        // Assert — should NOT merge because third dimension ("production" vs "staging") scores below threshold
        result.MergedCategories.Count.ShouldBe(2);
    }

    [Fact]
    public void MergeSingleDimension_WhenSchemaHasOneDimension()
    {
        // Arrange
        var schema = CategorizationSchema.Parse(["#DeadLetterReason"]);
        var categories = new List<DlqCategory>
        {
            new(["Retry after 3 attempts"], 5),
            new(["Retry after 5 attempts"], 3)
        };

        // Act
        var result = CategoryMerger.Merge(categories, schema);

        // Assert
        result.MergedCategories.Count.ShouldBe(1);
        result.MergedCategories[0].Values[0].ShouldBe("Retry after * attempts");
        result.MergedCategories[0].Count.ShouldBe(8);
    }

    [Fact]
    public void BuildCorrectMergeMap_ForNDimensionalMerge()
    {
        // Arrange
        var schema = CategorizationSchema.Parse(["#Subject", "$tier"]);
        var categories = new List<DlqCategory>
        {
            new(["Error A", "1"], 1),
            new(["Error B", "1"], 2)
        };

        // Act
        var result = CategoryMerger.Merge(categories, schema);

        // Assert
        var mergedKey = result.MergedCategories[0].ToKey();
        result.MergeMap.ShouldContainKey(mergedKey);
        var originals = result.MergeMap[mergedKey];
        originals.Count.ShouldBe(2);
        originals.ShouldContain(new DlqCategoryKey("Error A", "1"));
        originals.ShouldContain(new DlqCategoryKey("Error B", "1"));
    }

    [Fact]
    public void ExpandKeys_ForNDimensionalMerge()
    {
        // Arrange
        var schema = CategorizationSchema.Parse(["#Subject", "$tier"]);
        var categories = new List<DlqCategory>
        {
            new(["Error A", "1"], 1),
            new(["Error B", "1"], 2)
        };
        var result = CategoryMerger.Merge(categories, schema);
        var mergedKey = result.MergedCategories[0].ToKey();

        // Act
        var expanded = result.ExpandKeys(new HashSet<DlqCategoryKey> { mergedKey });

        // Assert
        expanded.Count.ShouldBe(2);
    }
}
