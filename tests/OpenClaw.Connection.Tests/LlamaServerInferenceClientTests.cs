using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerInferenceClientTests
{
    private const string ModelAlias = "qwen3.6-27b-mtp-q4-k-m";
    private static readonly Uri s_endpoint = new("http://127.0.0.1:18803/v1");

    /// <summary>
    /// The body llama-server actually returns when a model instance dies during load — the case
    /// that previously surfaced as a bare "HTTP 500 (InternalServerError)" with no root cause.
    /// </summary>
    private const string ModelLoadFailureBody =
        """
        {"error":{"code":500,"message":"model name=qwen3.6-27b-mtp-q4-k-m failed to load","type":"server_error"}}
        """;

    [Fact]
    public async Task VerifyAsync_SurfacesLlamaServerErrorBodyOnHttpFailure()
    {
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, ModelLoadFailureBody))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Equal(500, failure.StatusCode);
        Assert.Equal("model name=qwen3.6-27b-mtp-q4-k-m failed to load", failure.ServerError);
        Assert.Contains("HTTP 500", failure.Message, StringComparison.Ordinal);
        Assert.Contains("failed to load", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_RedactsAndNormalizesLlamaServerErrorBody()
    {
        const string secret = "test-secret-value";
        string payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = $"model failed\u2028Authorization: Bearer {secret}\u2029retry disabled",
            },
        });
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, payload))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Equal(
            "model failed Authorization: [REDACTED]",
            failure.ServerError);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2028', failure.Message);
        Assert.DoesNotContain('\u2029', failure.Message);
    }

    [Fact]
    public async Task VerifyAsync_RejectsVerbosePayloadRecordInsideRecognizedError()
    {
        const string sentinel = "SENTINEL-PROMPT-CONTENT";
        string payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = $"log_server_r: request: {{\"content\":\"{sentinel}\"}}",
            },
        });
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, payload))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Null(failure.ServerError);
        Assert.Equal(
            "llama-server inference returned HTTP 500 (InternalServerError).",
            failure.Message);
        Assert.DoesNotContain(sentinel, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Security-boundary regression: a body that is not llama-server's recognized
    /// <c>{"error": ...}</c> shape — HTML, malformed JSON, or JSON with a different shape — must
    /// never surface, even truncated. Only a recognized error field is server diagnostic evidence;
    /// anything else is an unvetted raw body and must fall back to status-only. Each case below
    /// embeds a sentinel that must never reach <see cref="LlamaServerInferenceException.Message"/>
    /// or <see cref="LlamaServerInferenceException.ServerError"/>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<html>SENTINEL-UNRECOGNIZED-BODY</html>")]
    [InlineData("{SENTINEL-UNRECOGNIZED-BODY")]
    [InlineData("""{"detail":"SENTINEL-UNRECOGNIZED-BODY"}""")]
    [InlineData("oversized")]
    public async Task VerifyAsync_FallsBackToStatusOnlyWhenErrorBodyIsUnusable(string body)
    {
        string payload = body == "oversized"
            ? new string('x', 32 * 1024) + "SENTINEL-UNRECOGNIZED-BODY"
            : body;
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.InternalServerError, payload))));

        LlamaServerInferenceException failure =
            await Assert.ThrowsAsync<LlamaServerInferenceException>(
                () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.Equal(500, failure.StatusCode);
        Assert.Equal(
            "llama-server inference returned HTTP 500 (InternalServerError).",
            failure.Message);
        Assert.Null(failure.ServerError);
        Assert.DoesNotContain("SENTINEL", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the class privacy contract: assistant output must never reach an exception message,
    /// even now that the failure path reads a response body.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_DoesNotLeakAssistantContent()
    {
        const string sentinel = "SENTINEL-ASSISTANT-CONTENT";
        string payload = JsonSerializer.Serialize(new
        {
            model = "a-different-alias",
            choices = new[] { new { message = new { content = sentinel } } },
        });
        using var client = new LlamaServerInferenceClient(
            new DelegateHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, payload))));

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.VerifyAsync(s_endpoint, ModelAlias));

        Assert.DoesNotContain(sentinel, failure.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string payload) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
