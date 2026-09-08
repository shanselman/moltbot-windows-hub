using System.Text.Json;

namespace OpenClaw.Shared;

internal sealed class PluginsGatewayApi : GatewayExtensionApi
{
    private readonly Func<long> _getConnectionEpoch;

    internal PluginsGatewayApi(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        Action<string> ensureMethodSupported,
        Func<long> getConnectionEpoch)
        : base(sendRequest, ensureMethodSupported)
    {
        _getConnectionEpoch = getConnectionEpoch;
    }

    internal async Task<PluginsListResult> ListAsync(int timeoutMs)
    {
        var wire = await SendAsync<PluginsListWireResult>(
            "plugins.list", new { }, timeoutMs).ConfigureAwait(false);
        return new PluginsListResult
        {
            Plugins = wire.Plugins,
            DiagnosticCount = wire.Diagnostics.Count,
            MutationAllowed = wire.MutationAllowed,
        };
    }

    internal Task<PluginsSearchResult> SearchAsync(string query, int limit, int timeoutMs)
    {
        RequireNonEmpty(query, nameof(query));
        ValidateLimit(limit);
        return SendAsync<PluginsSearchResult>(
            "plugins.search", new { query, limit }, timeoutMs);
    }

    internal Task<PluginInspectResult> InspectAsync(string pluginId, int timeoutMs)
    {
        RequireNonEmpty(pluginId, nameof(pluginId));
        return SendAsync<PluginInspectResult>(
            "plugins.inspect", new { pluginId }, timeoutMs);
    }

    internal Task<PluginMutationResult> InstallAsync(
        PluginInstallRequest request,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAcknowledgement(request.AcknowledgeCapabilities);
        var parameters = new Dictionary<string, object?>();
        switch (request.Source)
        {
            case PluginInstallSource.ClawHub:
                RequireNonEmpty(request.PackageName, nameof(request.PackageName));
                parameters["source"] = "clawhub";
                parameters["packageName"] = request.PackageName;
                AddOptionalString(parameters, "version", request.Version);
                break;
            case PluginInstallSource.Official:
                RequireNonEmpty(request.PluginId, nameof(request.PluginId));
                parameters["source"] = "official";
                parameters["pluginId"] = request.PluginId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), "Unknown plugin install source.");
        }

        if (request.AcknowledgeInstallPolicyWarning)
            parameters["acknowledgeInstallPolicyWarning"] = true;
        AddAcknowledgement(parameters, request.AcknowledgeCapabilities);
        return SendAsync<PluginMutationResult>("plugins.install", parameters, timeoutMs);
    }

    internal Task<PluginMutationResult> SetEnabledAsync(
        PluginSetEnabledRequest request,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNonEmpty(request.PluginId, nameof(request.PluginId));
        ValidateAcknowledgement(request.AcknowledgeCapabilities);
        var parameters = new Dictionary<string, object?>
        {
            ["pluginId"] = request.PluginId,
            ["enabled"] = request.Enabled,
        };
        AddAcknowledgement(parameters, request.AcknowledgeCapabilities);
        return SendAsync<PluginMutationResult>("plugins.setEnabled", parameters, timeoutMs);
    }

    internal Task<PluginMutationResult> UninstallAsync(string pluginId, int timeoutMs)
    {
        RequireNonEmpty(pluginId, nameof(pluginId));
        return SendAsync<PluginMutationResult>(
            "plugins.uninstall", new { pluginId }, timeoutMs);
    }

    private void ValidateAcknowledgement(PluginCapabilityAcknowledgement? acknowledgement)
    {
        if (acknowledgement is null)
            return;
        RequireNonEmpty(acknowledgement.ReviewToken, nameof(acknowledgement.ReviewToken));
        if (acknowledgement.ConnectionEpoch != _getConnectionEpoch())
        {
            throw new InvalidOperationException(
                "Plugin capability review expired after the Gateway connection changed.");
        }
    }

    private static void AddAcknowledgement(
        IDictionary<string, object?> parameters,
        PluginCapabilityAcknowledgement? acknowledgement)
    {
        if (acknowledgement is not null)
            parameters["acknowledgeCapabilities"] = new { reviewToken = acknowledgement.ReviewToken };
    }
}
