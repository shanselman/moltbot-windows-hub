using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// Read-only facade for the operator gateway client.
/// Exposes data events and request methods needed by UI consumers
/// without exposing connection lifecycle methods (connect/disconnect/dispose).
/// </summary>
public interface IOperatorGatewayClient
{
    // ─── Data Events ───
    event EventHandler<OpenClawNotification>? NotificationReceived;
    event EventHandler<AgentActivity>? ActivityChanged;
    event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    event EventHandler<SessionInfo[]>? SessionsUpdated;
    event EventHandler<GatewayUsageInfo>? UsageUpdated;
    event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
    event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
    event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    event EventHandler<JsonElement>? CronListUpdated;
    event EventHandler<JsonElement>? CronStatusUpdated;
    event EventHandler<JsonElement>? CronRunsUpdated;
    event EventHandler<JsonElement>? SkillsStatusUpdated;
    event EventHandler<JsonElement>? ConfigUpdated;
    event EventHandler<JsonElement>? ConfigSchemaUpdated;
    event EventHandler<AgentEventInfo>? AgentEventReceived;
    event EventHandler<PairingListInfo>? NodePairListUpdated;
    event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    event EventHandler<ModelsListInfo>? ModelsListUpdated;
    event EventHandler<PresenceEntry[]>? PresenceUpdated;
    event EventHandler<JsonElement>? AgentsListUpdated;
    event EventHandler<JsonElement>? AgentFilesListUpdated;
    event EventHandler<JsonElement>? AgentFileContentUpdated;
    event EventHandler<AgentEventInfo>? ChatEventReceived;

    // ─── Query ───
    string? OperatorDeviceId { get; }
    IReadOnlyList<string> GrantedOperatorScopes { get; }
    bool IsConnectedToGateway { get; }
    /// <summary>Canonical main session key resolved from hello-ok; <c>null</c> until handshake.</summary>
    string? MainSessionKey { get; }
    /// <summary>True once the hello-ok handshake has been processed.</summary>
    bool HasHandshakeSnapshot { get; }
    /// <summary>Gateway RPC methods and events advertised by the current hello-ok handshake.</summary>
    GatewayFeatureSet AdvertisedFeatures => GatewayFeatureSet.Empty;
    /// <summary>
    /// Monotonic transport generation used to invalidate artifact-bound consent
    /// after a disconnect or reconnect.
    /// </summary>
    long ConnectionEpoch => 0;

    // ─── Connection events (from WebSocketClientBase) ───
    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<string>? AuthenticationFailed;
    event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    event EventHandler? HandshakeSucceeded;

    // ─── Configuration ───
    void SetUserRules(IReadOnlyList<UserNotificationRule>? rules);
    void SetPreferStructuredCategories(bool value);

    // ─── Request Methods ───
    Task SendChatMessageAsync(string message, string? sessionKey = null);
    Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null);
    /// <summary>
    /// Fetches the normalized conversation transcript for a session
    /// (<c>chat.history</c>). Ships with a default so adding it does not
    /// source-break external implementers (test doubles); the real client
    /// overrides it. Non-overriding clients fail explicitly instead of looking
    /// like an empty transcript.
    /// </summary>
    Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey = null, int timeoutMs = 15000)
        => Task.FromException<ChatHistoryInfo>(new NotSupportedException("chat.history is not supported by this gateway client."));
    Task CheckHealthAsync();
    Task RequestSessionsAsync(string? agentId = null);
    Task RequestUsageAsync();
    Task RequestNodesAsync();
    Task RequestUsageStatusAsync();
    Task RequestUsageCostAsync(int days = 30);
    Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240);
    Task<bool> PatchSessionAsync(string key, string? model = null, string? thinkingLevel = null, string? verboseLevel = null);
    Task<bool> ResetSessionAsync(string key);
    Task<SessionResetResult> ResetSessionDetailedAsync(string key, int timeoutMs = 15000) =>
        Task.FromResult(new SessionResetResult
        {
            Ok = false,
            Error = "Response-aware sessions.reset is not supported by this gateway client."
        });
    Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true);
    Task<bool> CompactSessionAsync(string key, int maxLines = 400);
    Task<SessionCompactResult> CompactSessionDetailedAsync(string key, int timeoutMs = 120000) =>
        Task.FromResult(new SessionCompactResult
        {
            Ok = false,
            Error = "Response-aware sessions.compact is not supported by this gateway client."
        });
    Task RequestCronListAsync();
    Task RequestCronStatusAsync();
    Task<bool> RunCronJobAsync(string jobId, bool force = true);
    /// <summary>
    /// Response-aware cron run request. Compatibility fallback for implementers
    /// that only provide <see cref="RunCronJobAsync"/>; the fallback cannot
    /// enforce <paramref name="timeoutMs"/> and implementers should not implement
    /// <see cref="RunCronJobAsync"/> by delegating back to this method.
    /// </summary>
    async Task<CronRunRequestResult> RunCronJobDetailedAsync(string jobId, bool force = true, int timeoutMs = 12000)
    {
        var accepted = await RunCronJobAsync(jobId, force).ConfigureAwait(false);
        return accepted
            ? new CronRunRequestResult(true, true, null)
            : CronRunRequestResult.NotAccepted(null);
    }
    Task<bool> RemoveCronJobAsync(string jobId);
    Task<bool> AddCronJobAsync(object jobDefinition);
    Task<bool> UpdateCronJobAsync(string id, object patch);
    Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0);
    Task RequestSkillsStatusAsync(string? agentId = null);
    Task<SkillsStatusReport> GetSkillsStatusAsync(string? agentId = null, int timeoutMs = 15000)
        => Task.FromResult(SkillsStatusReport.Unsupported);
    Task<SkillsSearchResult> SearchSkillsAsync(string? query = null, int limit = 20, int timeoutMs = 15000)
        => Task.FromResult(SkillsSearchResult.Unsupported);
    Task<SkillsDetailResult> GetSkillDetailAsync(string installReference, int timeoutMs = 15000)
        => Task.FromResult(SkillsDetailResult.Unsupported);
    Task<SkillsSecurityVerdictsResult> GetSkillSecurityVerdictsAsync(string? agentId = null, int timeoutMs = 15000)
        => Task.FromResult(SkillsSecurityVerdictsResult.Unsupported);
    Task<SkillCardResult> GetSkillCardAsync(string skillKey, string? agentId = null, int timeoutMs = 15000)
        => Task.FromResult(SkillCardResult.Unsupported);
    Task<SkillMutationResult> InstallClawHubSkillAsync(ClawHubSkillInstallRequest request, int timeoutMs = 120000)
        => Task.FromResult(SkillMutationResult.Unsupported);
    Task<SkillMutationResult> UpdateClawHubSkillAsync(ClawHubSkillUpdateRequest request, int timeoutMs = 120000)
        => Task.FromResult(SkillMutationResult.Unsupported);
    Task<SkillMutationResult> SetSkillEnabledDetailedAsync(string skillKey, bool enabled, int timeoutMs = 15000)
        => Task.FromResult(SkillMutationResult.Unsupported);
    [Obsolete("Use InstallClawHubSkillAsync with the exact skills.search installRef.")]
    Task<bool> InstallSkillAsync(string skillId);
    Task<bool> SetSkillEnabledAsync(string skillKey, bool enabled);
    Task<PluginsListResult> ListPluginsAsync(int timeoutMs = 15000)
        => Task.FromResult(PluginsListResult.Unsupported);
    Task<PluginsSearchResult> SearchPluginsAsync(string query, int limit = 20, int timeoutMs = 15000)
        => Task.FromResult(PluginsSearchResult.Unsupported);
    Task<PluginInspectResult> InspectPluginAsync(string pluginId, int timeoutMs = 15000)
        => Task.FromResult(PluginInspectResult.Unsupported);
    Task<PluginMutationResult> InstallPluginAsync(PluginInstallRequest request, int timeoutMs = 120000)
        => Task.FromResult(PluginMutationResult.Unsupported);
    Task<PluginMutationResult> SetPluginEnabledAsync(PluginSetEnabledRequest request, int timeoutMs = 30000)
        => Task.FromResult(PluginMutationResult.Unsupported);
    Task<PluginMutationResult> UninstallPluginAsync(string pluginId, int timeoutMs = 120000)
        => Task.FromResult(PluginMutationResult.Unsupported);
    Task RequestConfigAsync();
    Task RequestConfigSchemaAsync();
    Task<bool> SetConfigAsync(string path, object value);
    Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash);
    /// <summary>Response-aware variant of <see cref="PatchConfigAsync"/>: awaits the gateway's reply and returns the real error on failure.</summary>
    Task<ConfigPatchResult> PatchConfigDetailedAsync(JsonElement fullConfig, string? baseHash, int timeoutMs = 15000);
    Task RequestAgentsListAsync();
    Task RequestAgentFilesListAsync(string agentId = "main");
    Task RequestAgentFileGetAsync(string agentId, string name);
    Task RequestModelsListAsync();
    Task RequestNodePairListAsync();
    Task<bool> NodePairApproveAsync(string requestId);
    Task<bool> NodePairRejectAsync(string requestId);
    Task<NodeForgetResult> NodePairRemoveAsync(string nodeId);
    Task<NodeRenameResult> NodeRenameAsync(string nodeId, string displayName);
    Task RequestDevicePairListAsync();
    Task<bool> DevicePairApproveAsync(string requestId);
    Task<bool> DevicePairRejectAsync(string requestId);
    Task<bool> StartChannelAsync(string channelName);
    /// <summary>Start a channel and return the full gateway response so the page can detect "unknown channel" (plugin not loaded).</summary>
    Task<ChannelStartResult?> StartChannelDetailedAsync(string channelName, int timeoutMs = 12000);
    Task<bool> StopChannelAsync(string channelName);
    /// <summary>Fetch the rich channels.status snapshot from the gateway. Mac/web canonical wire method.</summary>
    Task<ChannelsStatusSnapshot?> GetChannelsStatusAsync(bool probe = false, int timeoutMs = 12000);
    /// <summary>Log out / unlink a channel (whatsapp, telegram). Sends channels.logout { channel }.</summary>
    Task<bool> LogoutChannelAsync(string channelName, int timeoutMs = 12000);
    /// <summary>Begin a QR linking flow (whatsapp, signal). Sends web.login.start { force, timeoutMs }.</summary>
    Task<WebLoginStartResult?> WebLoginStartAsync(bool force = false, int timeoutMs = 30000);
    /// <summary>Long-poll for QR linking completion. Sends web.login.wait { currentQrDataUrl, timeoutMs }.</summary>
    Task<WebLoginWaitResult?> WebLoginWaitAsync(string? currentQrDataUrl = null, int timeoutMs = 30000);
    Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000);

    // ─── Gateway protocol APIs ───
    // These ship with default implementations so adding them does not source-break
    // existing external implementers of this interface (e.g. test doubles). The
    // real gateway client overrides them; defaults degrade gracefully to an
    // "unsupported" typed result, matching older-gateway behavior.
    /// <summary>Fetch the gateway command catalog (<c>commands.list</c>); applies <paramref name="query"/> client-side. Returns IsSupported=false on older gateways.</summary>
    Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null, int timeoutMs = 15000)
        => Task.FromResult(new CommandCatalog { IsSupported = false });
    /// <summary>Apply an extended <see cref="SessionPatch"/> (rich field set) to a session.</summary>
    Task<bool> PatchSessionAsync(string key, SessionPatch patch)
        => Task.FromResult(false);
    /// <summary>List session files, optionally scoped to a sub-path/search (<c>sessions.files.list</c>).</summary>
    Task<SessionFileList> ListSessionFilesAsync(string key, string? path = null, string? search = null, int timeoutMs = 15000)
        => Task.FromResult(new SessionFileList { Key = key, IsSupported = false });
    /// <summary>Read a session file's content (<c>sessions.files.get</c>).</summary>
    Task<SessionFileContent> GetSessionFileAsync(string key, string path, int timeoutMs = 15000)
        => Task.FromResult(new SessionFileContent { Key = key, Path = path, IsSupported = false });
    /// <summary>List compaction checkpoints for a session (<c>sessions.compaction.list</c>).</summary>
    Task<SessionCompactionCheckpointList> ListCompactionCheckpointsAsync(string key, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionCheckpointList { Key = key, IsSupported = false });
    /// <summary>Fetch a single compaction checkpoint's metadata (<c>sessions.compaction.get</c>).</summary>
    Task<SessionCompactionCheckpointResult> GetCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionCheckpointResult { Key = key, IsSupported = false });
    /// <summary>Branch a new session from a compaction checkpoint (<c>sessions.compaction.branch</c>).</summary>
    Task<SessionCompactionMutationResult> BranchCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionMutationResult { Key = key, CheckpointId = checkpointId, IsSupported = false });
    /// <summary>Restore a session to a compaction checkpoint (<c>sessions.compaction.restore</c>).</summary>
    Task<SessionCompactionMutationResult> RestoreCompactionCheckpointAsync(string key, string checkpointId, int timeoutMs = 15000)
        => Task.FromResult(new SessionCompactionMutationResult { Key = key, CheckpointId = checkpointId, IsSupported = false });
    /// <summary>Create a distinct session through <c>sessions.create</c>.</summary>
    Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request, int timeoutMs = 15000)
        => Task.FromResult(new SessionCreateResult
        {
            Ok = false,
            IsSupported = false,
            Error = "sessions.create is not supported by this gateway client."
        });
}

public sealed record CronRunRequestResult(
    bool Accepted,
    bool Enqueued,
    string? RunId,
    string? Reason = null,
    string? Error = null)
{
    public static CronRunRequestResult NotAccepted(string? error) =>
        new(false, false, null, null, error);
}
