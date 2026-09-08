using System.Text.Json;

namespace OpenClaw.Shared;

public enum PluginInstallSource
{
    ClawHub,
    Official,
}

public sealed class PluginCatalogInstallAction
{
    public string Source { get; set; } = string.Empty;
    public string? PackageName { get; set; }
    public string? PluginId { get; set; }
}

public sealed class PluginCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PackageName { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }
    public IReadOnlyList<string> Kind { get; set; } = [];
    public string? Origin { get; set; }
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
    public string State { get; set; } = string.Empty;
    public bool Featured { get; set; }
    public long? FeaturedAt { get; set; }
    public double? Order { get; set; }
    public bool HasIcon { get; set; }
    public PluginCatalogInstallAction? Install { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public bool Removable { get; set; }
}

public sealed class PluginsListResult
{
    public IReadOnlyList<PluginCatalogEntry> Plugins { get; set; } = [];
    public int DiagnosticCount { get; set; }
    public bool MutationAllowed { get; set; }
    public bool IsSupported { get; set; } = true;

    public static PluginsListResult Unsupported { get; } = new() { IsSupported = false };
}

internal sealed class PluginsListWireResult
{
    public IReadOnlyList<PluginCatalogEntry> Plugins { get; set; } = [];
    public IReadOnlyList<JsonElement> Diagnostics { get; set; } = [];
    public bool MutationAllowed { get; set; }
}

public sealed class PluginInspectSource
{
    public string Kind { get; set; } = string.Empty;
    public string? Spec { get; set; }
    public string? PackageName { get; set; }
    public string? Integrity { get; set; }
    public string? IntegrityKind { get; set; }
}

public sealed class PluginDeclaredSurface
{
    public IReadOnlyList<string> Channels { get; set; } = [];
    public IReadOnlyList<string> Providers { get; set; } = [];
    public IReadOnlyList<string> Tools { get; set; } = [];
    public IReadOnlyList<string> Contracts { get; set; } = [];
    public IReadOnlyList<string> Hooks { get; set; } = [];
    public IReadOnlyList<string> McpServers { get; set; } = [];
    public IReadOnlyList<string> CliCommands { get; set; } = [];
    public IReadOnlyList<string> CliBackends { get; set; } = [];
    public IReadOnlyList<string> Skills { get; set; } = [];
    public IReadOnlyList<string> DangerousConfigFlags { get; set; } = [];

    public bool HasAny =>
        Channels.Count > 0 || Providers.Count > 0 || Tools.Count > 0 ||
        Contracts.Count > 0 || Hooks.Count > 0 || McpServers.Count > 0 ||
        CliCommands.Count > 0 || CliBackends.Count > 0 || Skills.Count > 0 ||
        DangerousConfigFlags.Count > 0;
}

public sealed class PluginHookGrant
{
    public bool Effective { get; set; }
    public bool? Configured { get; set; }
}

public sealed class PluginHookGrants
{
    public PluginHookGrant AllowPromptInjection { get; set; } = new();
    public PluginHookGrant AllowConversationAccess { get; set; } = new();
}

public sealed class PluginLlmGrants
{
    public bool? AllowModelOverride { get; set; }
    public IReadOnlyList<string> AllowedModels { get; set; } = [];
    public IReadOnlyList<string> AllowedCompletionModels { get; set; } = [];
    public bool? AllowAuthProfileOverride { get; set; }
    public bool? AllowAgentIdOverride { get; set; }
}

public sealed class PluginSubagentGrants
{
    public bool? AllowModelOverride { get; set; }
    public IReadOnlyList<string> AllowedModels { get; set; } = [];
}

public sealed class PluginOperatorGrants
{
    public PluginHookGrants Hooks { get; set; } = new();
    public PluginLlmGrants? Llm { get; set; }
    public PluginSubagentGrants? Subagent { get; set; }
}

public sealed class PluginInstallTrust
{
    public string Disposition { get; set; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public string? CheckedAt { get; set; }
    public string? AcknowledgedAt { get; set; }
    public bool Pending { get; set; }
    public bool Stale { get; set; }
}

public sealed class PluginInspectEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Origin { get; set; }
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
}

public sealed class PluginInspectResult
{
    public bool Ok { get; set; }
    public PluginInspectEntry Plugin { get; set; } = new();
    public PluginInspectSource? Source { get; set; }
    public PluginDeclaredSurface Declared { get; set; } = new();
    public string ReviewToken { get; set; } = string.Empty;
    public PluginOperatorGrants Grants { get; set; } = new();
    public PluginInstallTrust? Trust { get; set; }
    public bool IsSupported { get; set; } = true;

    public static PluginInspectResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class PluginSearchPackage
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public bool IsOfficial { get; set; }
    public string? Summary { get; set; }
    public string? LatestVersion { get; set; }
    public string? RuntimeId { get; set; }
    public double? Downloads { get; set; }
    public string? VerificationTier { get; set; }
}

public sealed class PluginSearchEntry
{
    public double Score { get; set; }
    public PluginSearchPackage Package { get; set; } = new();
}

public sealed class PluginsSearchResult
{
    public IReadOnlyList<PluginSearchEntry> Results { get; set; } = [];
    public bool IsSupported { get; set; } = true;

    public static PluginsSearchResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed record PluginCapabilityAcknowledgement(
    string ReviewToken,
    long ConnectionEpoch);

public sealed record PluginInstallRequest
{
    private PluginInstallRequest() { }

    public PluginInstallSource Source { get; private init; }
    public string? PackageName { get; private init; }
    public string? PluginId { get; private init; }
    public string? Version { get; init; }
    public bool AcknowledgeInstallPolicyWarning { get; init; }
    public PluginCapabilityAcknowledgement? AcknowledgeCapabilities { get; init; }

    public static PluginInstallRequest FromClawHub(string packageName) =>
        new() { Source = PluginInstallSource.ClawHub, PackageName = packageName };

    public static PluginInstallRequest FromOfficialCatalog(string pluginId) =>
        new() { Source = PluginInstallSource.Official, PluginId = pluginId };
}

public sealed record PluginSetEnabledRequest(
    string PluginId,
    bool Enabled,
    PluginCapabilityAcknowledgement? AcknowledgeCapabilities = null);

public sealed class PluginMutationResult
{
    public bool Ok { get; set; }
    public PluginCatalogEntry? Plugin { get; set; }
    public string? PluginId { get; set; }
    public bool RestartRequired { get; set; }
    public IReadOnlyList<string> Removed { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public bool IsSupported { get; set; } = true;

    public static PluginMutationResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class PluginCapabilityConsentDetails
{
    public const string RequiredCode = "PLUGIN_CAPABILITY_CONSENT_REQUIRED";

    public string PluginId { get; private init; } = string.Empty;
    public string ReviewToken { get; private init; } = string.Empty;
    public PluginDeclaredSurface Widened { get; private init; } = new();
    public string? AcceptedAt { get; private init; }

    public static bool TryParse(
        GatewayRequestException exception,
        out PluginCapabilityConsentDetails? consent)
    {
        consent = null;
        if (exception.Details is not { ValueKind: JsonValueKind.Object } details ||
            !HasOnlyProperties(details, "capabilityConsentCode", "pluginId", "reviewToken", "widened", "acceptedAt") ||
            !TryReadRequiredString(details, "capabilityConsentCode", out var code) ||
            !string.Equals(code, RequiredCode, StringComparison.Ordinal) ||
            !TryReadRequiredString(details, "pluginId", out var pluginId) ||
            !TryReadRequiredString(details, "reviewToken", out var reviewToken))
        {
            return false;
        }

        var widened = new PluginDeclaredSurface();
        if (details.TryGetProperty("widened", out var widenedElement))
        {
            if (widenedElement.ValueKind != JsonValueKind.Object ||
                !HasOnlyProperties(
                    widenedElement,
                    "channels", "providers", "tools", "contracts", "hooks",
                    "mcpServers", "cliCommands", "cliBackends", "skills",
                    "dangerousConfigFlags"))
            {
                return false;
            }

            try
            {
                widened = JsonSerializer.Deserialize<PluginDeclaredSurface>(
                    widenedElement,
                    JsonSerializerOptionsCache.GatewayProtocol) ?? new PluginDeclaredSurface();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        string? acceptedAt = null;
        if (details.TryGetProperty("acceptedAt", out var acceptedAtElement))
        {
            if (acceptedAtElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(acceptedAtElement.GetString()))
            {
                return false;
            }
            acceptedAt = acceptedAtElement.GetString();
        }

        consent = new PluginCapabilityConsentDetails
        {
            PluginId = pluginId!,
            ReviewToken = reviewToken!,
            Widened = widened,
            AcceptedAt = acceptedAt,
        };
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement parent,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }
        value = element.GetString();
        return true;
    }

    private static bool HasOnlyProperties(JsonElement element, params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        return element.EnumerateObject().All(property => allowed.Contains(property.Name));
    }
}

public sealed class InstallPolicyFinding
{
    public string RuleId { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? Line { get; set; }
}

public sealed class InstallPolicyWarningDetails
{
    public const string AcknowledgementRequiredCode =
        "install_policy_warning_acknowledgement_required";

    public string TargetName { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string RequestMode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public IReadOnlyList<InstallPolicyFinding> Findings { get; set; } = [];

    public static bool TryParse(
        GatewayRequestException exception,
        out InstallPolicyWarningDetails? warning)
    {
        warning = null;
        if (exception.Details is not { ValueKind: JsonValueKind.Object } details ||
            !details.TryGetProperty("installPolicyCode", out var code) ||
            code.ValueKind != JsonValueKind.String ||
            !string.Equals(
                code.GetString(),
                AcknowledgementRequiredCode,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<InstallPolicyWarningDetails>(
                details,
                JsonSerializerOptionsCache.GatewayProtocol);
            if (parsed is null ||
                string.IsNullOrWhiteSpace(parsed.TargetName) ||
                string.IsNullOrWhiteSpace(parsed.Reason) ||
                (parsed.TargetType != "skill" && parsed.TargetType != "plugin") ||
                (parsed.RequestMode != "install" && parsed.RequestMode != "update"))
            {
                return false;
            }
            warning = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
