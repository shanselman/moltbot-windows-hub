using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerInferenceVerification(
    string ModelId,
    int PromptTokens,
    int CompletionTokens,
    double PromptMilliseconds,
    double CompletionMilliseconds);

/// <summary>
/// llama-server rejected the setup-time request. Carries the server's own error text, never prompt
/// or response content. Derives from <see cref="IOException"/> because <c>InvalidDataException</c>
/// is sealed; callers already filter on <see cref="IOException"/> alongside it, so the existing
/// failure handling keeps working.
/// </summary>
public sealed class LlamaServerInferenceException(string message, int statusCode, string? serverError)
    : IOException(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ServerError { get; } = serverError;
}

public interface ILlamaServerInferenceClient : IDisposable
{
    /// <summary>
    /// Sends one bounded OpenAI-compatible request to the managed endpoint, intentionally triggering
    /// lazy model loading during setup. Verifies the response plus token and timing evidence without
    /// returning or logging prompt or response content.
    /// </summary>
    Task<LlamaServerInferenceVerification> VerifyAsync(
        Uri endpoint,
        string modelAlias,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends one bounded OpenAI-compatible request to the managed router. This is
/// the setup-time first request, so it intentionally triggers lazy model load.
/// Prompt and response content are never returned or logged, except llama-server's
/// own error text on a failed request.
/// </summary>
public sealed partial class LlamaServerInferenceClient : ILlamaServerInferenceClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumErrorBytes = 8 * 1024;
    private const int MaximumErrorDetailLength = 400;
    private readonly HttpClient _client;

    public LlamaServerInferenceClient() : this(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    {
    }

    internal LlamaServerInferenceClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Sends one bounded OpenAI-compatible request to the managed endpoint, intentionally triggering
    /// lazy model loading during setup. Verifies the response plus token and timing evidence without
    /// returning or logging prompt or response content.
    /// </summary>
    public async Task<LlamaServerInferenceVerification> VerifyAsync(
        Uri endpoint,
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);

        Uri requestUri = new(endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(new
            {
                model = modelAlias,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = "Reply with a short confirmation that local inference is ready.",
                    },
                },
                max_tokens = 32,
                temperature = 0,
                stream = false,
            }),
        };

        using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? serverError = await ReadErrorDetailAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            throw new LlamaServerInferenceException(
                serverError is null
                    ? $"llama-server inference returned HTTP {(int)response.StatusCode} ({response.StatusCode})."
                    : $"llama-server inference returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {serverError}",
                (int)response.StatusCode,
                serverError);
        }

        byte[] payload = await ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("model", out JsonElement model) ||
            model.ValueKind != JsonValueKind.String ||
            !string.Equals(model.GetString(), modelAlias, StringComparison.Ordinal))
        {
            throw new InvalidDataException("llama-server inference did not report the selected model alias.");
        }

        ValidateAssistantOutput(root);
        (int promptTokens, int completionTokens) = ReadUsage(root);
        (double promptMilliseconds, double completionMilliseconds) = ReadTimings(root);
        return new(
            modelAlias,
            promptTokens,
            completionTokens,
            promptMilliseconds,
            completionMilliseconds);
    }

    private static void ValidateAssistantOutput(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidDataException("llama-server inference returned no choices.");
        }

        JsonElement choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            (!HasNonemptyString(message, "content") &&
             !HasNonemptyString(message, "reasoning_content")))
        {
            throw new InvalidDataException("llama-server inference returned no assistant output.");
        }
    }

    private static bool HasNonemptyString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString());

    private static (int PromptTokens, int CompletionTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("prompt_tokens", out JsonElement promptTokens) ||
            !promptTokens.TryGetInt32(out int prompt) || prompt <= 0 ||
            !usage.TryGetProperty("completion_tokens", out JsonElement completionTokens) ||
            !completionTokens.TryGetInt32(out int completion) || completion <= 0)
        {
            throw new InvalidDataException("llama-server inference returned invalid token usage.");
        }

        return (prompt, completion);
    }

    private static (double PromptMilliseconds, double CompletionMilliseconds) ReadTimings(JsonElement root)
    {
        if (!root.TryGetProperty("timings", out JsonElement timings) ||
            timings.ValueKind != JsonValueKind.Object ||
            !TryReadNonnegativeDouble(timings, "prompt_ms", out double promptMilliseconds) ||
            !TryReadNonnegativeDouble(timings, "predicted_ms", out double completionMilliseconds))
        {
            throw new InvalidDataException("llama-server inference returned invalid timing evidence.");
        }

        return (promptMilliseconds, completionMilliseconds);
    }

    private static bool TryReadNonnegativeDouble(JsonElement value, string propertyName, out double result)
    {
        result = 0;
        return value.TryGetProperty(propertyName, out JsonElement property) &&
            property.TryGetDouble(out result) &&
            double.IsFinite(result) &&
            result >= 0;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port is <= 0 or > 65_535 ||
            endpoint.Port == 80 ||
            !string.Equals(endpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "The llama-server endpoint must use an explicit IPv4 loopback /v1 address.",
                nameof(endpoint));
        }
    }

    /// <summary>
    /// Best-effort extraction of llama-server's own error text so a failed setup reports a root
    /// cause instead of a bare status code. Never throws, and never reads assistant output: this
    /// runs only on a non-success response, and reads only llama-server's recognized
    /// <c>{"error": ...}</c> shape. Anything else — HTML, a differently-shaped JSON body, or a
    /// truncated/malformed payload — is not attributable to llama-server's own diagnostics and
    /// yields <see langword="null"/> (status-only) rather than surfacing an unvetted raw body
    /// through the exception message, setup log, and completion UI.
    /// </summary>
    private static async Task<string?> ReadErrorDetailAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        try
        {
            payload = await ReadBoundedAsync(content, MaximumErrorBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException)
        {
            return null;
        }
        if (payload.Length == 0)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload, new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return Sanitize(error.GetString());
                if (error.ValueKind == JsonValueKind.Object)
                {
                    return Sanitize(ReadStringProperty(error, "message"))
                        ?? Sanitize(ReadStringProperty(error, "type"));
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, or truncated. No recognized llama-server error shape to surface.
        }

        return null;
    }

    private static string? ReadStringProperty(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (VerbosePayloadRecordPattern().IsMatch(value))
            return null;

        value = TokenSanitizer.SanitizeLogMessage(value);
        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsSeparator(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
            if (builder.Length >= MaximumErrorDetailLength)
                break;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?:\blog_server_[a-z_]*|\brequest|\bresponse)\s*:",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VerbosePayloadRecordPattern();

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The llama-server inference response exceeds the size limit.");

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The llama-server inference response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    public void Dispose() => _client.Dispose();
}
