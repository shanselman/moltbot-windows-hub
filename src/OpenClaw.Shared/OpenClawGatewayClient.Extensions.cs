using System.Text.Json;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient
{
    private GatewayFeatureSet _advertisedFeatures = GatewayFeatureSet.Empty;
    private readonly SkillsGatewayApi _skillsGatewayApi;
    private readonly PluginsGatewayApi _pluginsGatewayApi;

    public GatewayFeatureSet AdvertisedFeatures => Volatile.Read(ref _advertisedFeatures);
    public long ConnectionEpoch => ConnectionGeneration;

    public Task<SkillsStatusReport> GetSkillsStatusAsync(
        string? agentId = null,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.GetStatusAsync(agentId, timeoutMs);

    public Task<SkillsSearchResult> SearchSkillsAsync(
        string? query = null,
        int limit = 20,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.SearchAsync(query, limit, timeoutMs);

    public Task<SkillsDetailResult> GetSkillDetailAsync(
        string installReference,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.GetDetailAsync(installReference, timeoutMs);

    public Task<SkillsSecurityVerdictsResult> GetSkillSecurityVerdictsAsync(
        string? agentId = null,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.GetSecurityVerdictsAsync(agentId, timeoutMs);

    public Task<SkillCardResult> GetSkillCardAsync(
        string skillKey,
        string? agentId = null,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.GetCardAsync(skillKey, agentId, timeoutMs);

    public Task<SkillMutationResult> InstallClawHubSkillAsync(
        ClawHubSkillInstallRequest request,
        int timeoutMs = 120000) =>
        _skillsGatewayApi.InstallAsync(request, timeoutMs);

    public Task<SkillMutationResult> UpdateClawHubSkillAsync(
        ClawHubSkillUpdateRequest request,
        int timeoutMs = 120000) =>
        _skillsGatewayApi.UpdateAsync(request, timeoutMs);

    public Task<SkillMutationResult> SetSkillEnabledDetailedAsync(
        string skillKey,
        bool enabled,
        int timeoutMs = 15000) =>
        _skillsGatewayApi.SetEnabledAsync(skillKey, enabled, timeoutMs);

    public Task<PluginsListResult> ListPluginsAsync(int timeoutMs = 15000) =>
        _pluginsGatewayApi.ListAsync(timeoutMs);

    public Task<PluginsSearchResult> SearchPluginsAsync(
        string query,
        int limit = 20,
        int timeoutMs = 15000) =>
        _pluginsGatewayApi.SearchAsync(query, limit, timeoutMs);

    public Task<PluginInspectResult> InspectPluginAsync(
        string pluginId,
        int timeoutMs = 15000) =>
        _pluginsGatewayApi.InspectAsync(pluginId, timeoutMs);

    public Task<PluginMutationResult> InstallPluginAsync(
        PluginInstallRequest request,
        int timeoutMs = 120000) =>
        _pluginsGatewayApi.InstallAsync(request, timeoutMs);

    public Task<PluginMutationResult> SetPluginEnabledAsync(
        PluginSetEnabledRequest request,
        int timeoutMs = 30000) =>
        _pluginsGatewayApi.SetEnabledAsync(request, timeoutMs);

    public Task<PluginMutationResult> UninstallPluginAsync(
        string pluginId,
        int timeoutMs = 120000) =>
        _pluginsGatewayApi.UninstallAsync(pluginId, timeoutMs);

    private void CaptureAdvertisedFeatures(JsonElement helloOk) =>
        Volatile.Write(ref _advertisedFeatures, GatewayFeatureSet.FromHelloOk(helloOk));

    private void ResetAdvertisedFeatures() =>
        Volatile.Write(ref _advertisedFeatures, GatewayFeatureSet.Empty);

    private void EnsureExtensionMethodSupported(string method)
    {
        if (!HasHandshakeSnapshot)
            throw new InvalidOperationException("Gateway handshake is not ready");
        if (!AdvertisedFeatures.SupportsMethod(method))
            throw new NotSupportedException($"The connected Gateway does not advertise {method}.");
    }
}
