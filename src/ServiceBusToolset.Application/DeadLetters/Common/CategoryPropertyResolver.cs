using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class CategoryPropertyResolver
{
    private readonly ConcurrentDictionary<long, JsonNode?> _bodyCache = new();

    public string ResolveProperty(ServiceBusReceivedMessage message, CategoryPropertyRef propertyRef) =>
        propertyRef.Source == PropertySource.System
            ? ResolveSystemProperty(message, propertyRef.PropertyPath)
            : ResolveBodyProperty(message, propertyRef.PropertyPath);

    private static string ResolveSystemProperty(ServiceBusReceivedMessage message, string propertyName)
    {
        var value = propertyName switch
        {
            "Subject" => message.Subject,
            "DeadLetterReason" => message.DeadLetterReason,
            "ContentType" => message.ContentType,
            "CorrelationId" => message.CorrelationId,
            "MessageId" => message.MessageId,
            "SessionId" => message.SessionId,
            "ReplyTo" => message.ReplyTo,
            "To" => message.To,
            "DeadLetterErrorDescription" => message.DeadLetterErrorDescription,
            _ => message.ApplicationProperties.TryGetValue(propertyName, out var appPropValue)
                     ? appPropValue?.ToString()
                     : null
        };

        return value ?? "(none)";
    }

    private string ResolveBodyProperty(ServiceBusReceivedMessage message, string propertyPath)
    {
        var body = _bodyCache.GetOrAdd(message.SequenceNumber, _ => TryDecodeBody(message));

        if (body == null)
        {
            return "(none)";
        }

        return NavigateJsonPath(body, propertyPath);
    }

    private static JsonNode? TryDecodeBody(ServiceBusReceivedMessage message)
    {
        try
        {
            var decoded = MessageBodyDecoder.Decode(message);
            return decoded is JsonValue jv && jv.GetValueKind() == JsonValueKind.String
                       ? null
                       : decoded;
        }
        catch
        {
            return null;
        }
    }

    private static string NavigateJsonPath(JsonNode node, string path)
    {
        var segments = path.Split('.');
        var current = node;

        foreach (var segment in segments)
        {
            if (current is not JsonObject obj)
            {
                return "(none)";
            }

            current = obj[segment];
            if (current == null)
            {
                return "(none)";
            }
        }

        if (current is JsonValue value)
        {
            return value.ToString();
        }

        if (current is JsonArray or JsonObject)
        {
            return current.ToJsonString();
        }

        return "(none)";
    }
}
