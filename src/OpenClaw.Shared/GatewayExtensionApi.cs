using System.Text.Json;

namespace OpenClaw.Shared;

internal abstract class GatewayExtensionApi
{
    private readonly Func<string, object?, int, Task<JsonElement>> _sendRequest;
    private readonly Action<string> _ensureMethodSupported;

    protected GatewayExtensionApi(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        Action<string> ensureMethodSupported)
    {
        _sendRequest = sendRequest;
        _ensureMethodSupported = ensureMethodSupported;
    }

    protected async Task<T> SendAsync<T>(string method, object? parameters, int timeoutMs)
        where T : class
    {
        EnsureMethodSupported(method);
        var payload = await _sendRequest(method, parameters, timeoutMs).ConfigureAwait(false);
        return DeserializePayload<T>(payload, method);
    }

    protected void EnsureMethodSupported(string method) =>
        _ensureMethodSupported(method);

    protected static T DeserializePayload<T>(JsonElement payload, string method)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonSerializerOptionsCache.GatewayProtocol)
                ?? throw new JsonException("Payload was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Gateway returned an invalid {method} response.", ex);
        }
    }

    protected static Dictionary<string, object?> OptionalAgentParameters(string? agentId)
    {
        var parameters = new Dictionary<string, object?>();
        AddOptionalString(parameters, "agentId", agentId);
        return parameters;
    }

    protected static void AddOptionalString(
        IDictionary<string, object?> parameters,
        string propertyName,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters[propertyName] = value;
    }

    protected static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
    }

    protected static void RequireNonEmpty(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", parameterName);
    }
}
