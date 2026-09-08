using System.Text.Json;

namespace OpenClaw.Shared;

internal sealed class SkillsGatewayApi : GatewayExtensionApi
{
    internal SkillsGatewayApi(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        Action<string> ensureMethodSupported)
        : base(sendRequest, ensureMethodSupported)
    {
    }

    internal Task<SkillsStatusReport> GetStatusAsync(string? agentId, int timeoutMs) =>
        SendAsync<SkillsStatusReport>("skills.status", OptionalAgentParameters(agentId), timeoutMs);

    internal Task<SkillsSearchResult> SearchAsync(string? query, int limit, int timeoutMs)
    {
        ValidateLimit(limit);
        var parameters = new Dictionary<string, object?> { ["limit"] = limit };
        AddOptionalString(parameters, "query", query);
        return SendAsync<SkillsSearchResult>("skills.search", parameters, timeoutMs);
    }

    internal Task<SkillsDetailResult> GetDetailAsync(string installReference, int timeoutMs)
    {
        RequireNonEmpty(installReference, nameof(installReference));
        return SendAsync<SkillsDetailResult>(
            "skills.detail", new { slug = installReference }, timeoutMs);
    }

    internal Task<SkillsSecurityVerdictsResult> GetSecurityVerdictsAsync(
        string? agentId,
        int timeoutMs) =>
        SendAsync<SkillsSecurityVerdictsResult>(
            "skills.securityVerdicts", OptionalAgentParameters(agentId), timeoutMs);

    internal Task<SkillCardResult> GetCardAsync(
        string skillKey,
        string? agentId,
        int timeoutMs)
    {
        RequireNonEmpty(skillKey, nameof(skillKey));
        var parameters = OptionalAgentParameters(agentId);
        parameters["skillKey"] = skillKey;
        return SendAsync<SkillCardResult>("skills.skillCard", parameters, timeoutMs);
    }

    internal Task<SkillMutationResult> InstallAsync(
        ClawHubSkillInstallRequest request,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNonEmpty(request.InstallReference, nameof(request.InstallReference));
        var parameters = OptionalAgentParameters(request.AgentId);
        parameters["source"] = "clawhub";
        parameters["slug"] = request.InstallReference;
        AddOptionalString(parameters, "version", request.Version);
        if (request.TimeoutMs.HasValue)
            parameters["timeoutMs"] = request.TimeoutMs.Value;
        return SendAsync<SkillMutationResult>("skills.install", parameters, timeoutMs);
    }

    internal Task<SkillMutationResult> UpdateAsync(
        ClawHubSkillUpdateRequest request,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireNonEmpty(request.InstallReference, nameof(request.InstallReference));
        var parameters = OptionalAgentParameters(request.AgentId);
        parameters["source"] = "clawhub";
        parameters["slug"] = request.InstallReference;
        return SendAsync<SkillMutationResult>("skills.update", parameters, timeoutMs);
    }

    internal Task<SkillMutationResult> SetEnabledAsync(
        string skillKey,
        bool enabled,
        int timeoutMs)
    {
        RequireNonEmpty(skillKey, nameof(skillKey));
        return SendAsync<SkillMutationResult>(
            "skills.update", new { skillKey, enabled }, timeoutMs);
    }
}
