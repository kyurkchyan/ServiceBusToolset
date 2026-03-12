using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using ServiceBusToolset.Application.Common.ServiceBus.Serialization;

namespace ServiceBusToolset.Application.DeadLetters.Common;

public sealed class CategoryPropertyResolver
{
    private readonly ConcurrentDictionary<long, JsonNode?> _bodyCache = new();

    /// <summary>
            /// Resolves a property value from a Service Bus message based on the provided property reference.
            /// </summary>
            /// <param name="message">The Service Bus message to read the property from.</param>
            /// <param name="propertyRef">Reference that specifies the property source (system or body) and the property path to resolve.</param>
            /// <returns>The resolved property value as a string, or "(none)" if the property is missing or cannot be decoded.</returns>
            public string ResolveProperty(ServiceBusReceivedMessage message, CategoryPropertyRef propertyRef) =>
        propertyRef.Source == PropertySource.System
            ? ResolveSystemProperty(message, propertyRef.PropertyPath)
            : ResolveBodyProperty(message, propertyRef.PropertyPath);

    /// <summary>
    /// Resolves a well-known system property or an application property value from the given Service Bus message.
    /// </summary>
    /// <param name="propertyName">The system property name (e.g., "Subject", "MessageId", "DeadLetterReason", "ContentType", "CorrelationId", "SessionId", "ReplyTo", "To", "DeadLetterErrorDescription") or an application property key to look up in ApplicationProperties.</param>
    /// <returns>The property's string value if present; otherwise the literal "(none)".</returns>
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

    /// <summary>
    /// Resolves a value from the message JSON body by navigating a dot-separated property path, using a cached decoded body keyed by the message's SequenceNumber.
    /// </summary>
    /// <param name="message">The Service Bus message whose body is decoded and cached for lookup.</param>
    /// <param name="propertyPath">Dot-separated path (e.g., "order.customer.name") identifying the property to retrieve from the body.</param>
    /// <returns>
    /// The property's string value; for objects or arrays returns their JSON string representation; returns "(none)" if the body is absent, cannot be decoded, or the path does not exist.
    /// </returns>
    private string ResolveBodyProperty(ServiceBusReceivedMessage message, string propertyPath)
    {
        var body = _bodyCache.GetOrAdd(message.SequenceNumber, _ => TryDecodeBody(message));

        if (body == null)
        {
            return "(none)";
        }

        return NavigateJsonPath(body, propertyPath);
    }

    /// <summary>
    /// Decode the Service Bus message body into a JsonNode when possible.
    /// </summary>
    /// <param name="message">The Service Bus message whose body will be decoded.</param>
    /// <returns>The decoded <see cref="JsonNode"/>, or <c>null</c> if the body is a JSON string, cannot be decoded, or an error occurs.</returns>
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

    /// <summary>
    /// Retrieves a value from a JSON node following a dot-separated property path.
    /// </summary>
    /// <param name="node">The root JSON node to navigate.</param>
    /// <param name="path">Dot-separated sequence of property names (e.g. "a.b.c").</param>
    /// <returns>
    /// The string representation of a terminal JSON value if the path resolves; the compact JSON string for objects or arrays; "(none)" if the path cannot be resolved or the terminal node is not a value/object/array.
    /// </returns>
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
