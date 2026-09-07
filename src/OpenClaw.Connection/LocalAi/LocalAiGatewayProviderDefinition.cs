using OpenClaw.Shared.Inference.Catalog;
using System.Text.Json;

namespace OpenClaw.Connection.LocalAi;

/// <summary>Canonical gateway configuration for the companion-owned llama.cpp provider.</summary>
public static class LocalAiGatewayProviderDefinition
{
    private const string ApiType = "openai-completions";
    public const string CliRedactedApiKey = "__OPENCLAW_REDACTED__";
    public const string ProviderPath = "models.providers.llamacpp";
    public const string PrimaryModelPath = "agents.defaults.model.primary";
    public const int ProviderTimeoutSeconds = 300;
    public const int MaximumOutputTokens = 8_192;

    public static string BuildProviderJson(LocalAiResolvedInstall install)
    {
        return BuildProviderJson(install, "llama-local");
    }

    /// <summary>
    /// Compares a provider returned by <c>openclaw config get --json</c> with
    /// the managed definition. The CLI intentionally redacts secret values,
    /// so the API key may be either its written value or the documented
    /// redaction marker; every routing and model field must still match.
    /// </summary>
    public static bool MatchesProviderJson(string providerJson, LocalAiResolvedInstall install)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerJson);
        ArgumentNullException.ThrowIfNull(install);

        try
        {
            using JsonDocument actual = JsonDocument.Parse(providerJson);
            using JsonDocument expected = JsonDocument.Parse(BuildProviderJson(install));
            if (JsonEquals(actual.RootElement, expected.RootElement))
                return true;

            using JsonDocument redacted = JsonDocument.Parse(
                BuildProviderJson(install, CliRedactedApiKey));
            return JsonEquals(actual.RootElement, redacted.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        if (left.ValueKind == JsonValueKind.Object)
        {
            JsonProperty[] leftProperties = [.. left.EnumerateObject()];
            JsonProperty[] rightProperties = [.. right.EnumerateObject()];
            if (leftProperties.Length != rightProperties.Length)
                return false;
            foreach (JsonProperty property in leftProperties)
            {
                if (!right.TryGetProperty(property.Name, out JsonElement rightValue) ||
                    !JsonEquals(property.Value, rightValue))
                {
                    return false;
                }
            }
            return true;
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
            JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
            while (leftItems.MoveNext())
            {
                if (!rightItems.MoveNext() || !JsonEquals(leftItems.Current, rightItems.Current))
                    return false;
            }
            return !rightItems.MoveNext();
        }

        return JsonElement.DeepEquals(left, right);
    }

    private static string BuildProviderJson(LocalAiResolvedInstall install, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(install);
        LocalModelInfo model = GetQualifiedModel(install);
        Uri endpoint = install.Endpoint
            ?? throw new InvalidOperationException("The verified Local AI endpoint is required.");

        var value = new
        {
            baseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
            api = ApiType,
            apiKey,
            timeoutSeconds = ProviderTimeoutSeconds,
            models = new[]
            {
                new
                {
                    id = install.Manifest.ModelAlias,
                    name = model.DisplayName,
                    reasoning = true,
                    input = new[] { "text" },
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = install.Manifest.ContextLength,
                    contextTokens = install.Manifest.ContextLength,
                    maxTokens = MaximumOutputTokens,
                    compat = new { supportsTools = true, supportsUsageInStreaming = true },
                    api = ApiType,
                },
            },
        };
        return JsonSerializer.Serialize(value);
    }

    public static string BuildPrimaryModel(LocalAiResolvedInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        _ = GetQualifiedModel(install);
        return $"llamacpp/{install.Manifest.ModelAlias}";
    }

    private static LocalModelInfo GetQualifiedModel(LocalAiResolvedInstall install)
    {
        LocalModelInfo model = LocalModelCatalog.FindInstalled(install.Manifest.ModelCatalogId)
            ?? throw new InvalidDataException("The managed Local AI model is no longer qualified.");
        if (!string.Equals(model.Id, install.Manifest.ModelAlias, StringComparison.Ordinal))
            throw new InvalidDataException("The managed Local AI model alias does not match the qualified catalog.");
        return model;
    }

    public static void ValidateFallbackModel(string? model)
        => LocalAiGatewayModelPolicy.ValidateFallbackModel(model);

    public static bool TryReadPrimaryModelJson(string json, out string? model)
    {
        model = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.String)
                return false;
            model = document.RootElement.GetString();
            ValidateFallbackModel(model);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            model = null;
            return false;
        }
    }

    public static string BuildProviderBatchJson(LocalAiResolvedInstall install)
    {
        using JsonDocument provider = JsonDocument.Parse(BuildProviderJson(install));
        return JsonSerializer.Serialize(new[]
        {
            new { path = ProviderPath, value = (object)provider.RootElement.Clone() },
            new { path = PrimaryModelPath, value = (object)BuildPrimaryModel(install) },
        });
    }
}
