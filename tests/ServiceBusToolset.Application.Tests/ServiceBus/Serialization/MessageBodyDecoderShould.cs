using System.Text.Json.Nodes;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Serialization;

public class MessageBodyDecoderShould
{
    [Fact]
    public void ReturnJsonObject_WhenBodyIsValidJson()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("{\"name\": \"test\", \"value\": 123}")
                                                      .WithContentType("application/json")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeOfType<JsonObject>();
        result["name"]?.GetValue<string>().ShouldBe("test");
        result["value"]?.GetValue<int>().ShouldBe(123);
    }

    [Fact]
    public void ReturnJsonValue_WhenBodyIsPlainText()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("Hello, World!")
                                                      .WithContentType("text/plain")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeAssignableTo<JsonValue>();
        result.GetValue<string>().ShouldBe("Hello, World!");
    }

    [Fact]
    public void ReturnJsonObject_WhenNoContentTypeButValidJson()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("{\"key\": \"value\"}")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeOfType<JsonObject>();
        result["key"]?.GetValue<string>().ShouldBe("value");
    }

    [Fact]
    public void ReturnBase64String_WhenContentIsBinary()
    {
        var binaryData = new byte[]
        {
            0x00,
            0x01,
            0x02,
            0xFF,
            0xFE
        };
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody(BinaryData.FromBytes(binaryData))
                                                      .WithContentType("application/octet-stream")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeAssignableTo<JsonValue>();
        var base64 = result.GetValue<string>();
        Convert.FromBase64String(base64).ShouldBe(binaryData);
    }

    [Fact]
    public void ReturnEmptyJsonObject_WhenBodyIsEmptyJsonObject()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("{}")
                                                      .WithContentType("application/json")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeOfType<JsonObject>();
        (result as JsonObject)!.Count.ShouldBe(0);
    }

    [Fact]
    public void ReturnJsonObject_WhenContentTypeContainsJson()
    {
        // Tests the Contains("json") path
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("{\"test\": true}")
                                                      .WithContentType("application/vnd.custom+json")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeOfType<JsonObject>();
        result["test"]?.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void ReturnJsonArray_WhenBodyIsJsonArray()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("[1, 2, 3]")
                                                      .WithContentType("application/json")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeOfType<JsonArray>();
        (result as JsonArray)!.Count.ShouldBe(3);
    }

    [Fact]
    public void ReturnBase64_WhenBodyContainsInvalidUtf8()
    {
        // Create bytes that include invalid UTF-8 replacement character trigger
        var invalidUtf8 = new byte[]
        {
            0xFF,
            0xFE,
            0x00,
            0x01
        };
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody(BinaryData.FromBytes(invalidUtf8))
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeAssignableTo<JsonValue>();
        var base64 = result.GetValue<string>();
        Convert.FromBase64String(base64).ShouldBe(invalidUtf8);
    }

    [Fact]
    public void ReturnJsonValue_WhenUtf8TextIsNotJson()
    {
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithBody("This is plain text without JSON structure")
                                                      .Build();

        var result = MessageBodyDecoder.Decode(message);

        result.ShouldBeAssignableTo<JsonValue>();
        result.GetValue<string>().ShouldBe("This is plain text without JSON structure");
    }
}
