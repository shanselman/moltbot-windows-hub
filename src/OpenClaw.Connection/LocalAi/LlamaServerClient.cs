// <summary>
// Bounded, loopback-only health and model-state client for the managed llama-server router.
// Parses router model metadata (path from status.args or top-level path) into
// LlamaServerModelStatusEvidence, and LlamaServerClient polls /health plus model state to
// produce LlamaServerRouterProbeResult used to decide when the managed endpoint is ready.
// Usage:
//   using var client = new LlamaServerClient();
//   LlamaServerRouterProbeResult probe = await client.ProbeManagedModelAsync(
//       endpoint: new Uri("http://127.0.0.1:18803"),
//       modelAlias: "local-model",
//       expectedModelPath: install.ModelPath,
//       cancellationToken);
//   if (probe.IsReadyForManagedModel(install.ModelPath)) { /* router is healthy and serving the model */ }
// </summary>
using System.Text.Json;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerRouterProbeResult(
    bool IsHealthy,
    LocalAiModelAvailabilityState ModelState,
    string? ReportedModelPath,
    string? Detail)
{
    internal bool IsReadyForManagedModel(string expectedModelPath) =>
        IsHealthy &&
        ModelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded &&
        !string.IsNullOrWhiteSpace(ReportedModelPath) &&
        LlamaServerModelStatusParser.PathsEqual(ReportedModelPath, expectedModelPath);
}

public sealed record LlamaServerModelStatusEvidence(
    LocalAiModelAvailabilityState State,
    string ModelPath,
    string ServerStatus);

/// <summary>
/// Parses the router model metadata emitted by qualified llama-server builds.
/// Unloaded preset models in b10488 report their path in status.args, while a
/// loaded model may also expose the documented top-level path field.
/// </summary>
public static class LlamaServerModelStatusParser
{
    public static LlamaServerModelStatusEvidence? Parse(
        JsonElement root,
        string modelAlias,
        string expectedModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelPath);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The llama-server model status response has an invalid shape.");
        }

        JsonElement? match = null;
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("id", out JsonElement id) ||
                id.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The llama-server model status contains an invalid entry.");
            }
            if (!string.Equals(id.GetString(), modelAlias, StringComparison.Ordinal))
                continue;
            if (match is not null)
                throw new InvalidDataException("The llama-server model status contains duplicate aliases.");
            match = model;
        }

        if (match is null)
            return null;

        JsonElement selected = match.Value;
        if (!selected.TryGetProperty("status", out JsonElement statusElement) ||
            statusElement.ValueKind != JsonValueKind.Object ||
            !statusElement.TryGetProperty("value", out JsonElement valueElement) ||
            valueElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(valueElement.GetString()))
        {
            throw new InvalidDataException("The llama-server model status does not contain a valid state.");
        }

        string? topLevelPath = ReadOptionalTopLevelPath(selected);
        string? argumentPath = ReadOptionalModelArgument(statusElement);
        string reportedPath = topLevelPath ?? argumentPath
            ?? throw new InvalidDataException("The llama-server model status does not identify the managed model path.");
        if (!PathsEqual(reportedPath, expectedModelPath) ||
            (topLevelPath is not null && argumentPath is not null && !PathsEqual(topLevelPath, argumentPath)))
        {
            throw new InvalidDataException("The llama-server model status does not match the managed model.");
        }

        string status = valueElement.GetString()!;
        LocalAiModelAvailabilityState state = status switch
        {
            "loaded" => LocalAiModelAvailabilityState.Loaded,
            "unloaded" or "loading" or "sleeping" => LocalAiModelAvailabilityState.Verified,
            _ => LocalAiModelAvailabilityState.Unknown,
        };
        return new(state, reportedPath, status);
    }

    private static string? ReadOptionalTopLevelPath(JsonElement selected)
    {
        if (!selected.TryGetProperty("path", out JsonElement path))
            return null;
        if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
            throw new InvalidDataException("The llama-server model path is invalid.");
        return path.GetString();
    }

    private static string? ReadOptionalModelArgument(JsonElement status)
    {
        if (!status.TryGetProperty("args", out JsonElement args))
            return null;
        if (args.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The llama-server model arguments are invalid.");

        string? modelPath = null;
        JsonElement[] values = args.EnumerateArray().ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index].ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The llama-server model arguments contain a non-string value.");
            string? value = values[index].GetString();
            if (value is not ("--model" or "-m"))
                continue;
            if (modelPath is not null || index + 1 >= values.Length ||
                values[index + 1].ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(values[index + 1].GetString()))
            {
                throw new InvalidDataException("The llama-server model arguments contain an invalid model path.");
            }
            modelPath = values[++index].GetString();
        }
        return modelPath;
    }

    internal static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The llama-server reported an invalid model path.", ex);
        }
    }
}

internal interface ILlamaServerClient : IDisposable
{
    Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(
        Uri endpoint,
        string modelAlias,
        string expectedModelPath,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded, loopback-only health and model-state client for the managed llama-server router.</summary>
public sealed class LlamaServerClient : ILlamaServerClient
{
    private const int MaxEvidenceResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;

    public LlamaServerClient() : this(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    {
    }

    internal LlamaServerClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    public async Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(
        Uri endpoint,
        string modelAlias,
        string expectedModelPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModelPath);
        ValidateManagedEndpoint(endpoint);

        if (!await ProbeHealthAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            return new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The llama-server router health check did not succeed.");
        }

        try
        {
            return await ProbeModelAsync(endpoint, modelAlias, expectedModelPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, LocalAiModelAvailabilityState.Unknown, null, "The model status check timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            return new(false, LocalAiModelAvailabilityState.Unknown, null, "The model status response was invalid.");
        }
    }

    [Obsolete("Use ProbeManagedModelAsync instead.")]
    public Task<LlamaServerRouterProbeResult> ProbeRouterAsync(
        Uri endpoint,
        string modelAlias,
        string expectedModelPath,
        CancellationToken cancellationToken = default) =>
        ProbeManagedModelAsync(endpoint, modelAlias, expectedModelPath, cancellationToken);

    private async Task<bool> ProbeHealthAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(
                    BuildEndpointUri(endpoint, "/health"),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "ok", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private async Task<LlamaServerRouterProbeResult> ProbeModelAsync(
        Uri endpoint,
        string modelAlias,
        string expectedModelPath,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
                BuildEndpointUri(endpoint, "/models", "autoload=false"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server model status returned HTTP {(int)response.StatusCode}.");

        byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        LlamaServerModelStatusEvidence? evidence = LlamaServerModelStatusParser.Parse(
            document.RootElement,
            modelAlias,
            expectedModelPath);
        if (evidence is null)
            return new(false, LocalAiModelAvailabilityState.NotInstalled, null, "The configured model is not registered.");
        return new(
            evidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded,
            evidence.State,
            evidence.ModelPath,
            $"llama-server reports the model as {evidence.ServerStatus}.");
    }

    private static void ValidateManagedEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Port is <= 0 or > 65535 ||
            endpoint.Port == 80 ||
            !string.Equals(endpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The llama-server endpoint must use an explicit IPv4 loopback port.", nameof(endpoint));
        }
    }

    private static Uri BuildEndpointUri(Uri endpoint, string path, string? query = null) =>
        new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", endpoint.Port, path)
        {
            Query = query ?? string.Empty,
        }.Uri;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxEvidenceResponseBytes)
            throw new InvalidDataException("The llama-server evidence response exceeds the size limit.");

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaxEvidenceResponseBytes)
                throw new InvalidDataException("The llama-server evidence response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
    }

    public void Dispose() => _client.Dispose();
}
