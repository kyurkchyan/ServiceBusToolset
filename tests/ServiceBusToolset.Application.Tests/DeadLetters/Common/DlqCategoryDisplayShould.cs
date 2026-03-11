using ServiceBusToolset.Application.DeadLetters.Common;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class DlqCategoryDisplayShould
{
    [Fact]
    public void GenerateTableData_WithCategories()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("OrderProcessor", "MaxDeliveryCountExceeded", 10),
            new("PaymentHandler", "TimeoutExceeded", 5)
        };

        // Act
        var (headers, rows) = DlqCategoryDisplay.GenerateTableData(categories);

        // Assert
        headers.ShouldBe(new[]
        {
            "#",
            "#Subject",
            "#DeadLetterReason",
            "Count"
        });

        var rowList = rows.ToList();
        rowList.Count.ShouldBe(2);

        rowList[0].ShouldBe(new[]
        {
            "1",
            "OrderProcessor",
            "MaxDeliveryCountExceeded",
            "10"
        });
        rowList[1].ShouldBe(new[]
        {
            "2",
            "PaymentHandler",
            "TimeoutExceeded",
            "5"
        });
    }

    [Fact]
    public void GenerateTableData_WithEmptyCategories()
    {
        // Arrange
        var categories = new List<DlqCategory>();

        // Act
        var (headers, rows) = DlqCategoryDisplay.GenerateTableData(categories);

        // Assert
        headers.ShouldBe(new[]
        {
            "#",
            "#Subject",
            "#DeadLetterReason",
            "Count"
        });
        rows.ShouldBeEmpty();
    }

    [Fact]
    public void GenerateTableData_ReplacesLineEndings()
    {
        // Arrange
        var categories = new List<DlqCategory> { new("Label\nWith\nNewlines", "Reason\r\nWith\r\nCRLF", 1) };

        // Act
        var (_, rows) = DlqCategoryDisplay.GenerateTableData(categories);

        // Assert
        var row = rows.Single();
        row[1].ShouldNotContain("\n");
        row[1].ShouldNotContain("\r");
        row[2].ShouldNotContain("\n");
        row[2].ShouldNotContain("\r");
    }

    [Fact]
    public void DisplayTable_CallsOutputActions()
    {
        // Arrange
        var categories = new List<DlqCategory> { new("TestLabel", "TestReason", 25) };

        var writtenLines = new List<string>();
        var tableWritten = false;
        IEnumerable<string>? capturedHeaders = null;
        IEnumerable<string[]>? capturedRows = null;

        // Act
        DlqCategoryDisplay.DisplayTable(categories,
                                        25,
                                        line => writtenLines.Add(line),
                                        (headers, rows) =>
                                        {
                                            tableWritten = true;
                                            capturedHeaders = headers;
                                            capturedRows = rows;
                                        });

        // Assert
        writtenLines.ShouldContain("");
        writtenLines.ShouldContain("Dead Letter Summary:");
        writtenLines.ShouldContain("Total: 25 messages");
        tableWritten.ShouldBeTrue();
        capturedHeaders.ShouldNotBeNull();
        capturedRows.ShouldNotBeNull();
    }

    [Fact]
    public void DisplayTable_ShowsCorrectTotal()
    {
        // Arrange
        var categories = new List<DlqCategory>
        {
            new("Cat1", "Reason1", 100),
            new("Cat2", "Reason2", 50)
        };

        var writtenLines = new List<string>();

        // Act
        DlqCategoryDisplay.DisplayTable(categories,
                                        150,
                                        line => writtenLines.Add(line),
                                        (_, _) => { });

        // Assert
        writtenLines.ShouldContain("Total: 150 messages");
    }
}
