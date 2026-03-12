using ServiceBusToolset.Application.DeadLetters.Common;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.DeadLetters.Common;

public class CategoryPropertyResolverShould
{
    private readonly CategoryPropertyResolver _resolver = new();

    // --- System property resolution ---

    [Fact]
    public void ResolveSubject_WhenSystemPropertyIsSubject()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithSubject("OrderProcessor")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "Subject");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("OrderProcessor");
    }

    [Fact]
    public void ResolveDeadLetterReason_WhenSystemPropertyIsDeadLetterReason()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "DeadLetterReason");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("MaxDeliveryCountExceeded");
    }

    [Fact]
    public void ResolveContentType_WhenSystemPropertyIsContentType()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithContentType("application/json")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "ContentType");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("application/json");
    }

    [Fact]
    public void ResolveMessageId_WhenSystemPropertyIsMessageId()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithMessageId("msg-123")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "MessageId");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("msg-123");
    }

    [Fact]
    public void ResolveCorrelationId_WhenSystemPropertyIsCorrelationId()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithCorrelationId("corr-456")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "CorrelationId");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("corr-456");
    }

    [Fact]
    public void ResolveToProperty_WhenSystemPropertyIsTo()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithTo("destination-queue")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "To");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("destination-queue");
    }

    [Fact]
    public void ResolveDeadLetterErrorDescription_WhenSet()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithDeadLetterErrorDescription("Something went wrong")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "DeadLetterErrorDescription");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("Something went wrong");
    }

    [Fact]
    public void ReturnNone_WhenSystemPropertyIsNull()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create().Build(); // No subject set
        var prop = new CategoryPropertyRef(PropertySource.System, "Subject");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    // --- ApplicationProperties fallback ---

    [Fact]
    public void FallBackToApplicationProperties_WhenSystemPropertyNotRecognized()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithApplicationProperty("CustomHeader", "custom-value")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "CustomHeader");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("custom-value");
    }

    [Fact]
    public void ReturnNone_WhenApplicationPropertyNotFound()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create().Build();
        var prop = new CategoryPropertyRef(PropertySource.System, "NonExistentProp");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    // --- Body property resolution ---

    [Fact]
    public void ResolveTopLevelBodyProperty()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new { errorCode = "E001", tier = 1 })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "errorCode");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("E001");
    }

    [Fact]
    public void ResolveNestedBodyProperty()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new { error = new { code = "E002", severity = "critical" } })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "error.code");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("E002");
    }

    [Fact]
    public void ResolveDeeplyNestedBodyProperty()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new
                                                      {
                                                          context = new
                                                          {
                                                              deployment = new { region = "us-east-1" }
                                                          }
                                                      })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "context.deployment.region");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("us-east-1");
    }

    [Fact]
    public void ResolveNumericBodyProperty()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new { tier = 2 })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "tier");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("2");
    }

    [Fact]
    public void ReturnNone_WhenBodyPropertyDoesNotExist()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new { name = "test" })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "nonexistent");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    [Fact]
    public void ReturnNone_WhenNestedPathDoesNotExist()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithJsonBody(new { error = new { code = "E001" } })
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "error.nonexistent.deep");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    [Fact]
    public void ReturnNone_WhenBodyIsNotJson()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("plain text, not json")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "field");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    [Fact]
    public void ReturnNone_WhenBodyIsPlainString()
    {
        // Arrange - a JSON string value like "hello" (valid JSON but not an object)
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("\"hello\"")
                                                      .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "field");

        // Act
        var result = _resolver.ResolveProperty(message, prop);

        // Assert
        result.ShouldBe("(none)");
    }

    // --- Caching ---

    [Fact]
    public void CacheDecodedBody_WhenSameMessageAccessedTwice()
    {
        // Arrange
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithSequenceNumber(42)
                                                      .WithJsonBody(new { code = "E001", severity = "warning" })
                                                      .Build();
        var codeProp = new CategoryPropertyRef(PropertySource.Body, "code");
        var severityProp = new CategoryPropertyRef(PropertySource.Body, "severity");

        // Act - resolve two different body properties from the same message
        var code = _resolver.ResolveProperty(message, codeProp);
        var severity = _resolver.ResolveProperty(message, severityProp);

        // Assert - both should resolve correctly (body decoded once, cached)
        code.ShouldBe("E001");
        severity.ShouldBe("warning");
    }

    [Fact]
    public void ResolveDifferentMessages_Independently()
    {
        // Arrange
        var message1 = ServiceBusReceivedMessageBuilder.Create()
                                                       .WithSequenceNumber(1)
                                                       .WithJsonBody(new { tier = 1 })
                                                       .Build();
        var message2 = ServiceBusReceivedMessageBuilder.Create()
                                                       .WithSequenceNumber(2)
                                                       .WithJsonBody(new { tier = 2 })
                                                       .Build();
        var prop = new CategoryPropertyRef(PropertySource.Body, "tier");

        // Act
        var result1 = _resolver.ResolveProperty(message1, prop);
        var result2 = _resolver.ResolveProperty(message2, prop);

        // Assert
        result1.ShouldBe("1");
        result2.ShouldBe("2");
    }
}
