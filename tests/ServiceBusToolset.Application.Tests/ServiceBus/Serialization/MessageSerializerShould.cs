using System.Text.Json;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;
using ServiceBusToolset.Application.Tests.Common.Builders;
using Shouldly;
using Xunit;
using ServiceBusMessage = ServiceBusToolset.Application.Common.ServiceBus.Models.ServiceBusMessage;

namespace ServiceBusToolset.Application.Tests.ServiceBus.Serialization;

public class MessageSerializerShould : IDisposable
{
    private readonly string _testFilePath = Path.Combine(Path.GetTempPath(), $"test-messages-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    [Fact]
    public void MapAllProperties_WhenConvertingToDto()
    {
        var enqueuedTime = DateTimeOffset.UtcNow;
        var message = ServiceBusReceivedMessageBuilder.Create()
                                                      .WithMessageId("msg-123")
                                                      .WithCorrelationId("corr-456")
                                                      .WithSubject("TestSubject")
                                                      .WithContentType("application/json")
                                                      .WithJsonBody(new { name = "test" })
                                                      .WithDeadLetterReason("MaxDeliveryCountExceeded")
                                                      .WithEnqueuedTime(enqueuedTime)
                                                      .WithSequenceNumber(42)
                                                      .WithApplicationProperty("custom-prop", "custom-value")
                                                      .Build();

        var dto = MessageSerializer.ToDto(message);

        dto.MessageId.ShouldBe("msg-123");
        dto.CorrelationId.ShouldBe("corr-456");
        dto.Subject.ShouldBe("TestSubject");
        dto.ContentType.ShouldBe("application/json");
        dto.DeadLetterReason.ShouldBe("MaxDeliveryCountExceeded");
        dto.EnqueuedTime.ShouldBe(enqueuedTime);
        dto.SequenceNumber.ShouldBe(42);
        dto.ApplicationProperties.ShouldContainKey("custom-prop");
    }

    [Fact]
    public void ConvertMultipleMessages_WhenCallingToDtoList()
    {
        var messages = new[]
        {
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-1").WithBody("{}").Build(),
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-2").WithBody("{}").Build(),
            ServiceBusReceivedMessageBuilder.Create().WithMessageId("msg-3").WithBody("{}").Build()
        };

        var dtos = MessageSerializer.ToDtoList(messages);

        dtos.Count.ShouldBe(3);
        dtos[0].MessageId.ShouldBe("msg-1");
        dtos[1].MessageId.ShouldBe("msg-2");
        dtos[2].MessageId.ShouldBe("msg-3");
    }

    [Fact]
    public async Task WriteValidJson_WhenCallingWriteJsonAsync()
    {
        var messages = new List<ServiceBusMessage>
        {
            new()
            {
                MessageId = "msg-1",
                Subject = "Test Subject"
            }
        };

        await MessageSerializer.WriteJsonAsync(_testFilePath, messages);

        File.Exists(_testFilePath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(_testFilePath);
        var parsed = JsonDocument.Parse(content);
        parsed.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        parsed.RootElement.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task UseCamelCasePropertyNames_WhenWritingJson()
    {
        var messages = new List<ServiceBusMessage>
        {
            new()
            {
                MessageId = "test",
                EnqueuedTime = DateTimeOffset.UtcNow
            }
        };

        await MessageSerializer.WriteJsonAsync(_testFilePath, messages);

        var content = await File.ReadAllTextAsync(_testFilePath);
        content.ShouldContain("\"messageId\"");
        content.ShouldContain("\"enqueuedTime\"");
        content.ShouldNotContain("\"MessageId\"", Case.Sensitive);
    }
}
