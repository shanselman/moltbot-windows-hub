using System.Text.Json;

namespace OpenClaw.Shared;

internal sealed class PluginsGatewayApi : GatewayExtensionApi
{
    private readonly Func<long> _getConnectionEpoch;
    private readonly Func<string, object?, int, long, Task<JsonElement>> _sendMutationRequest;

    internal PluginsGatewayApi(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        Action<string> ensureMethodSupported,
        Func<long> getConnectionEpoch,
        Func<string, object?, int, long, Task<JsonElement>> sendMutationRequest)
        : base(sendRequest, ensureMethodSupported)
    {
        _getConnectionEpoch = getConnectionEpoch;
        _sendMutationRequest = sendMutationRequest;
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
        var connectionEpoch = CaptureMutationEpoch(request.AcknowledgeCapabilities);
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
        return SendMutationAsync("plugins.install", parameters, timeoutMs, connectionEpoch);
    }

    internal Task<PluginMutationResult> SetEnabledAsync(
        PluginSetEnabledRequest request,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNonEmpty(request.PluginId, nameof(request.PluginId));
        var connectionEpoch = CaptureMutationEpoch(request.AcknowledgeCapabilities);
        var parameters = new Dictionary<string, object?>
        {
            ["pluginId"] = request.PluginId,
            ["enabled"] = request.Enabled,
        };
        AddAcknowledgement(parameters, request.AcknowledgeCapabilities);
        return SendMutationAsync("plugins.setEnabled", parameters, timeoutMs, connectionEpoch);
    }

    internal Task<PluginMutationResult> UninstallAsync(string pluginId, int timeoutMs)
    {
        RequireNonEmpty(pluginId, nameof(pluginId));
        return SendMutationAsync(
            "plugins.uninstall", new { pluginId }, timeoutMs, _getConnectionEpoch());
    }

    private long CaptureMutationEpoch(PluginCapabilityAcknowledgement? acknowledgement)
    {
        var connectionEpoch = _getConnectionEpoch();
        if (acknowledgement is null)
            return connectionEpoch;
        RequireNonEmpty(acknowledgement.ReviewToken, nameof(acknowledgement.ReviewToken));
        if (acknowledgement.ConnectionEpoch != connectionEpoch)
        {
            throw new InvalidOperationException(
                "Plugin capability review expired after the Gateway connection changed.");
        }
        return connectionEpoch;
    }

    private async Task<PluginMutationResult> SendMutationAsync(
        string method,
        object? parameters,
        int timeoutMs,
        long connectionEpoch)
    {
        EnsureMethodSupported(method);
        var payload = await _sendMutationRequest(
            method,
            parameters,
            timeoutMs,
            connectionEpoch).ConfigureAwait(false);
        return DeserializePayload<PluginMutationResult>(payload, method);
    }

    private static void AddAcknowledgement(
        IDictionary<string, object?> parameters,
        PluginCapabilityAcknowledgement? acknowledgement)
    {
        if (acknowledgement is not null)
            parameters["acknowledgeCapabilities"] = new { reviewToken = acknowledgement.ReviewToken };
    }
}
