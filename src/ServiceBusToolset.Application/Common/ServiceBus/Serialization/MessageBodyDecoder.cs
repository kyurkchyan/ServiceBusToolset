using System.Text;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;

namespace ServiceBusToolset.Application.Common.ServiceBus.Serialization;

/// <summary>
/// Utility for decoding Service Bus message bodies into readable formats.
/// </summary>
public static class MessageBodyDecoder
{
    /// <summary>
    /// Attempts to decode a message body into a JSON node.
    /// Returns the body as JSON if possible, as a string value if text, or as Base64 for binary content.
    /// </summary>
    public static JsonNode Decode(ServiceBusReceivedMessage message)
    {
        var text = TryDecodeAsText(message);

        if (text != null)
        {
            return TryParseAsJson(text) ?? JsonValue.Create(text);
        }

        return JsonValue.Create(Convert.ToBase64String(message.Body.ToArray()));
    }

    private static string? TryDecodeAsText(ServiceBusReceivedMessage message)
    {
        // Try content-type hint first
        if (message.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
            message.ContentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return message.Body.ToString();
            }
            catch
            {
                // Fall through to UTF-8 decode
            }
        }

        // Try to decode as UTF-8 text
        try
        {
            var decoded = Encoding.UTF8.GetString(message.Body.ToArray());
            // Check if it's valid UTF-8 text (no replacement characters)
            if (!decoded.Contains('\uFFFD'))
            {
                return decoded;
            }
        }
        catch
        {
            // Binary content
        }

        return null;
    }

    private static JsonNode? TryParseAsJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
    }
}
