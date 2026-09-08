using System.Buffers;
using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// A response-aware Gateway RPC rejection. Structured details are bounded and
/// sanitized before being retained, while capability review tokens remain exact
/// so a caller can complete the Gateway's artifact-bound consent handshake.
/// </summary>
public sealed class GatewayRequestException : InvalidOperationException
{
    internal GatewayRequestException(
        string method,
        string message,
        string? code,
        JsonElement? details)
        : base(message)
    {
        Method = method;
        Code = code;
        Details = details;
    }

    public string Method { get; }
    public string? Code { get; }
    public JsonElement? Details { get; }

    internal static GatewayRequestException FromResponse(
        string method,
        JsonElement response,
        string fallbackMessage)
    {
        var message = ReadErrorString(response, "message") ?? fallbackMessage;
        var code = ReadErrorString(response, "code");
        var details = ReadErrorDetails(response);
        return new GatewayRequestException(
            method,
            TokenSanitizer.Sanitize(message),
            code,
            details.HasValue ? GatewayErrorDetailsSanitizer.Sanitize(details.Value) : null);
    }

    private static string? ReadErrorString(JsonElement response, string propertyName)
    {
        if (!response.TryGetProperty("error", out var error))
            return null;
        if (propertyName == "message" && error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static JsonElement? ReadErrorDetails(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (error.TryGetProperty("details", out var details))
            return details.Clone();

        if (error.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("details", out details))
        {
            return details.Clone();
        }

        return null;
    }
}

internal static class GatewayErrorDetailsSanitizer
{
    private const int MaxDepth = 8;
    private const int MaxItems = 128;
    private const int MaxStringLength = 4096;
    private const string Redacted = "[REDACTED]";

    internal static JsonElement Sanitize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, value, depth: 0, propertyName: null);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        JsonElement value,
        int depth,
        string? propertyName)
    {
        if (depth >= MaxDepth)
        {
            writer.WriteStringValue("[TRUNCATED]");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var propertyCount = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (propertyCount++ >= MaxItems) break;
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteValue(writer, property.Value, depth + 1, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                var itemCount = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (itemCount++ >= MaxItems) break;
                    WriteValue(writer, item, depth + 1, propertyName);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Length > MaxStringLength)
                    text = text[..MaxStringLength];
                writer.WriteStringValue(
                    string.Equals(propertyName, "reviewToken", StringComparison.Ordinal)
                        ? text
                        : TokenSanitizer.Sanitize(text));
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                value.WriteTo(writer);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static bool IsSensitiveProperty(string propertyName) =>
        !string.Equals(propertyName, "reviewToken", StringComparison.Ordinal) &&
        TokenSanitizer.IsSensitiveMetadataKeyName(propertyName);
}
