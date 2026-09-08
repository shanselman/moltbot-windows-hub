using System.Text.Json;

namespace OpenClaw.Shared;

internal sealed class SkillsGatewayApi : GatewayExtensionApi
{
    private readonly Func<long> _getConnectionEpoch;
    private readonly Func<string, object?, int, long, Task<JsonElement>> _sendMutationRequest;

    internal SkillsGatewayApi(
        Func<string, object?, int, Task<JsonElement>> sendRequest,
        Action<string> ensureMethodSupported,
        Func<long> getConnectionEpoch,
        Func<string, object?, int, long, Task<JsonElement>> sendMutationRequest)
        : base(sendRequest, ensureMethodSupported)
    {
        _getConnectionEpoch = getConnectionEpoch;
        _sendMutationRequest = sendMutationRequest;
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
        var connectionEpoch = _getConnectionEpoch();
        if (request.ConnectionEpoch.HasValue && request.ConnectionEpoch.Value != connectionEpoch)
        {
            throw new InvalidOperationException(
                "Skill review expired after the Gateway connection changed.");
        }
        return SendInstallAsync(parameters, timeoutMs, connectionEpoch);
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

    private async Task<SkillMutationResult> SendInstallAsync(
        object? parameters,
        int timeoutMs,
        long connectionEpoch)
    {
        const string method = "skills.install";
        EnsureMethodSupported(method);
        var payload = await _sendMutationRequest(
            method,
            parameters,
            timeoutMs,
            connectionEpoch).ConfigureAwait(false);
        return DeserializePayload<SkillMutationResult>(payload, method);
    }
}
