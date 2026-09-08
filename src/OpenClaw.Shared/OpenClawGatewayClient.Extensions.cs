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
        SupportsExtensionMethod("skills.status")
            ? _skillsGatewayApi.GetStatusAsync(agentId, timeoutMs)
            : Task.FromResult(SkillsStatusReport.Unsupported);

    public Task<SkillsSearchResult> SearchSkillsAsync(
        string? query = null,
        int limit = 20,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("skills.search")
            ? _skillsGatewayApi.SearchAsync(query, limit, timeoutMs)
            : Task.FromResult(SkillsSearchResult.Unsupported);

    public Task<SkillsDetailResult> GetSkillDetailAsync(
        string installReference,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("skills.detail")
            ? _skillsGatewayApi.GetDetailAsync(installReference, timeoutMs)
            : Task.FromResult(SkillsDetailResult.Unsupported);

    public Task<SkillsSecurityVerdictsResult> GetSkillSecurityVerdictsAsync(
        string? agentId = null,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("skills.securityVerdicts")
            ? _skillsGatewayApi.GetSecurityVerdictsAsync(agentId, timeoutMs)
            : Task.FromResult(SkillsSecurityVerdictsResult.Unsupported);

    public Task<SkillCardResult> GetSkillCardAsync(
        string skillKey,
        string? agentId = null,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("skills.skillCard")
            ? _skillsGatewayApi.GetCardAsync(skillKey, agentId, timeoutMs)
            : Task.FromResult(SkillCardResult.Unsupported);

    public Task<SkillMutationResult> InstallClawHubSkillAsync(
        ClawHubSkillInstallRequest request,
        int timeoutMs = 120000) =>
        SupportsExtensionMethod("skills.install")
            ? _skillsGatewayApi.InstallAsync(request, timeoutMs)
            : Task.FromResult(SkillMutationResult.Unsupported);

    public Task<SkillMutationResult> UpdateClawHubSkillAsync(
        ClawHubSkillUpdateRequest request,
        int timeoutMs = 120000) =>
        SupportsExtensionMethod("skills.update")
            ? _skillsGatewayApi.UpdateAsync(request, timeoutMs)
            : Task.FromResult(SkillMutationResult.Unsupported);

    public Task<SkillMutationResult> SetSkillEnabledDetailedAsync(
        string skillKey,
        bool enabled,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("skills.update")
            ? _skillsGatewayApi.SetEnabledAsync(skillKey, enabled, timeoutMs)
            : Task.FromResult(SkillMutationResult.Unsupported);

    public Task<PluginsListResult> ListPluginsAsync(int timeoutMs = 15000) =>
        SupportsExtensionMethod("plugins.list")
            ? _pluginsGatewayApi.ListAsync(timeoutMs)
            : Task.FromResult(PluginsListResult.Unsupported);

    public Task<PluginsSearchResult> SearchPluginsAsync(
        string query,
        int limit = 20,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("plugins.search")
            ? _pluginsGatewayApi.SearchAsync(query, limit, timeoutMs)
            : Task.FromResult(PluginsSearchResult.Unsupported);

    public Task<PluginInspectResult> InspectPluginAsync(
        string pluginId,
        int timeoutMs = 15000) =>
        SupportsExtensionMethod("plugins.inspect")
            ? _pluginsGatewayApi.InspectAsync(pluginId, timeoutMs)
            : Task.FromResult(PluginInspectResult.Unsupported);

    public Task<PluginMutationResult> InstallPluginAsync(
        PluginInstallRequest request,
        int timeoutMs = 120000) =>
        SupportsExtensionMethod("plugins.install")
            ? _pluginsGatewayApi.InstallAsync(request, timeoutMs)
            : Task.FromResult(PluginMutationResult.Unsupported);

    public Task<PluginMutationResult> SetPluginEnabledAsync(
        PluginSetEnabledRequest request,
        int timeoutMs = 30000) =>
        SupportsExtensionMethod("plugins.setEnabled")
            ? _pluginsGatewayApi.SetEnabledAsync(request, timeoutMs)
            : Task.FromResult(PluginMutationResult.Unsupported);

    public Task<PluginMutationResult> UninstallPluginAsync(
        string pluginId,
        int timeoutMs = 120000) =>
        SupportsExtensionMethod("plugins.uninstall")
            ? _pluginsGatewayApi.UninstallAsync(pluginId, timeoutMs)
            : Task.FromResult(PluginMutationResult.Unsupported);

    private void CaptureAdvertisedFeatures(JsonElement helloOk) =>
        Volatile.Write(ref _advertisedFeatures, GatewayFeatureSet.FromHelloOk(helloOk));

    private void ResetAdvertisedFeatures() =>
        Volatile.Write(ref _advertisedFeatures, GatewayFeatureSet.Empty);

    private bool SupportsExtensionMethod(string method)
    {
        if (!HasHandshakeSnapshot)
            throw new InvalidOperationException("Gateway handshake is not ready");
        return AdvertisedFeatures.SupportsMethod(method);
    }

    private void EnsureExtensionMethodSupported(string method)
    {
        if (!HasHandshakeSnapshot)
            throw new InvalidOperationException("Gateway handshake is not ready");
        if (!AdvertisedFeatures.SupportsMethod(method))
            throw new NotSupportedException($"The connected Gateway does not advertise {method}.");
    }
}
