using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public partial class OpenClawGatewayClient : WebSocketClientBase, IOperatorGatewayClient
{
    private const string OperatorClientId = "cli";
    private const string OperatorClientMode = "cli";
    private const string OperatorRole = "operator";
    private static readonly Regex s_pairingRequestIdRegex = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.Compiled);
    private static readonly string[] s_operatorScopes =
    [
        "operator.admin",
        "operator.pairing",
    ];
    private static readonly string[] s_operatorBootstrapScopes =
    [
        "operator.approvals",
        "operator.read",
        "operator.talk.secrets",
        "operator.write"
    ];

    // Tracked state
    private readonly Dictionary<string, SessionInfo> _sessions = new();
    private GatewayUsageInfo? _usage;
    private GatewayUsageStatusInfo? _usageStatus;
    private GatewayCostUsageInfo? _usageCost;
    private readonly PendingRequestRegistry _pendingRequests = new();
    private readonly object _sessionsLock = new();
    private readonly DeviceIdentity _deviceIdentity;
    private IConnectEnvelopeSigner _connectEnvelopeSigner;
    private readonly string _currentGatewayUrl;
    private string? _mainSessionKey;
    private bool _mainSessionKeyIsCanonical;
    private bool _hasHandshakeSnapshot;

    /// <summary>
    /// The gateway's resolved main session key as published in the hello-ok
    /// snapshot (preferring canonical <c>sessionDefaults.mainSessionKey</c>,
    /// with <c>mainKey</c> retained for older gateways). <c>null</c> until
    /// handshake completes or after a disconnect. Callers should pass this value
    /// (or <c>null</c>) to <see cref="SendChatMessageForRunAsync"/>; never
    /// substitute a literal like <c>"main"</c>, which can drift from the
    /// canonical key the gateway echoes back in chat events.
    /// </summary>
    /// <remarks>
    /// Read via <see cref="Volatile.Read{T}(ref T)"/> because the field is
    /// written from the gateway WebSocket thread and read from the UI thread
    /// (through <see cref="OpenClawChatDataProvider"/>).
    /// </remarks>
    public string? MainSessionKey => Volatile.Read(ref _mainSessionKey);

    /// <summary>
    /// True once the hello-ok handshake has been processed (i.e. session
    /// defaults are known). Reset to false on disconnect. Surfaces to the UI
    /// as <see cref="OpenClaw.Chat.ChatComposeTarget.IsReady"/>.
    /// </summary>
    public bool HasHandshakeSnapshot => Volatile.Read(ref _hasHandshakeSnapshot);
    private string? _operatorDeviceId;
    private string[] _grantedOperatorScopes = Array.Empty<string>();
    private string _connectAuthToken;
    private string? _assistantMediaAuthToken;
    private bool _useV2Signature; // true after v3 signature rejected by gateway

    /// <summary>Set to true to skip v3 and use v2 signatures directly (for gateways that don't support v3).</summary>
    public bool UseV2Signature { get => _useV2Signature; set => _useV2Signature = value; }
    internal IConnectEnvelopeSigner ConnectEnvelopeSigner
    {
        get => _connectEnvelopeSigner;
        set => _connectEnvelopeSigner = value;
    }
    private long? _challengeTimestampMs;
    private string? _currentChallengeNonce;
    private bool _usageStatusUnsupported;
    private bool _usageCostUnsupported;
    private bool _sessionPreviewUnsupported;
    private bool _nodeListUnsupported;
    private bool _modelsListUnsupported;
    private bool _nodePairListUnsupported;
    private bool _devicePairListUnsupported;
    private bool _agentsListUnsupported;
    private bool _agentFilesListUnsupported;
    private bool _agentFileGetUnsupported;
    private bool _operatorReadScopeUnavailable;
    private bool _pairingRequiredAwaitingApproval;
    private string? _pairingRequiredRequestId;
    private bool _authFailed;
    private readonly HandshakeChallengeGate _handshakeChallengeGate = new();
    private bool _protocolMismatch;
    private string? _lastSkillsStatusAgentId;
    private readonly bool _tokenIsBootstrapToken;
    private readonly bool _bootstrapPairAsNode;
    private readonly bool _ignoreStoredDeviceToken;
    private readonly bool _persistHandshakeDeviceTokens;

    /// <summary>True when the gateway reported "pairing required" for this device.</summary>
    public bool IsPairingRequired => Volatile.Read(ref _pairingRequiredAwaitingApproval);

    /// <summary>Safe requestId returned in structured pairing-required details, when present.</summary>
    public string? PairingRequiredRequestId => Volatile.Read(ref _pairingRequiredRequestId);

    /// <summary>True when the device signature was rejected in all supported modes.</summary>
    public bool IsAuthFailed => _authFailed;

    /// <summary>The gateway auth token used for this connection.</summary>
    public string ConnectAuthToken => _connectAuthToken;
    private IReadOnlyList<UserNotificationRule>? _userRules;
    private bool _preferStructuredCategories = true;
    /// <summary>
    /// Controls whether structured notification metadata (Intent, Channel) takes priority
    /// over keyword-based classification. Call after construction and whenever settings change.
    /// </summary>
    public void SetPreferStructuredCategories(bool value) => _preferStructuredCategories = value;

    private void ResetUnsupportedMethodFlags()
    {
        _usageStatusUnsupported = false;
        _usageCostUnsupported = false;
        _sessionPreviewUnsupported = false;
        _nodeListUnsupported = false;
        _modelsListUnsupported = false;
        _nodePairListUnsupported = false;
        _devicePairListUnsupported = false;
        _agentsListUnsupported = false;
        _agentFilesListUnsupported = false;
        _agentFileGetUnsupported = false;
        _operatorReadScopeUnavailable = false;
    }

    /// <summary>
    /// Provides user-defined notification rules to the categorizer so custom rules
    /// are applied when classifying incoming gateway notifications.
    /// Call after construction and whenever settings change.
    /// </summary>
    public void SetUserRules(IReadOnlyList<UserNotificationRule>? rules)
    {
        _userRules = rules;
    }

    protected override int ReceiveBufferSize => 16384;
    protected override string ClientRole => "gateway";

    protected override Task ProcessMessageAsync(string json)
    {
        ProcessMessageForConnection(json, CurrentConnectionGeneration);
        return Task.CompletedTask;
    }

    protected override Task ProcessMessageForConnectionAsync(
        string json,
        long sourceConnectionGeneration)
    {
        ProcessMessageForConnection(json, sourceConnectionGeneration);
        return Task.CompletedTask;
    }

    protected override Task OnConnectedAsync()
    {
        _handshakeChallengeGate.Reset(CurrentConnectionGeneration);
        _pendingRequests.OpenConnection();
        ResetAdvertisedFeatures();
        ResetUnsupportedMethodFlags();
        RaiseTransportConnected();
        return Task.CompletedTask;
    }

    protected void RaiseTransportConnected() =>
        TransportConnected?.Invoke(this, EventArgs.Empty);

    protected override bool ShouldAutoReconnect()
    {
        // PairingRequired must stay visible, but approval only takes effect on a fresh socket.
        return !_authFailed && !_protocolMismatch;
    }

    protected override void OnDisconnected()
    {
        // Invalidate the handshake snapshot — the next hello-ok must
        // re-establish the canonical session key, scopes, etc. Without this,
        // a reconnect-after-server-restart could leave the tray sending to a
        // stale canonical key that the new server doesn't recognize, and
        // HasHandshakeSnapshot would lie about the offline state to callers.
        Volatile.Write(ref _mainSessionKey, null);
        Volatile.Write(ref _mainSessionKeyIsCanonical, false);
        Volatile.Write(ref _hasHandshakeSnapshot, false);
        ResetAdvertisedFeatures();
        _pendingRequests.Drain(
            new GatewayConnectionLostException(
                RemoteCloseStatusCode,
                RemoteCloseStatusDescription));
    }

    protected override void OnDisposing()
    {
        _pendingRequests.Drain();
        _assistantMediaHttpClient.Dispose();
    }

    protected override void OnError(Exception ex)
    {
        _pendingRequests.Drain();
    }

    // Events
    public event EventHandler<OpenClawNotification>? NotificationReceived;
    public event EventHandler<AgentActivity>? ActivityChanged;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<SessionInfo[]>? SessionsUpdated;
    public event EventHandler<GatewayUsageInfo>? UsageUpdated;
    public event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
    public event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
    public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
    public event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    public event EventHandler<JsonElement>? CronListUpdated;
    public event EventHandler<JsonElement>? CronStatusUpdated;
    public event EventHandler<JsonElement>? CronRunsUpdated;
    public event EventHandler<JsonElement>? SkillsStatusUpdated;
    public event EventHandler<JsonElement>? ConfigUpdated;
    public event EventHandler<JsonElement>? ConfigSchemaUpdated;

    /// <summary>
    /// Optional fail-closed authorization invoked after connect.challenge and immediately before
    /// the credential-bearing connect message. Validation clients use this to re-prove the exact
    /// local listener after the WebSocket has connected, closing listener-replacement races.
    /// </summary>
    public Func<CancellationToken, Task<ReconnectAuthorizationResult>>?
        HandshakeAuthorizationAsync { get; set; }

    // New events for agent events, pairing, and models
    public event EventHandler<AgentEventInfo>? AgentEventReceived;
    public event EventHandler<PairingListInfo>? NodePairListUpdated;
    public event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
    public event EventHandler<ModelsListInfo>? ModelsListUpdated;
    public event EventHandler<PresenceEntry[]>? PresenceUpdated;
    public event EventHandler<JsonElement>? AgentsListUpdated;
    public event EventHandler<JsonElement>? AgentFilesListUpdated;
    public event EventHandler<JsonElement>? AgentFileContentUpdated;

    /// <summary>
    /// Raised when the gateway broadcasts a "chat" event (assistant or user
    /// message echo). Use this to drive a chat-UI timeline; the existing
    /// <see cref="NotificationReceived"/> path continues to fire toast
    /// notifications and is unaffected.
    /// </summary>
    public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
    public event EventHandler<AgentEventInfo>? ChatEventReceived;

    /// <summary>Raised when a device token is received from the gateway during hello-ok handshake.</summary>
    public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    /// <summary>Raised when the hello-ok handshake completes successfully.</summary>
    public event EventHandler? HandshakeSucceeded;
    /// <summary>Raised after the WebSocket transport connects, before the gateway handshake begins.</summary>
    public event EventHandler? TransportConnected;
    /// <summary>Raised when the gateway requires pairing approval for this device.</summary>
    public event EventHandler<string?>? PairingRequired;
    /// <summary>Raised when v3 signature was rejected and client fell back to v2.</summary>
    public event EventHandler? V2SignatureFallback;
    /// <summary>
    /// Raised with the typed reason for an operator connection failure. Consumers should use this
    /// kind for policy/UI decisions and keep the accompanying text only for sanitized detail.
    /// </summary>
    public event EventHandler<GatewayErrorKind>? ConnectionFailure;
    /// <summary>Raised with sanitized wire-protocol compatibility details for the current handshake.</summary>
    public event EventHandler<GatewayProtocolCompatibility>? ProtocolCompatibilityChanged;

    public string? OperatorDeviceId => _operatorDeviceId;
    public IReadOnlyList<string> GrantedOperatorScopes => _grantedOperatorScopes;
    public virtual bool IsConnectedToGateway => IsConnected;
    public int? LastRemoteCloseStatusCode => RemoteCloseStatusCode;

    protected override void OnConnectionException(Exception exception)
    {
        RaiseConnectionFailure(GatewayErrorClassifier.Classify(exception.ToString()));
    }

    protected override void OnReconnectAuthorizationDenied(
        ReconnectAuthorizationResult authorization)
    {
        RaiseConnectionFailure(authorization.FailureKind);
    }

    protected void RaiseConnectionFailure(GatewayErrorKind kind) =>
        ConnectionFailure?.Invoke(this, kind);

    public OpenClawGatewayClient(
        string gatewayUrl,
        string token,
        IOpenClawLogger? logger = null,
        bool tokenIsBootstrapToken = false,
        bool bootstrapPairAsNode = false,
        string? identityPath = null,
        bool ignoreStoredDeviceToken = false,
        bool persistHandshakeDeviceTokens = true,
        string? assistantMediaAuthToken = null,
        HttpMessageHandler? assistantMediaHandler = null)
        : base(gatewayUrl, token, logger)
    {
        _tokenIsBootstrapToken = tokenIsBootstrapToken;
        _bootstrapPairAsNode = bootstrapPairAsNode;
        _ignoreStoredDeviceToken = ignoreStoredDeviceToken;
        _persistHandshakeDeviceTokens = persistHandshakeDeviceTokens;
        _assistantMediaHttpClient = assistantMediaHandler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(assistantMediaHandler, disposeHandler: true);
        SetAssistantMediaAuthToken(assistantMediaAuthToken);
        _currentGatewayUrl = gatewayUrl;
        var dataPath = identityPath ?? OpenClawAppIdentity.ResolveRoamingDataDirectory(
            Environment.GetEnvironmentVariable);

        _deviceIdentity = new DeviceIdentity(dataPath, _logger);
        _deviceIdentity.Initialize();
        _connectEnvelopeSigner = new DeviceIdentityConnectEnvelopeSigner(_deviceIdentity);
        _connectAuthToken = HasUsableOperatorDeviceToken ? _deviceIdentity.DeviceToken! : (_tokenIsBootstrapToken ? string.Empty : _token);
        _useV2Signature |= _tokenIsBootstrapToken && !HasUsableOperatorDeviceToken;
        _skillsGatewayApi = new SkillsGatewayApi(SendWizardRequestAsync, EnsureExtensionMethodSupported);
        _pluginsGatewayApi = new PluginsGatewayApi(
            SendWizardRequestAsync,
            EnsureExtensionMethodSupported,
            () => ConnectionEpoch,
            SendExtensionMutationRequestAsync);
    }

    /// <summary>
    /// Updates the independently authorized credential used by assistant-media
    /// HTTP requests. A null or blank value disables authenticated media HTTP.
    /// </summary>
    public void SetAssistantMediaAuthToken(string? token) =>
        Volatile.Write(
            ref _assistantMediaAuthToken,
            string.IsNullOrWhiteSpace(token) ? null : token);

    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            try
            {
                await CloseWebSocketAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during disconnect: {ex.Message}");
            }
        }
        _pendingRequests.Drain();
        RaiseStatusChanged(ConnectionStatus.Disconnected);
        _logger.Info("Disconnected");
    }

    public async Task CheckHealthAsync()
    {
        if (!IsConnected)
        {
            await ReconnectWithBackoffAsync();
            return;
        }

        try
        {
            var req = new
            {
                type = "req",
                id = Guid.NewGuid().ToString(),
                method = "health",
                @params = new { deep = true }
            };
            await SendRawAsync(JsonSerializer.Serialize(req));
        }
        catch (Exception ex)
        {
            _logger.Error("Health check failed", ex);
            RaiseStatusChanged(ConnectionStatus.Error);
            await ReconnectWithBackoffAsync();
        }
    }

    public async Task SendChatMessageAsync(string message, string? sessionKey = null, string? sessionId = null, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        _ = await SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments).ConfigureAwait(false);
    }

    public async Task<ChatSendResult> SendChatMessageForRunAsync(
        string message,
        string? sessionKey = null,
        string? sessionId = null,
        IReadOnlyList<ChatAttachment>? attachments = null,
        string? idempotencyKey = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Gateway connection is not open");

        var hasAttachments = attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message) && !hasAttachments)
            throw new ArgumentException("Message or attachment is required", nameof(message));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key cannot be whitespace", nameof(idempotencyKey));

        var effectiveSessionKey = ResolveEffectiveSessionKey(
            sessionKey, Volatile.Read(ref _mainSessionKey), "chat.send");
        var effectiveIdempotencyKey = idempotencyKey?.Trim() ?? Guid.NewGuid().ToString();

        var requestId = Guid.NewGuid().ToString();
        var pending = _pendingRequests.RegisterChatSend(requestId, "chat.send");

        // The gateway's `chat.send` validates strictly on (sessionKey, message,
        // idempotencyKey). The spec document mentioned `sessionId` and `text`
        // as Control-UI variants, but the live operator endpoint rejects them
        // ("unexpected property"). The `sessionId` parameter is therefore only
        // tracked client-side (used for display correlation) and not forwarded.
        _ = sessionId; // reserved for future protocol versions

        var chatParams = new Dictionary<string, object?>
        {
            ["sessionKey"] = effectiveSessionKey,
            ["message"] = message,
            ["idempotencyKey"] = effectiveIdempotencyKey
        };

        if (hasAttachments)
            chatParams["attachments"] = attachments;

        var req = new
        {
            type = "req",
            id = requestId,
            method = "chat.send",
            @params = chatParams
        };

        try
        {
            await SendRawAsync(JsonSerializer.Serialize(req));
            var result = await WaitForGatewayResponseAsync(
                pending.Task,
                TimeSpan.FromSeconds(5),
                CancellationToken,
                "Timed out waiting for chat.send response from gateway");
            _logger.Info($"Sent chat message ({message.Length} chars{(hasAttachments ? $", {attachments!.Count} attachment(s)" : "")})");
            return result;
        }
        finally
        {
            _pendingRequests.TryRemove(pending.Handle);
        }
    }

    Task IOperatorGatewayClient.SendChatMessageAsync(string message, string? sessionKey) =>
        SendChatMessageAsync(message, sessionKey);

    Task<ChatSendResult> IOperatorGatewayClient.SendChatMessageForRunAsync(string message, string? sessionKey) =>
        SendChatMessageForRunAsync(message, sessionKey);

    /// <summary>
    /// Fetches the conversation transcript for a session. The gateway applies
    /// display normalization (strips delivery directives, tool-call XML, etc.)
    /// before returning. Throws on RPC failure or timeout.
    /// </summary>
    public async Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey = null, int timeoutMs = 15000)
    {
        var effectiveSessionKey = ResolveEffectiveSessionKey(
            sessionKey, Volatile.Read(ref _mainSessionKey), "chat.history");

        var payload = await SendWizardRequestAsync(
            "chat.history",
            new { sessionKey = effectiveSessionKey },
            timeoutMs);

        return ParseChatHistory(payload, effectiveSessionKey);
    }

    /// <summary>
    /// Aborts an in-flight agent run. Per spec, partial assistant output may
    /// still be visible after the abort and is persisted into the transcript
    /// with abort metadata.
    /// </summary>
    public async Task SendChatAbortAsync(string runId, string? sessionKey = null, int timeoutMs = 5000)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("runId is required", nameof(runId));
        var effectiveSessionKey = ResolveEffectiveSessionKey(
            sessionKey, Volatile.Read(ref _mainSessionKey), "chat.abort");
        await SendWizardRequestAsync("chat.abort", new { runId, sessionKey = effectiveSessionKey }, timeoutMs);
    }

    /// <summary>
    /// Resolves a pending exec approval over the operator-approvals gateway RPC
    /// (<c>exec.approval.resolve</c>). Required scope: <c>operator.approvals</c>.
    ///
    /// This is the correct path for native approval-button UI. Do NOT use a
    /// <c>/approve &lt;id&gt; &lt;decision&gt;</c> chat message: slash commands are
    /// queued behind the agent's main turn, but the agent is blocked waiting
    /// on the approval — so the slash command can only be processed after the
    /// run times out, by which point the approval is moot.
    /// </summary>
    /// <param name="approvalId">The exec approval id from <c>exec.approval.requested.payload.id</c>.</param>
    /// <param name="decision">One of <c>allow-once</c>, <c>allow-always</c>, <c>deny</c>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="approvalId"/> is empty or <paramref name="decision"/> is not in the allowlist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the gateway is not connected at call time, or returns an <c>ok:false</c> response. The caller (e.g. the chat approval banner) relies on this to keep the Allow/Deny UI on screen for retry instead of silently dismissing it when the resolve was actually rejected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the gateway connection is lost while the resolve is in flight. Same banner-preserve contract.</exception>
    /// <exception cref="TimeoutException">Thrown when no response arrives within the resolve timeout window. Preserves the banner for retry.</exception>
    public async Task ResolveExecApprovalAsync(string approvalId, string decision)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            throw new ArgumentException("approvalId is required", nameof(approvalId));
        if (string.IsNullOrWhiteSpace(decision))
            throw new ArgumentException("decision is required", nameof(decision));
        if (!IsValidApprovalDecision(decision))
            throw new ArgumentException($"decision must be one of allow-once, allow-always, deny (got '{decision}')", nameof(decision));

        // Up-front disconnect guard: avoid registering a pending request that
        // can never complete. The post-await re-check below handles the
        // race window where the socket drops mid-send.
        if (!IsConnected)
            throw new InvalidOperationException("Cannot resolve exec approval: gateway is not connected.");

        var requestId = Guid.NewGuid().ToString();
        var pending = _pendingRequests.RegisterApproval(
            requestId,
            "exec.approval.resolve");

        // A lifecycle drain faults this task with OperationCanceledException.
        // Banner callers must keep catching System.Exception so disconnects
        // preserve the approval UI and surface a retryable error.
        try
        {
            await SendRawAsync(SerializeRequest(requestId, "exec.approval.resolve", new { id = approvalId, decision }));
        }
        catch
        {
            _pendingRequests.TryRemove(pending.Handle);
            throw;
        }

        // Post-send disconnect re-check. Closes a race where the WebSocket
        // dies between the up-front IsConnected guard and TCS registration:
        // Drain can run before registration, then SendRawAsync silently
        // succeeds against the now-dead socket, leaving the TCS stranded
        // until the 15s timeout instead of failing fast for retry.
        //
        // TryRemove-first idiom: if we win the race, throw immediately with no
        // exception set on the discarded task. If TryRemove returns false,
        // Drain already claimed and faulted our entry; fall
        // through to the WhenAny/await path so the OperationCanceledException
        // it set is observed by the caller.
        if (!IsConnected && _pendingRequests.TryRemove(pending.Handle))
        {
            throw new InvalidOperationException("Gateway disconnected before exec.approval.resolve was sent.");
        }

        // Bounded wait. Approval-resolve is interactive and the response may
        // traverse gateway → operator → optional UI confirm → ack, so we give
        // it a larger budget than the chat.send sibling's 5s. A timeout keeps
        // the banner visible for the user to retry rather than hanging the UI.
        try
        {
            await WaitForGatewayResponseAsync(
                pending.Task,
                TimeSpan.FromSeconds(15),
                CancellationToken,
                "Timed out waiting for exec.approval.resolve response from gateway");
        }
        finally
        {
            _pendingRequests.TryRemove(pending.Handle);
        }
    }

    private static async Task<T> WaitForGatewayResponseAsync<T>(
        Task<T> responseTask,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationSignal.TrySetCanceled(cancellationToken));
        var timeoutTask = Task.Delay(timeout);
        var completedTask = await Task.WhenAny(responseTask, timeoutTask, cancellationSignal.Task);
        return await ResolveGatewayResponseAsync(
            responseTask,
            completedTask,
            cancellationSignal.Task,
            timeoutMessage);
    }

    internal static async Task<T> ResolveGatewayResponseAsync<T>(
        Task<T> responseTask,
        Task completedTask,
        Task cancellationTask,
        string timeoutMessage)
    {
        // A response that arrived before this continuation resumed wins over
        // either terminal signal, preserving the existing same-tick contract.
        if (responseTask.IsCompleted)
            return await responseTask.ConfigureAwait(false);

        // Classify from the task that actually won WhenAny. Sampling the token
        // here would let later shutdown reclassify an already-elapsed deadline.
        if (ReferenceEquals(completedTask, cancellationTask))
        {
            await cancellationTask.ConfigureAwait(false);
            throw new OperationCanceledException("Gateway request canceled");
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static bool IsValidApprovalDecision(string decision)
        => string.Equals(decision, "allow-once", StringComparison.Ordinal)
        || string.Equals(decision, "allow-always", StringComparison.Ordinal)
        || string.Equals(decision, "deny", StringComparison.Ordinal);

    private static ChatHistoryInfo ParseChatHistory(JsonElement payload, string sessionKey)
    {
        var info = new ChatHistoryInfo { SessionKey = sessionKey };
        if (payload.ValueKind != JsonValueKind.Object) return info;

        if (payload.TryGetProperty("sessionId", out var sidProp) && sidProp.ValueKind == JsonValueKind.String)
            info.SessionId = sidProp.GetString();

        if (!payload.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return info;

        var list = new List<ChatMessageInfo>(msgs.GetArrayLength());
        foreach (var m in msgs.EnumerateArray())
        {
            var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
            // Spec doc lists `ts`, but gateway 2026.4.23 actually returns
            // `timestamp` on chat.history rows (verified via WS RX trace).
            // Accept both for forward/back compat.
            long ts = 0;
            if (m.TryGetProperty("timestamp", out var tsProp1) && tsProp1.ValueKind == JsonValueKind.Number)
                ts = tsProp1.GetInt64();
            else if (m.TryGetProperty("ts", out var tsProp2) && tsProp2.ValueKind == JsonValueKind.Number)
                ts = tsProp2.GetInt64();

            var openClawMetadata = ExtractOpenClawMetadata(m);

            // stopReason on assistant messages (e.g. "stop", "toolUse", possibly "abort")
            string? stopReason = null;
            if (m.TryGetProperty("stopReason", out var sr))
                stopReason = sr.GetString();

            // Content can include text and structured tool call/result blocks.
            string text = ExtractMessageText(m, role);
            var toolContent = ExtractToolContent(m, role, text);
            var contentParts = ExtractOrderedMessageContent(m, role, toolContent);
            if (string.IsNullOrEmpty(text)
                && toolContent.Count == 0
                && contentParts.All(static part => part.Kind != ChatMessageContentPartKind.Media))
            {
                continue;
            }
            if (string.IsNullOrEmpty(role)) continue;
            if (!string.IsNullOrEmpty(text)
                && toolContent.Count == 0
                && ChatMessageInfo.IsSilentAssistantDirective(role, text))
            {
                continue;
            }

            var (inputTokens, outputTokens, responseTokens, contextPercent) = ExtractChatUsage(m);
            list.Add(new ChatMessageInfo
            {
                SessionKey = sessionKey,
                Role = role,
                Text = text,
                ToolContent = toolContent,
                ContentParts = contentParts,
                State = "final",
                Ts = ts,
                OpenClawId = openClawMetadata.Id,
                OpenClawSeq = openClawMetadata.Seq,
                OpenClawKind = openClawMetadata.Kind,
                CompactionTokensBefore = openClawMetadata.TokensBefore,
                CompactionTokensAfter = openClawMetadata.TokensAfter,
                StopReason = stopReason,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ResponseTokens = responseTokens,
                ContextPercent = contextPercent
            });
        }
        info.Messages = list;
        return info;
    }

    private static string ExtractMessageText(JsonElement message, string role)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
        {
            return AssistantMediaDirectiveParser.Project(role, content.GetString()).Text;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(item.GetString());
                }
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("type", out var ty) && ty.GetString() == "text" &&
                         item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(tx.GetString());
                }
            }
            return AssistantMediaDirectiveParser.Project(role, sb.ToString()).Text;
        }

        return string.Empty;
    }

    private static IReadOnlyList<ChatToolContentInfo> ExtractToolContent(
        JsonElement message,
        string role,
        string messageText)
    {
        var blocks = new List<ChatToolContentInfo>();
        var hasArrayContent = message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array;
        if (hasArrayContent)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var typeElement)
                    || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var normalizedType = (typeElement.GetString() ?? string.Empty)
                    .Replace("_", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();
                var kind = normalizedType switch
                {
                    "toolcall" or "tooluse" => ChatToolContentKind.Call,
                    "toolresult" => ChatToolContentKind.Result,
                    _ => (ChatToolContentKind?)null,
                };
                if (kind is null)
                    continue;

                var itemSemanticCallId = ReadFirstString(
                    item,
                    "callId",
                    "tool_call_id",
                    "toolCallId",
                    "tool_use_id",
                    "toolUseId");
                var messageSemanticCallId = ReadFirstString(
                    message,
                    "callId",
                    "tool_call_id",
                    "toolCallId",
                    "tool_use_id",
                    "toolUseId");
                var callId = kind == ChatToolContentKind.Call
                    ? ReadFirstString(item, "id")
                        ?? itemSemanticCallId
                        ?? messageSemanticCallId
                    : itemSemanticCallId
                        ?? messageSemanticCallId
                        ?? ReadFirstString(item, "id");
                blocks.Add(new ChatToolContentInfo
                {
                    Kind = kind.Value,
                    CallId = callId,
                    ToolName = ReadFirstString(item, "name")
                        ?? ReadFirstString(message, "toolName", "tool_name")
                        ?? "tool",
                    Args = kind == ChatToolContentKind.Call
                        ? ReadFirstValue(item, "args", "arguments", "input")
                        : null,
                    Text = kind == ChatToolContentKind.Result
                        ? ExtractToolContentText(item)
                        : null,
                    IsError = ReadNullableBoolean(item, "isError", "is_error")
                        ?? ReadBoolean(message, "isError", "is_error"),
                });
            }
        }

        var normalizedRole = role.Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var supportsResultFallback = normalizedRole == "toolresult"
            || (hasArrayContent && normalizedRole is "tool" or "function");
        if (blocks.All(static block => block.Kind != ChatToolContentKind.Result)
            && supportsResultFallback
            && !string.IsNullOrEmpty(messageText))
        {
            blocks.Add(new ChatToolContentInfo
            {
                Kind = ChatToolContentKind.Result,
                CallId = ReadFirstString(
                    message,
                    "callId",
                    "tool_call_id",
                    "toolCallId",
                    "tool_use_id",
                    "toolUseId"),
                ToolName = ReadFirstString(message, "toolName", "tool_name", "name") ?? "tool",
                Text = messageText,
                IsError = ReadBoolean(message, "isError", "is_error"),
            });
        }

        return blocks;
    }

    private static IReadOnlyList<ChatMessageContentPartInfo> ExtractOrderedMessageContent(
        JsonElement message,
        string role,
        IReadOnlyList<ChatToolContentInfo> toolContent)
    {
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
        {
            return Array.Empty<ChatMessageContentPartInfo>();
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return AssistantMediaDirectiveParser.Project(role, content.GetString()).ContentParts;
        }

        var parts = new List<ChatMessageContentPartInfo>();
        var textRun = new StringBuilder();
        var toolIndex = 0;
        var structuredToolCount = 0;

        void AppendTextBlock(string? value)
        {
            if (textRun.Length > 0)
                textRun.Append('\n');
            textRun.Append(value);
        }

        void FlushTextRun()
        {
            if (textRun.Length == 0)
                return;
            parts.AddRange(
                AssistantMediaDirectiveParser.Project(role, textRun.ToString()).ContentParts);
            textRun.Clear();
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AppendTextBlock(item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                FlushTextRun();
                continue;
            }

            var normalizedType = (typeElement.GetString() ?? string.Empty)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (normalizedType == "text")
            {
                if (item.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    AppendTextBlock(textElement.GetString());
                }
                continue;
            }

            FlushTextRun();
            if (TryParseStructuredMedia(item, normalizedType, out var media))
            {
                parts.Add(new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = media,
                });
                continue;
            }

            if (normalizedType is not ("toolcall" or "tooluse" or "toolresult"))
                continue;

            structuredToolCount++;
            if (toolIndex < toolContent.Count)
            {
                parts.Add(new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Tool,
                    Tool = toolContent[toolIndex++],
                });
            }
        }

        FlushTextRun();

        var normalizedRole = role.Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (structuredToolCount == 0
            && normalizedRole is "toolresult" or "tool" or "function"
            && toolContent.Count > 0)
        {
            return toolContent
                .Select(static tool => new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Tool,
                    Tool = tool,
                })
                .ToArray();
        }

        while (toolIndex < toolContent.Count)
        {
            parts.Add(new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Tool,
                Tool = toolContent[toolIndex++],
            });
        }

        return parts;
    }

    private static bool TryParseStructuredMedia(
        JsonElement item,
        string normalizedType,
        out ChatMediaContentInfo media)
    {
        media = null!;
        var mimeType = ReadFirstString(item, "mimeType", "mime_type")?.Trim().ToLowerInvariant();
        var hasMediaShape = normalizedType is "image" or "audio" or "video" or "file" or "attachment"
            || !string.IsNullOrWhiteSpace(mimeType)
            || ReadFirstString(item, "artifactId", "artifact_id") is not null
            || ReadFirstString(item, "url") is not null;
        if (!hasMediaShape)
            return false;

        var kind = normalizedType switch
        {
            "image" => ChatMediaContentKind.Image,
            "audio" => ChatMediaContentKind.Audio,
            "video" => ChatMediaContentKind.Video,
            "file" or "attachment" => ClassifyMediaMimeType(mimeType, ChatMediaContentKind.File),
            _ => ClassifyMediaMimeType(mimeType, ChatMediaContentKind.Unknown),
        };
        var url = ReadFirstString(item, "url");
        var artifactId = ReadFirstString(item, "artifactId", "artifact_id")
            ?? TryCreateManagedArtifactId(url, kind);

        media = new ChatMediaContentInfo
        {
            Kind = kind,
            Source = ChatMediaContentSource.Structured,
            Type = ReadFirstString(item, "type"),
            MimeType = NormalizeMediaMimeType(mimeType),
            FileName = NormalizeMediaDisplayText(
                ReadFirstString(item, "fileName", "file_name"),
                255),
            ArtifactId = NormalizeMediaDisplayText(artifactId, 512),
            AgentId = NormalizeMediaDisplayText(ReadFirstString(item, "agentId", "agent_id"), 256),
            Url = NormalizeMediaProtocolValue(url, 4096),
            OpenUrl = NormalizeMediaProtocolValue(ReadFirstString(item, "openUrl", "open_url"), 4096),
            Alt = NormalizeMediaDisplayText(ReadFirstString(item, "alt"), 1024),
            Width = ReadPositiveInt(item, "width"),
            Height = ReadPositiveInt(item, "height"),
            SizeBytes = ReadPositiveLong(item, "sizeBytes", "size_bytes"),
            DurationSeconds = ReadDurationSeconds(item),
            Playback = ReadFirstString(item, "playback")?.ToLowerInvariant() switch
            {
                "native" => ChatMediaPlaybackMode.Native,
                "transcode" => ChatMediaPlaybackMode.Transcode,
                _ => null,
            },
        };
        return true;
    }

    private static ChatMediaContentKind ClassifyMediaMimeType(
        string? mimeType,
        ChatMediaContentKind fallback)
    {
        if (mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return ChatMediaContentKind.Image;
        if (mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return ChatMediaContentKind.Audio;
        if (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return ChatMediaContentKind.Video;
        return fallback;
    }

    private static string? NormalizeMediaMimeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains('/') && normalized.All(static character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '/' or '+' or '-' or '.' or '_')
                ? normalized
                : null;
    }

    private static string? NormalizeMediaDisplayText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (!char.IsControl(character) && character is not '\r' and not '\n')
                builder.Append(character);
            if (builder.Length == maxLength)
                break;
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static string? NormalizeMediaProtocolValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return null;
        return value.Trim();
    }

    private static int? ReadPositiveInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result)
            || result <= 0)
        {
            return null;
        }
        return result;
    }

    private static long? ReadPositiveLong(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var result)
                && result > 0)
            {
                return result;
            }
        }
        return null;
    }

    private static double? ReadDurationSeconds(JsonElement item)
    {
        if (item.TryGetProperty("durationSeconds", out var seconds)
            && seconds.ValueKind == JsonValueKind.Number
            && seconds.TryGetDouble(out var secondsValue)
            && secondsValue >= 0)
        {
            return secondsValue;
        }
        if (item.TryGetProperty("durationMs", out var milliseconds)
            && milliseconds.ValueKind == JsonValueKind.Number
            && milliseconds.TryGetDouble(out var millisecondsValue)
            && millisecondsValue >= 0)
        {
            return millisecondsValue / 1000d;
        }
        return null;
    }

    private static string? TryCreateManagedArtifactId(string? rawUrl, ChatMediaContentKind kind)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Relative, out var relative)
            || relative.IsAbsoluteUri)
        {
            return null;
        }
        var path = rawUrl.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 7
            || !segments[..4].SequenceEqual(new[] { "api", "chat", "media", "outgoing" })
            || !string.Equals(segments[6], "full", StringComparison.Ordinal)
            || !Guid.TryParse(segments[5], out var attachmentId))
        {
            return null;
        }
        var prefix = kind is ChatMediaContentKind.Audio or ChatMediaContentKind.Video
            ? "artifact_managed_media_"
            : "artifact_managed_image_";
        return prefix + attachmentId.ToString("D").ToLowerInvariant();
    }

    private static JsonElement? ReadFirstValue(JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (value.TryGetProperty(propertyName, out var property))
                return property.Clone();
        }

        return null;
    }

    private static string? ReadFirstString(JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (value.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool ReadBoolean(JsonElement value, params string[] propertyNames)
        => ReadNullableBoolean(value, propertyNames) ?? false;

    private static bool? ReadNullableBoolean(JsonElement value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (value.TryGetProperty(propertyName, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }
        }

        return null;
    }

    private static string? ExtractToolContentText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (!item.TryGetProperty("content", out var content))
            return null;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = content.EnumerateArray()
            .Where(static part => part.ValueKind == JsonValueKind.Object)
            .Select(static part =>
                part.TryGetProperty("text", out var partText)
                && partText.ValueKind == JsonValueKind.String
                    ? partText.GetString()
                    : null)
            .Where(static part => !string.IsNullOrEmpty(part))
            .ToArray();
        return parts.Length > 0 ? string.Join('\n', parts) : null;
    }

    /// <summary>
    /// Sends a wizard RPC request and waits for the response payload.
    /// Used for wizard.start, wizard.next, wizard.cancel, wizard.status.
    /// </summary>
    public Task<JsonElement> SendWizardRequestAsync(
        string method,
        object? parameters = null,
        int timeoutMs = 30000) =>
        SendWizardRequestCoreAsync(method, parameters, timeoutMs, expectedConnectionEpoch: null);

    private Task<JsonElement> SendExtensionMutationRequestAsync(
        string method,
        object? parameters,
        int timeoutMs,
        long expectedConnectionEpoch) =>
        SendWizardRequestCoreAsync(method, parameters, timeoutMs, expectedConnectionEpoch);

    private async Task<JsonElement> SendWizardRequestCoreAsync(
        string method,
        object? parameters,
        int timeoutMs,
        long? expectedConnectionEpoch)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Gateway connection is not open");

        _logger.Info($"[GatewayClient] Sending frame: {method}");
        var requestId = Guid.NewGuid().ToString();
        var pending = _pendingRequests.RegisterWizard(requestId, method);

        try
        {
            var serializedRequest = SerializeRequest(requestId, method, parameters);
            if (expectedConnectionEpoch.HasValue)
            {
                var sent = await SendRawAsync(
                    serializedRequest,
                    expectedConnectionEpoch.Value,
                    CancellationToken).ConfigureAwait(false);
                if (!sent)
                {
                    throw new InvalidOperationException(
                        "Plugin operation expired after the Gateway connection changed.");
                }
            }
            else
            {
                await SendRawAsync(serializedRequest).ConfigureAwait(false);
            }
            return await pending.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for {method} response", ex);
        }
        finally
        {
            _pendingRequests.TryRemove(pending.Handle);
        }
    }

    /// <summary>Request session list from gateway.</summary>
    public async Task RequestSessionsAsync(string? agentId = null)
    {
        if (_operatorReadScopeUnavailable) return;
        if (!string.IsNullOrEmpty(agentId))
            await SendTrackedRequestAsync("sessions.list", new { agentId });
        else
            await SendTrackedRequestAsync("sessions.list");
    }

    /// <summary>Subscribe to session change events so the gateway pushes
    /// <c>sessions.changed</c> notifications when sessions are mutated.</summary>
    public async Task SubscribeSessionEventsAsync()
    {
        var ok = await TrySendTrackedRequestAsync("sessions.subscribe", new { });
        _logger.Info($"[SUBSCRIBE] sessions.subscribe sent, result={ok}");
    }

    /// <summary>Request usage/context info from gateway (may not be supported on all gateways).</summary>
    public async Task RequestUsageAsync()
    {
        if (_operatorReadScopeUnavailable) return;
        if (!IsConnected) return;
        try
        {
            if (_usageStatusUnsupported)
            {
                await RequestLegacyUsageAsync();
                return;
            }

            await RequestUsageStatusAsync();
            if (!_usageCostUnsupported)
            {
                await RequestUsageCostAsync(days: 30);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Usage request failed: {ex.Message}");
        }
    }

    /// <summary>Request connected node inventory from gateway.</summary>
    public async Task RequestNodesAsync()
    {
        if (_operatorReadScopeUnavailable) return;
        if (_nodeListUnsupported) return;
        await SendTrackedRequestAsync("node.list");
    }

    public async Task RequestUsageStatusAsync()
    {
        await SendTrackedRequestAsync("usage.status");
    }

    public async Task RequestUsageCostAsync(int days = 30)
    {
        if (days <= 0) days = 30;
        await SendTrackedRequestAsync("usage.cost", new { days });
    }

    public async Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240)
    {
        if (_sessionPreviewUnsupported) return;
        if (keys.Length == 0) return;
        if (limit <= 0) limit = 1;
        if (maxChars < 20) maxChars = 20;

        await SendTrackedRequestAsync("sessions.preview", new
        {
            keys,
            limit,
            maxChars
        });
    }

    public Task<bool> PatchSessionAsync(string key, string? model = null, string? thinkingLevel = null, string? verboseLevel = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);

        var payload = new Dictionary<string, object?>
        {
            ["key"] = key
        };
        if (model is not null)
            payload["model"] = model;
        if (thinkingLevel is not null)
            payload["thinkingLevel"] = thinkingLevel;
        if (verboseLevel is not null)
            payload["verboseLevel"] = verboseLevel;
        return TrySendTrackedRequestAsync("sessions.patch", payload);
    }

    public Task<bool> ResetSessionAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return TrySendTrackedRequestAsync("sessions.reset", new { key });
    }

    public Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return TrySendTrackedRequestAsync("sessions.delete", new { key, deleteTranscript });
    }

    public Task<bool> CompactSessionAsync(string key, int maxLines = 400)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        if (maxLines <= 0) maxLines = 400;
        return TrySendTrackedRequestAsync("sessions.compact", new { key, maxLines });
    }

    // Cron job management

    public async Task RequestCronListAsync()
    {
        await SendTrackedRequestAsync("cron.list", new { includeDisabled = true });
    }

    public async Task RequestCronStatusAsync()
    {
        await SendTrackedRequestAsync("cron.status");
    }

    public async Task<bool> RunCronJobAsync(string jobId, bool force = true)
    {
        var result = await RunCronJobDetailedAsync(jobId, force);
        return result.Accepted && result.Enqueued;
    }

    public async Task<CronRunRequestResult> RunCronJobDetailedAsync(string jobId, bool force = true, int timeoutMs = 12000)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return CronRunRequestResult.NotAccepted("Job id is required.");

        var payloads = new List<object>();
        if (!force)
            payloads.Add(new { jobId, mode = "due" });
        payloads.Add(new { jobId });
        payloads.Add(new { id = jobId, force });

        string? lastError = null;
        foreach (var requestPayload in payloads)
        {
            try
            {
                var payload = await SendWizardRequestAsync("cron.run", requestPayload, timeoutMs);
                return ParseCronRunRequestResult(payload);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (!IsCronRunPayloadShapeError(ex.Message))
                    break;
            }
        }

        _logger.Warn($"cron.run request failed: {lastError}");
        return CronRunRequestResult.NotAccepted(lastError);
    }

    private static bool IsCronRunPayloadShapeError(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.Contains("invalid cron.run params", StringComparison.OrdinalIgnoreCase);

    internal static CronRunRequestResult ParseCronRunRequestResult(JsonElement payload)
    {
        var accepted = !payload.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
        var hasEnqueued = payload.TryGetProperty("enqueued", out var enqEl);
        var enqueued = hasEnqueued && enqEl.ValueKind == JsonValueKind.True;
        var enqueuedFalse = hasEnqueued && enqEl.ValueKind == JsonValueKind.False;
        var hasRan = payload.TryGetProperty("ran", out var ranEl);
        var ran = hasRan
            ? ranEl.ValueKind == JsonValueKind.True
            : (bool?)null;
        var runId = payload.TryGetProperty("runId", out var runIdEl) && runIdEl.ValueKind == JsonValueKind.String
            ? runIdEl.GetString()
            : null;
        var reason = payload.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString()
            : null;

        return new CronRunRequestResult(
            accepted,
            accepted && !enqueuedFalse && ran != false &&
            (enqueued || !string.IsNullOrWhiteSpace(runId) || ran == true || (!hasEnqueued && !hasRan)),
            runId,
            reason);
    }

    public Task<bool> RemoveCronJobAsync(string jobId)
    {
        return TrySendTrackedRequestAsync("cron.remove", new { id = jobId });
    }

    public Task<bool> AddCronJobAsync(object jobDefinition)
    {
        return TrySendTrackedRequestAsync("cron.add", jobDefinition);
    }

    public Task<bool> UpdateCronJobAsync(string id, object patch)
    {
        // Wire format uses "id" consistently with cron.run / cron.remove
        return TrySendTrackedRequestAsync("cron.update", new { id, patch });
    }

    public async Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0)
    {
        // Wire format uses "id" consistently with cron.run / cron.remove
        await SendTrackedRequestAsync("cron.runs", new { id, limit, offset });
    }

    // Skills/plugin management

    public async Task RequestSkillsStatusAsync(string? agentId = null)
    {
        _lastSkillsStatusAgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId;

        if (_lastSkillsStatusAgentId is not null)
            await SendTrackedRequestAsync("skills.status", new { agentId = _lastSkillsStatusAgentId });
        else
            await SendTrackedRequestAsync("skills.status");
    }

    [Obsolete("Use InstallClawHubSkillAsync with the exact skills.search installRef.")]
    public Task<bool> InstallSkillAsync(string skillId)
    {
        // The former { id } payload is rejected by every supported Gateway skill
        // install schema and is unsafe once publishers share a slug. Keep the
        // compatibility member non-mutating until callers migrate to the typed API.
        return Task.FromResult(false);
    }

    public async Task<bool> SetSkillEnabledAsync(string skillKey, bool enabled)
    {
        try
        {
            var result = await SetSkillEnabledDetailedAsync(skillKey, enabled).ConfigureAwait(false);
            return result.Ok;
        }
        catch (Exception ex)
        {
            _logger.Warn($"skills.update failed: {TokenSanitizer.Sanitize(ex.Message)}");
            return false;
        }
    }

    // Gateway config management

    public async Task RequestConfigAsync()
    {
        await SendTrackedRequestAsync("config.get");
    }

    public async Task RequestConfigSchemaAsync()
    {
        await SendTrackedRequestAsync("config.schema");
    }

    public Task<bool> SetConfigAsync(string path, object value)
    {
        return TrySendTrackedRequestAsync("config.set", new { path, value });
    }

    /// <summary>
    /// Patch the gateway config. The gateway expects { raw: "full json string", baseHash: "..." }.
    /// </summary>
    public Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash)
    {
        var raw = fullConfig.GetRawText();
        if (baseHash != null)
            return TrySendTrackedRequestAsync("config.patch", new { raw, baseHash });
        else
            return TrySendTrackedRequestAsync("config.patch", new { raw });
    }

    /// <summary>
    /// Response-aware variant of <see cref="PatchConfigAsync"/>. Uses the
    /// wizard request mechanism (<see cref="SendWizardRequestAsync"/>) so we
    /// actually await the gateway's response and return a <see cref="ConfigPatchResult"/>
    /// with the real error message on failure. The fire-and-forget
    /// <see cref="PatchConfigAsync"/> stays for legacy callers that don't
    /// care about the gateway's reply.
    /// </summary>
    public async Task<ConfigPatchResult> PatchConfigDetailedAsync(JsonElement fullConfig, string? baseHash, int timeoutMs = 15000)
    {
        var raw = fullConfig.GetRawText();
        object payload = baseHash != null ? new { raw, baseHash } : (object)new { raw };
        try
        {
            var response = await SendWizardRequestAsync("config.patch", payload, timeoutMs);
            _logger.Info("config.patch succeeded");
            return new ConfigPatchResult
            {
                Ok = true,
                RawResponse = response.ValueKind == JsonValueKind.Undefined ? null : response.GetRawText(),
            };
        }
        catch (Exception ex)
        {
            // Sanitize before logging — the gateway sometimes echoes patched
            // field values in validation errors, so logging ex.Message verbatim
            // can leak secrets to the on-disk tray log (Hanselman review LOW-7).
            // The full unsanitized message stays in ConfigPatchResult.Error so
            // the UI banner can show it.
            _logger.Warn($"config.patch failed: {SanitizeErrorForLog(ex.Message)}");
            return new ConfigPatchResult
            {
                Ok = false,
                Error = ex.Message,
                RawResponse = ex.ToString(),
            };
        }
    }

    /// <summary>
    /// Best-effort scrub of a gateway error message before it lands in the
    /// tray log: caps length and masks token-shaped values for the channel
    /// credential keys we know about (botToken / signingSecret / webhookUrl
    /// / nsec / privateKey / generic token|secret|key|password fields). The
    /// gateway may put raw field values in validation errors, and the log
    /// is persistent on disk — see Hanselman review LOW-7.
    /// </summary>
    private static string SanitizeErrorForLog(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? "";
        const int MaxLen = 500;
        var truncated = raw.Length > MaxLen ? raw[..MaxLen] + "…(truncated)" : raw;
        // Mask JSON-style "field": "value" pairs for sensitive field names.
        truncated = System.Text.RegularExpressions.Regex.Replace(
            truncated,
            @"(""(?i:botToken|signingSecret|webhookUrl|nsec|privateKey|token|secret|apiKey|password)""\s*:\s*"")[^""]+("")",
            "$1<redacted>$2");
        return truncated;
    }

    // Agent methods

    public async Task RequestAgentsListAsync()
    {
        if (_agentsListUnsupported) return;
        await SendTrackedRequestAsync("agents.list");
    }

    public async Task RequestAgentFilesListAsync(string agentId = "main")
    {
        if (_agentFilesListUnsupported) return;
        await SendTrackedRequestAsync("agents.files.list", new { agentId });
    }

    public async Task RequestAgentFileGetAsync(string agentId, string name)
    {
        if (_agentFileGetUnsupported) return;
        await SendTrackedRequestAsync("agents.files.get", new { agentId, name });
    }

    // Models list

    public async Task RequestModelsListAsync()
    {
        if (_modelsListUnsupported) return;

        ModelsListInfo configured;
        try
        {
            var configuredPayload = await SendWizardRequestAsync(
                "models.list",
                new { view = "configured" },
                timeoutMs: 10000);
            configured = ParseModelsListPayload(configuredPayload);
        }
        catch (Exception ex)
        {
            // Preserve the legacy fire-and-forget path for older gateways and
            // transient response-aware request failures.
            _logger.Warn($"Configured models.list request failed; using legacy request path: {ex.Message}");
            await SendTrackedRequestAsync("models.list", new { view = "configured" });
            return;
        }

        // Populate the existing safe choices immediately, then enhance the picker
        // when the broader catalog response arrives.
        ModelsListUpdated?.Invoke(this, configured);

        ModelsListInfo catalog;
        try
        {
            var catalogPayload = await SendWizardRequestAsync(
                "models.list",
                new { view = "all" },
                timeoutMs: 10000);
            catalog = ParseModelsListPayload(catalogPayload);
        }
        catch (Exception ex)
        {
            // A full catalog is an enhancement. The configured list remains a
            // safe, selectable fallback when discovery is unsupported or slow.
            _logger.Warn($"Full models.list catalog request failed; keeping configured models only: {ex.Message}");
            return;
        }

        ModelsListUpdated?.Invoke(this, MergeModelCatalog(configured, catalog));
    }

    // Node/Device pairing

    public async Task RequestNodePairListAsync()
    {
        if (_nodePairListUnsupported) return;
        await SendTrackedRequestAsync("node.pair.list");
    }

    public virtual Task<bool> NodePairApproveAsync(string requestId) =>
        ApprovePairingRequestAsync("node.pair.approve", requestId, RequestNodePairListAsync);

    public Task<bool> NodePairRejectAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("node.pair.reject", new { requestId });
    }

    /// <summary>
    /// Removes a paired node from the gateway and waits for the gateway's
    /// application-level response. Returns Success=true only when the
    /// gateway confirms the removal — Success=false on transport failure,
    /// missing scope, unknown nodeId, or any server-side rejection. The
    /// gateway also broadcasts <c>node.pair.resolved</c> with
    /// <c>decision="removed"</c> after success, which the broadcast handler
    /// turns into a node.list + node.pair.list refresh.
    /// </summary>
    public async Task<NodeForgetResult> NodePairRemoveAsync(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return new NodeForgetResult(false, "nodeId required");
        if (!IsConnected)
            return new NodeForgetResult(false, "Gateway connection is not open");

        try
        {
            // SendWizardRequestAsync awaits the matching ack frame and
            // throws InvalidOperationException when the gateway responds
            // with ok=false, so callers see a real failure result on
            // rejection (missing scope, unknown nodeId) rather than a
            // false success the moment the WS frame is sent.
            await SendWizardRequestAsync("node.pair.remove", new { nodeId });
            return new NodeForgetResult(true);
        }
        catch (InvalidOperationException ex)
        {
            // Gateway business error (e.g. "missing scope: operator.pairing",
            // "unknown nodeId"). Surface this verbatim so the user sees an
            // actionable message.
            _logger.Warn($"node.pair.remove rejected: {ex.Message}");
            return new NodeForgetResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            // Transport / timeout / unexpected exception. Don't leak raw
            // exception text into the UI — return null so the caller uses
            // its localized fallback string.
            _logger.Warn($"node.pair.remove failed: {ex.Message}");
            return new NodeForgetResult(false, ErrorMessage: null);
        }
    }

    /// <summary>
    /// Renames the display name of a paired node. Awaits the gateway's
    /// response, so callers can rely on <see cref="NodeRenameResult.Success"/>
    /// before refreshing UI state. The gateway does not broadcast a rename,
    /// so callers should follow a successful rename with
    /// <see cref="RequestNodesAsync"/> to pick up the new value.
    /// </summary>
    public async Task<NodeRenameResult> NodeRenameAsync(string nodeId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return new NodeRenameResult(false, ErrorMessage: "nodeId required");
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            return new NodeRenameResult(false, ErrorMessage: "displayName required");
        if (!IsConnected)
            return new NodeRenameResult(false, ErrorMessage: "Gateway connection is not open");

        try
        {
            var response = await SendWizardRequestAsync(
                "node.rename",
                new { nodeId, displayName = trimmed });

            var returnedNodeId = response.ValueKind == JsonValueKind.Object &&
                                 response.TryGetProperty("nodeId", out var idEl)
                ? idEl.GetString()
                : nodeId;
            var returnedDisplayName = response.ValueKind == JsonValueKind.Object &&
                                      response.TryGetProperty("displayName", out var nameEl)
                ? nameEl.GetString() ?? trimmed
                : trimmed;
            return new NodeRenameResult(true, returnedNodeId, returnedDisplayName);
        }
        catch (InvalidOperationException ex)
        {
            // Gateway business error (e.g. "missing scope: operator.pairing",
            // "unknown nodeId"). Surface this verbatim so the user sees an
            // actionable message.
            _logger.Warn($"node.rename rejected: {ex.Message}");
            return new NodeRenameResult(false, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            // Transport / timeout / unexpected exception. Don't leak raw
            // exception text into the UI — return null so the caller uses
            // its localized fallback string.
            _logger.Warn($"node.rename failed: {ex.Message}");
            return new NodeRenameResult(false, ErrorMessage: null);
        }
    }

    public virtual async Task RequestDevicePairListAsync()
    {
        if (_devicePairListUnsupported) return;
        await SendTrackedRequestAsync("device.pair.list");
    }

    public virtual Task<bool> DevicePairApproveAsync(string requestId) =>
        ApprovePairingRequestAsync("device.pair.approve", requestId, RequestDevicePairListAsync);

    private async Task<bool> ApprovePairingRequestAsync(
        string method,
        string requestId,
        Func<Task> refreshPairListAsync)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return false;
        if (!IsConnected)
            return false;

        try
        {
            await SendWizardRequestAsync(method, new { requestId }, timeoutMs: 12000);
            _ = refreshPairListAsync();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warn($"{method} rejected: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{method} failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> DevicePairRejectAsync(string requestId)
    {
        return TrySendTrackedRequestAsync("device.pair.reject", new { requestId });
    }

    /// <summary>
    /// Start a channel. Sends <c>channels.start { channel }</c> — the gateway's
    /// canonical wire method per <c>src/gateway/server-methods-list.ts:21</c>.
    /// (Note: previously this sent <c>channel.start</c> singular which the gateway
    /// rejects as an unknown method; that was a latent bug.) Returns true when
    /// the gateway acknowledges the start, false on any failure. For the rich
    /// error payload — including "unknown channel" which means the channel
    /// plugin isn't installed on the gateway host — use
    /// <see cref="StartChannelDetailedAsync"/>.
    /// </summary>
    public async Task<bool> StartChannelAsync(string channelName)
    {
        var result = await StartChannelDetailedAsync(channelName);
        return result != null && result.Ok && result.Started;
    }

    /// <summary>
    /// Start a channel via <c>channels.start</c> and return the full gateway
    /// response (including error message + raw JSON). The page uses this to
    /// distinguish "channel started" from "plugin not loaded on gateway" so
    /// the user gets accurate guidance instead of a generic failure.
    /// </summary>
    public async Task<ChannelStartResult?> StartChannelDetailedAsync(string channelName, int timeoutMs = 12000)
    {
        if (!IsConnected) return null;
        try
        {
            var response = await SendWizardRequestAsync(
                "channels.start",
                new { channel = channelName },
                timeoutMs);
            var raw = response.GetRawText();
            string? acctId = null;
            string? channel = null;
            bool started = false;
            if (response.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (response.TryGetProperty("channel", out var ch) && ch.ValueKind == System.Text.Json.JsonValueKind.String)
                    channel = ch.GetString();
                if (response.TryGetProperty("accountId", out var aid) && aid.ValueKind == System.Text.Json.JsonValueKind.String)
                    acctId = aid.GetString();
                if (response.TryGetProperty("started", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.True)
                    started = true;
            }
            _logger.Info($"channels.start {channelName} → started={started}");
            return new ChannelStartResult
            {
                Channel = channel ?? channelName,
                AccountId = acctId,
                Started = started,
                Ok = true,
                RawResponse = raw,
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"channels.start {channelName} failed: {ex.Message}");
            return new ChannelStartResult
            {
                Channel = channelName,
                Started = false,
                Ok = false,
                Error = ex.Message,
                RawResponse = ex.ToString(),
            };
        }
    }

    /// <summary>Stop a channel. Sends <c>channels.stop { channel }</c>.</summary>
    public async Task<bool> StopChannelAsync(string channelName)
    {
        if (!IsConnected) return false;
        try
        {
            await SendWizardRequestAsync("channels.stop", new { channel = channelName }, 12000);
            _logger.Info($"channels.stop {channelName} succeeded");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"channels.stop {channelName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetch the rich channels.status snapshot — the canonical channel-status API
    /// used by macOS and the web UI. Returns null on failure.
    /// The <paramref name="timeoutMs"/> is propagated to the gateway so slow
    /// environments can extend the probe budget without recompiling.
    /// </summary>
    public async Task<ChannelsStatusSnapshot?> GetChannelsStatusAsync(bool probe = false, int timeoutMs = 12000)
    {
        if (!IsConnected) return null;
        try
        {
            // Pass the caller's timeoutMs through to the gateway. We give the
            // request envelope a slightly larger overall budget than the probe
            // budget so a slow but successful probe still returns in time.
            var probeTimeoutMs = Math.Max(1000, timeoutMs - 2000);
            var response = await SendWizardRequestAsync(
                "channels.status",
                new { probe, timeoutMs = probeTimeoutMs },
                timeoutMs);
            return ChannelsStatusParser.Parse(response);
        }
        catch (Exception ex)
        {
            _logger.Warn($"channels.status request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Log out / unlink a channel. Sends <c>channels.logout { channel }</c>.</summary>
    public async Task<bool> LogoutChannelAsync(string channelName, int timeoutMs = 12000)
    {
        if (!IsConnected) return false;
        try
        {
            await SendWizardRequestAsync("channels.logout", new { channel = channelName }, timeoutMs);
            _logger.Info($"channels.logout {channelName} succeeded");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"channels.logout {channelName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Begin a web/QR linking flow for the current default linking channel.</summary>
    public async Task<WebLoginStartResult?> WebLoginStartAsync(bool force = false, int timeoutMs = 30000)
    {
        if (!IsConnected) return null;
        try
        {
            var response = await SendWizardRequestAsync(
                "web.login.start",
                new { force, timeoutMs },
                timeoutMs + 5000);
            return new WebLoginStartResult
            {
                Message = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                QrDataUrl = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("qrDataUrl", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null,
                Connected = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                RawResponse = response.ValueKind != JsonValueKind.Undefined ? response.GetRawText() : null,
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"web.login.start failed: {ex.Message}");
            // Return a populated result with the error so the UI can surface
            // it in the diagnostic disclosure. Returning null would lose the
            // gateway's actual reason for failing.
            //
            // Use ex.Message (the clean, gateway-relayed reason — e.g.
            // "web login provider is not available") rather than ex.ToString():
            // the latter leaks our own .NET stack trace with internal CI build
            // paths into user-facing UI (issue #957). The message is the signal
            // the page pattern-matches on and shows to the user.
            return new WebLoginStartResult
            {
                Error = ex.Message,
                RawResponse = ex.Message,
            };
        }
    }

    /// <summary>Long-poll for QR linking completion.</summary>
    public async Task<WebLoginWaitResult?> WebLoginWaitAsync(string? currentQrDataUrl = null, int timeoutMs = 30000)
    {
        if (!IsConnected) return null;
        try
        {
            var response = await SendWizardRequestAsync(
                "web.login.wait",
                new { currentQrDataUrl, timeoutMs },
                timeoutMs + 5000);
            return new WebLoginWaitResult
            {
                Message = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                QrDataUrl = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("qrDataUrl", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null,
                Connected = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True,
                RawResponse = response.ValueKind != JsonValueKind.Undefined ? response.GetRawText() : null,
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"web.login.wait failed: {ex.Message}");
            // ex.Message, not ex.ToString(): keep the clean gateway reason and
            // avoid leaking an internal .NET stack trace into the UI (issue #957).
            return new WebLoginWaitResult
            {
                Error = ex.Message,
                RawResponse = ex.Message,
            };
        }
    }

    private async Task<bool> SendConnectMessageAsync(
        string? nonce,
        long connectionGeneration,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();
        var pending = IsConnected
            ? _pendingRequests.RegisterTracked(requestId, "connect")
            : null;
        var role = GetConnectRole();
        var requestedScopes = GetRequestedScopes(role);
        var credential = SelectConnectCredential();
        var appVersion = AppVersionInfo.Version;
        var envelope = ConnectEnvelopeBuilder.PrepareOperator(
            new OperatorConnectEnvelopeOptions(
                requestId,
                appVersion,
                role,
                requestedScopes,
                credential,
                nonce,
                _challengeTimestampMs,
                _useV2Signature),
            _connectEnvelopeSigner);
        var signedAt = envelope.SignedAt;
        var connectNonce = envelope.Nonce!;
        var signatureToken = credential.Value;

        // Log complete handshake details for diagnostics
        _logger.Info($"[HANDSHAKE] → Sending connect:");
        _logger.Info($"  role={role}, clientId={OperatorClientId}, mode={OperatorClientMode}");
        _logger.Info($"  scopes=[{string.Join(", ", requestedScopes)}]");
        _logger.Info($"  isBootstrap={_tokenIsBootstrapToken}, hasDeviceToken={!string.IsNullOrEmpty(_deviceIdentity.DeviceToken)}");
        _logger.Info($"  deviceId={_deviceIdentity.DeviceId[..Math.Min(16, _deviceIdentity.DeviceId.Length)]}...");
        _logger.Info($"  nonce={(!string.IsNullOrEmpty(connectNonce) ? connectNonce[..Math.Min(12, connectNonce.Length)] + "..." : "(empty)")}");
        _logger.Info($"  signedAt={signedAt}");
        _logger.Info($"  sigToken(len)={signatureToken.Length}, preview=[REDACTED]");
        _logger.Info($"  signature format={(_useV2Signature ? "v2" : "v3")}, platform={WindowsClientMetadata.Platform}, family={WindowsClientMetadata.DeviceFamily}");

        var signedPayload = envelope.SigningArguments.BuildPayload(_deviceIdentity);
        _logger.Info($"[HANDSHAKE] signed: {TokenSanitizer.Sanitize(signedPayload)}");

        // Also log what auth field we're sending
        var authObj = BuildAuthPayload();
        var authJson = JsonSerializer.Serialize(authObj);
        _logger.Info($"[HANDSHAKE] auth: {RedactAuthPayload(authJson)}");

        // Try v3 first (matches reference client). Fall back to v2 if gateway rejects v3.
        var signature = envelope.Sign();

        try
        {
            var sent = await SendRawAsync(
                    envelope.Serialize(signature),
                    connectionGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sent && pending is not null)
                _pendingRequests.TryRemove(pending);
            return sent;
        }
        catch
        {
            if (pending is not null)
            {
                _pendingRequests.TryRemove(pending);
            }
            throw;
        }
    }

    private string GetConnectRole()
    {
        return _bootstrapPairAsNode && _tokenIsBootstrapToken && !HasUsableOperatorDeviceToken
            ? "node"
            : OperatorRole;
    }

    private string[] GetRequestedScopes(string role)
    {
        if (role == "node")
            return [];

        if (!HasUsableOperatorDeviceToken)
        {
            // Shared gateway token (non-bootstrap) → request admin scope.
            // Bootstrap tokens get bounded scopes.
            if (!_tokenIsBootstrapToken)
                return s_operatorScopes;

            return s_operatorBootstrapScopes;
        }

        return _deviceIdentity.DeviceTokenScopes is { Count: > 0 } scopes
            ? scopes.ToArray()
            : s_operatorScopes;
    }

    /// <summary>
    /// Builds the auth payload for the connect handshake, matching the gateway's
    /// HandshakeConnectAuth type: { token?, bootstrapToken?, deviceToken?, password? }.
    /// Fresh devices send bootstrapToken for initial QR/setup-code pairing.
    /// Paired devices send an explicit deviceToken.
    /// </summary>
    private Dictionary<string, string> BuildAuthPayload()
    {
        return SelectConnectCredential().ToAuthPayload();
    }

    private ConnectCredential SelectConnectCredential()
    {
        if (HasUsableOperatorDeviceToken)
            return new DeviceTokenConnectCredential(_deviceIdentity.DeviceToken!);

        if (_tokenIsBootstrapToken)
        {
            // Fresh QR/setup-code device: do not also send auth.token, which upstream treats
            // as an explicit gateway token and therefore suppresses bootstrap pairing.
            return new BootstrapTokenConnectCredential(_token);
        }

        return new TokenConnectCredential(_connectAuthToken);
    }

    private bool HasUsableOperatorDeviceToken =>
        !_ignoreStoredDeviceToken && !string.IsNullOrEmpty(_deviceIdentity.DeviceToken);

    private async Task SendTrackedRequestAsync(string method, object? parameters = null)
    {
        if (!IsConnected) return;

        var requestId = Guid.NewGuid().ToString();
        var pending = _pendingRequests.RegisterTracked(requestId, method);
        try
        {
            await SendRawAsync(SerializeRequest(requestId, method, parameters));
        }
        catch
        {
            _pendingRequests.TryRemove(pending);
            throw;
        }
    }

    private async Task<bool> TrySendTrackedRequestAsync(string method, object? parameters = null)
    {
        try
        {
            await SendTrackedRequestAsync(method, parameters);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{method} request failed: {ex.Message}");
            return false;
        }
    }

    private async Task RequestLegacyUsageAsync()
    {
        try
        {
            await SendTrackedRequestAsync("usage");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Legacy usage request failed: {ex.Message}");
        }
    }

    private static string SerializeRequest(string requestId, string method, object? parameters)
    {
        if (parameters is null)
        {
            return JsonSerializer.Serialize(new { type = "req", id = requestId, method });
        }
        return JsonSerializer.Serialize(new { type = "req", id = requestId, method, @params = parameters });
    }

    // --- Message processing ---

    private void ProcessMessage(string json) =>
        ProcessMessageForConnection(json, CurrentConnectionGeneration);

    private void ProcessMessageForConnection(
        string json,
        long sourceConnectionGeneration)
    {
        if (!IsCurrentConnectionGeneration(sourceConnectionGeneration))
            return;

        if (_protocolMismatch)
        {
            _logger.Warn("[HANDSHAKE] Ignoring message after terminal protocol mismatch");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "res":
                    HandleResponseForConnection(root, sourceConnectionGeneration);
                    break;
                case "event":
                    HandleEventForConnection(
                        root,
                        json.Length,
                        sourceConnectionGeneration);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.Warn($"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Error("Message processing error", ex);
        }
    }

    private void HandleResponseForConnection(
        JsonElement root,
        long sourceConnectionGeneration)
    {
        string? requestId = null;
        if (root.TryGetProperty("id", out var idProp))
        {
            requestId = idProp.GetString();
        }

        _pendingRequests.TryTake(requestId, out var pending);
        var requestMethod = pending?.Method;

        switch (pending?.Category)
        {
            case PendingRequestCategory.ChatSend:
            {
                var pendingChatSend = (ChatSendRequestResolution)pending;
                if (root.TryGetProperty("ok", out var okChatProp) &&
                    okChatProp.ValueKind == JsonValueKind.False)
                {
                    var message = TryGetErrorMessage(root) ?? "request failed";
                    _logger.Warn($"chat.send failed: {message}");
                    pendingChatSend.TryFault(new InvalidOperationException(message));
                    return;
                }

                pendingChatSend.TryComplete(ParseChatSendResult(root));
                return;
            }

            case PendingRequestCategory.Wizard:
            {
                var wizardCompletion = (WizardRequestResolution)pending;
                if (root.TryGetProperty("ok", out var okWiz) &&
                    okWiz.ValueKind == JsonValueKind.False)
                {
                    wizardCompletion.TryFault(
                        GatewayRequestException.FromResponse(
                            wizardCompletion.Method,
                            root,
                            "wizard request failed"));
                }
                else if (root.TryGetProperty("payload", out var wizPayload))
                {
                    // HIGH: never log the wizard payload body. It can include
                    // prompts, tool args, and chat content. Log shape only.
                    var wizardPayloadLen = wizPayload.ValueKind == JsonValueKind.Undefined
                        ? 0
                        : wizPayload.GetRawText().Length;
                    _logger.Info($"Wizard response payload kind={wizPayload.ValueKind} len={wizardPayloadLen}");
                    wizardCompletion.TryComplete(wizPayload.Clone());
                }
                else
                {
                    wizardCompletion.TryComplete(root.Clone());
                }
                return;
            }

            case PendingRequestCategory.Approval:
            {
                var approvalCompletion = (ApprovalRequestResolution)pending;
                if (root.TryGetProperty("ok", out var okAppr) &&
                    okAppr.ValueKind == JsonValueKind.False)
                {
                    var message = TryGetErrorMessage(root) ?? "exec.approval.resolve rejected";
                    _logger.Warn($"exec.approval.resolve rejected by gateway: {message}");
                    approvalCompletion.TryFault(new InvalidOperationException(message));
                }
                else
                {
                    approvalCompletion.TryComplete(true);
                }
                return;
            }
        }

        if (root.TryGetProperty("ok", out var okProp) &&
            okProp.ValueKind == JsonValueKind.False)
        {
            HandleRequestError(requestMethod, root);
            return;
        }

        if (!root.TryGetProperty("payload", out var payload))
        {
            if (string.Equals(requestMethod, "connect", StringComparison.Ordinal))
                HandleProtocolMismatch("connect success response has no payload");
            return;
        }

        var isHelloOk = GatewayProtocolContract.IsHelloOk(payload);
        if (isHelloOk &&
            HandshakeAuthorizationAsync is not null &&
            !string.Equals(requestMethod, "connect", StringComparison.Ordinal))
        {
            _logger.Warn("[HANDSHAKE] Ignoring uncorrelated hello-ok on guarded validation connection.");
            return;
        }

        if ((string.Equals(requestMethod, "connect", StringComparison.Ordinal) || isHelloOk) &&
            !GatewayProtocolContract.TryValidateHelloOk(payload, out var protocolError))
        {
            var compatibility = GatewayProtocolCompatibility.FromGatewayExpectation(
                GatewayProtocolContract.TryGetProtocol(payload, out var protocol) ? protocol : null);
            HandleProtocolMismatch(protocolError, compatibility);
            return;
        }

        if (!string.IsNullOrEmpty(requestMethod) && HandleKnownResponse(requestMethod!, payload))
        {
            return;
        }

        // Handle handshake acknowledgement payload.
        if (isHelloOk)
        {
            if (!IsCurrentConnectionGeneration(sourceConnectionGeneration) ||
                !_handshakeChallengeGate.IsAuthorized(sourceConnectionGeneration) ||
                !string.Equals(requestMethod, "connect", StringComparison.Ordinal))
            {
                _logger.Warn("[HANDSHAKE] Ignoring stale or uncorrelated hello-ok.");
                return;
            }

            _logger.Info($"[HANDSHAKE] Received hello-ok!");
            Volatile.Write(ref _pairingRequiredAwaitingApproval, false);
            Volatile.Write(ref _pairingRequiredRequestId, null);
            _authFailed = false;
            _protocolMismatch = false;
            _ = GatewayProtocolContract.TryGetProtocol(payload, out var acceptedProtocol);
            ProtocolCompatibilityChanged?.Invoke(
                this,
                GatewayProtocolCompatibility.Compatible(acceptedProtocol));
            ResetReconnectAttempts();
            _operatorDeviceId = TryGetHandshakeDeviceId(payload);
            _grantedOperatorScopes = TryGetHandshakeScopes(payload);
            CaptureAdvertisedFeatures(payload);
            // Write the key first, then publish the readiness flag. Pair with
            // Volatile.Read on the public getters so a reader observing
            // HasHandshakeSnapshot==true is guaranteed to see the populated
            // MainSessionKey (release/acquire ordering).
            var hasCanonicalMainSessionKey = TryGetCanonicalHandshakeMainSessionKey(
                payload,
                out var canonicalMainSessionKey);
            Volatile.Write(ref _mainSessionKeyIsCanonical, hasCanonicalMainSessionKey);
            Volatile.Write(
                ref _mainSessionKey,
                hasCanonicalMainSessionKey ? canonicalMainSessionKey : TryGetHandshakeMainSessionKey(payload));
            Volatile.Write(ref _hasHandshakeSnapshot, true);
            _logger.Info($"[HANDSHAKE] deviceId={_operatorDeviceId}, scopes=[{string.Join(", ", _grantedOperatorScopes)}], mainSession={_mainSessionKey ?? "(unset)"}");
            PublishGatewaySelf(GatewaySelfInfo.FromHelloOk(payload));

            var persistedRoleTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var roleToken in EnumerateHandshakeDeviceTokens(payload))
            {
                var stored = TryStoreHandshakeDeviceToken(roleToken.Role, roleToken.Token, roleToken.Scopes);
                persistedRoleTokens.Add(roleToken.Role);
                if (stored && roleToken.Role.Equals("node", StringComparison.OrdinalIgnoreCase))
                    _logger.Info("Node device token stored for Windows tray node reconnect");
                else if (stored)
                    _logger.Info($"{roleToken.Role} device token stored for reconnect");
                DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(roleToken.Token, roleToken.Scopes, roleToken.Role));
            }

            if (_bootstrapPairAsNode && !persistedRoleTokens.Contains("node"))
            {
                var nodeDeviceToken = TryGetHandshakeDeviceTokenCore(payload, "node", allowDirectDeviceTokenFallback: true);
                if (!string.IsNullOrWhiteSpace(nodeDeviceToken))
                {
                    var nodeDeviceTokenScopes = TryGetHandshakeDeviceTokenScopesCore(payload, "node", allowDirectDeviceTokenFallback: true);
                    var stored = TryStoreHandshakeDeviceToken("node", nodeDeviceToken, nodeDeviceTokenScopes);
                    persistedRoleTokens.Add("node");
                    if (stored)
                        _logger.Info("Node device token stored for Windows tray node reconnect");
                    DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(nodeDeviceToken, nodeDeviceTokenScopes, "node"));
                }
            }

            var newDeviceToken = !_bootstrapPairAsNode
                ? (persistedRoleTokens.Contains(OperatorRole) ? null : TryGetHandshakeDeviceTokenCore(payload, preferredRole: null))
                : TryGetHandshakeDeviceTokenCore(payload, OperatorRole, allowDirectDeviceTokenFallback: false);
            if (!string.IsNullOrWhiteSpace(newDeviceToken))
            {
                var deviceTokenScopes = _bootstrapPairAsNode
                    ? TryGetHandshakeDeviceTokenScopesCore(payload, OperatorRole, allowDirectDeviceTokenFallback: false)
                    : TryGetHandshakeDeviceTokenScopesCore(payload, preferredRole: null);
                var stored = TryStoreHandshakeDeviceToken(OperatorRole, newDeviceToken, deviceTokenScopes);
                _connectAuthToken = newDeviceToken;
                if (stored)
                    _logger.Info("Operator device token stored for reconnect");
                DeviceTokenReceived?.Invoke(this, new DeviceTokenReceivedEventArgs(newDeviceToken, deviceTokenScopes, "operator"));
            }

            _logger.Info("Handshake complete (hello-ok)");
            HandshakeSucceeded?.Invoke(this, EventArgs.Empty);
            if (!string.IsNullOrWhiteSpace(_operatorDeviceId))
            {
                _logger.Info($"Operator device ID: {_operatorDeviceId}");
            }
            if (_grantedOperatorScopes.Length > 0)
            {
                _logger.Info($"Granted operator scopes: {string.Join(", ", _grantedOperatorScopes)}");
            }
            _logger.Info($"Main session key: {_mainSessionKey ?? "(unset)"}");

            // Extract presence from snapshot
            TryParsePresence(payload);

            RaiseStatusChanged(ConnectionStatus.Connected);

            // Request initial state after handshake
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await CheckHealthAsync();
                await RequestSessionsAsync();
                await SubscribeSessionEventsAsync();
                await RequestUsageAsync();
                await RequestNodesAsync();
                await RequestAgentsListAsync();
            });
        }

        // Handle health response — channels
        if (payload.TryGetProperty("channels", out var channels))
        {
            PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(payload));
            ParseChannelHealth(channels);
        }

        // Handle sessions response
        if (payload.TryGetProperty("sessions", out var sessions))
        {
            ParseSessions(sessions);
        }

        // Handle usage response
        if (payload.TryGetProperty("usage", out var usage))
        {
            ParseUsage(usage);
        }

        if (payload.TryGetProperty("nodes", out var nodes))
        {
            ParseNodeList(nodes);
        }
    }

    private bool TryStoreHandshakeDeviceToken(string role, string token, string[]? scopes)
    {
        if (!_persistHandshakeDeviceTokens)
            return false;

        try
        {
            _deviceIdentity.StoreDeviceTokenForRole(role, token, scopes);
            return true;
        }
        catch (DeviceIdentityLoadException ex)
        {
            _logger.Error(
                $"Failed to persist {role} device token during handshake; connection remains active: {ex.InnerException?.Message}");
            return false;
        }
    }

    private bool HandleKnownResponse(string method, JsonElement payload)
    {
        switch (method)
        {
            case "health":
                PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(payload));
                if (payload.TryGetProperty("channels", out var channels))
                    ParseChannelHealth(channels);
                return true;
            case "sessions.list":
                if (TryGetSessionsPayload(payload, out var sessionsPayload))
                    ParseSessions(sessionsPayload);
                return true;
            case "usage":
                ParseUsage(payload);
                return true;
            case "usage.status":
                ParseUsageStatus(payload);
                return true;
            case "usage.cost":
                ParseUsageCost(payload);
                return true;
            case "node.list":
                if (TryGetNodesPayload(payload, out var nodesPayload))
                    ParseNodeList(nodesPayload);
                return true;
            case "sessions.preview":
                ParseSessionsPreview(payload);
                return true;
            case "sessions.patch":
            case "sessions.reset":
            case "sessions.delete":
            case "sessions.compact":
                ParseSessionCommandResult(method, payload);
                return true;
            case "cron.list":
                CronListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "cron.status":
                CronStatusUpdated?.Invoke(this, payload.Clone());
                return true;
            case "cron.run":
            case "cron.remove":
            case "cron.add":
            case "cron.update":
                // After add/update/remove, refresh the list
                _ = RequestCronListAsync();
                return true;
            case "cron.runs":
                CronRunsUpdated?.Invoke(this, payload.Clone());
                return true;
            case "skills.status":
                SkillsStatusUpdated?.Invoke(this, payload.Clone());
                return true;
            case "skills.install":
            case "skills.update":
                // Re-fetch the same skills scope so filtered views do not jump back to all agents.
                _ = RequestSkillsStatusAsync(_lastSkillsStatusAgentId);
                return true;
            case "config.get":
                ConfigUpdated?.Invoke(this, payload.Clone());
                return true;
            case "config.schema":
                ConfigSchemaUpdated?.Invoke(this, payload.Clone());
                return true;
            case "config.set":
            case "config.patch":
                return true;
            case "agents.list":
                AgentsListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "agents.files.list":
                AgentFilesListUpdated?.Invoke(this, payload.Clone());
                return true;
            case "agents.files.get":
                AgentFileContentUpdated?.Invoke(this, payload.Clone());
                return true;
            case "models.list":
                ParseModelsList(payload);
                return true;
            case "node.pair.list":
                ParseNodePairList(payload);
                return true;
            case "node.pair.approve":
            case "node.pair.reject":
                _ = RequestNodePairListAsync();
                return true;
            case "device.pair.list":
                ParseDevicePairList(payload);
                return true;
            case "device.pair.approve":
            case "device.pair.reject":
                _ = RequestDevicePairListAsync();
                return true;
            default:
                return false;
        }
    }

    private static ChatSendResult ParseChatSendResult(JsonElement root)
    {
        string? runId = null;
        string? sessionKey = null;
        string? status = null;
        string? error = null;
        var cached = false;

        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("runId", out var runIdProp))
                runId = runIdProp.GetString();
            if (payload.TryGetProperty("sessionKey", out var sessionKeyProp))
                sessionKey = sessionKeyProp.GetString();
            if (payload.TryGetProperty("status", out var statusProp))
                status = statusProp.GetString();
            if (payload.TryGetProperty("error", out var payloadErrorProp))
                error = TryReadJsonValueAsString(payloadErrorProp);
            else if (payload.TryGetProperty("message", out var payloadMessageProp) && ChatSendResult.IsFailureStatus(status))
                error = TryReadJsonValueAsString(payloadMessageProp);
        }

        if (root.TryGetProperty("status", out var rootStatusProp))
            status ??= rootStatusProp.GetString();
        if (root.TryGetProperty("error", out var rootErrorProp))
            error ??= TryReadJsonValueAsString(rootErrorProp);

        if (root.TryGetProperty("meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("cached", out var cachedProp) &&
            cachedProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            cached = cachedProp.GetBoolean();
        }

        return new ChatSendResult
        {
            RunId = runId,
            SessionKey = sessionKey,
            Status = status,
            Error = error,
            Cached = cached
        };
    }

    private static string? TryReadJsonValueAsString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Object when value.TryGetProperty("message", out var message) => TryReadJsonValueAsString(message),
            _ => null
        };

    private void HandleRequestError(string? method, JsonElement root)
    {
        var message = TryGetErrorMessage(root) ?? "request failed";
        var detailCode = method == "connect" ? TryGetErrorDetailCode(root) : null;
        var topLevelCode = method == "connect" ? TryGetErrorTopLevelCode(root) : null;

        if (string.IsNullOrEmpty(method))
        {
            _logger.Warn($"Gateway request failed: {message}");
            return;
        }

        if (method == "connect")
        {
            _logger.Warn($"[HANDSHAKE] Connect error from gateway: message=\"{message}\", detailCode={detailCode ?? "none"}");
            // Log raw JSON for debugging (truncated)
            var rawJson = root.ToString() ?? "";
            if (rawJson.Length > 500) rawJson = rawJson[..500] + "...";
            _logger.Info($"[HANDSHAKE] Raw error response: {rawJson}");
        }

        if (method == "connect" &&
            GatewayErrorClassifier.ClassifyWithCode(message, topLevelCode, detailCode) ==
                GatewayErrorKind.ProtocolMismatch)
        {
            HandleProtocolMismatch(message, GatewayProtocolContract.ParseMismatch(root));
            return;
        }

        if (method == "connect" && detailCode == "DEVICE_AUTH_SIGNATURE_EXPIRED")
        {
            _authFailed = true;
            RaiseAuthenticationFailed($"{detailCode}: {message}");
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        if (method == "connect" &&
            (message.Contains("device signature invalid", StringComparison.OrdinalIgnoreCase) ||
             detailCode == "DEVICE_AUTH_SIGNATURE_INVALID"))
        {
            if (!_useV2Signature)
            {
                // v3 rejected — set flag so next connect uses v2.
                // Don't retry on this socket — gateway closes it after rejection.
                // The auto-reconnect will use v2 on the fresh connection.
                _useV2Signature = true;
                _logger.Warn($"[HANDSHAKE] v3 signature rejected, will use v2 on reconnect");
                V2SignatureFallback?.Invoke(this, EventArgs.Empty);
                return;
            }
            // v2 also rejected — real auth error
            _logger.Warn($"[HANDSHAKE] v2 signature also rejected — wrong key or token. Raw: {message}");
            _authFailed = true;
            RaiseConnectionFailure(GatewayErrorKind.Auth);
            RaiseAuthenticationFailed($"device signature rejected — {message}");
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        var pairingDetails = TryGetPairingConnectErrorDetails(root);
        if (method == "connect" &&
            (pairingDetails.IsPairingRequired || message.Contains("pairing required", StringComparison.OrdinalIgnoreCase)))
        {
            Volatile.Write(ref _pairingRequiredRequestId, pairingDetails.RequestId);
            Volatile.Write(ref _pairingRequiredAwaitingApproval, true);
            _logger.Warn($"[HANDSHAKE] Pairing required (requestId={pairingDetails.RequestId}). Waiting for approval.");
            PairingRequired?.Invoke(this, pairingDetails.RequestId);
            return;
        }

        // Permanent auth failures — stop retrying and notify the app. Check BOTH the top-level
        // error.code and the structured error.details.code so a device-token mismatch delivered in
        // either place is recognized (the gateway may send the reason only as a code with a generic
        // message).
        if (method == "connect" &&
            (IsTerminalAuthError(message) || IsTerminalAuthDetailCode(detailCode) || IsTerminalAuthDetailCode(topLevelCode)))
        {
            _authFailed = true;
            var failureKind = GatewayErrorClassifier.ClassifyWithCode(message, topLevelCode, detailCode);
            RaiseConnectionFailure(failureKind);
            // Preserve a structured device-token mismatch in the raised string so the connection
            // manager can distinguish it (auto-recoverable) from a wrong shared token and other
            // terminal auth failures. Only the device-token code is enriched; other codes pass
            // through unchanged so they are never mistaken for a recoverable device-token drift.
            var isDeviceMismatch =
                string.Equals(detailCode, GatewayErrorClassifier.DeviceTokenMismatchCode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(topLevelCode, GatewayErrorClassifier.DeviceTokenMismatchCode, StringComparison.OrdinalIgnoreCase);
            var authMessage = isDeviceMismatch
                ? $"{GatewayErrorClassifier.DeviceTokenMismatchCode}: {message}"
                : message;
            RaiseAuthenticationFailed(authMessage);
            RaiseStatusChanged(ConnectionStatus.Error);
            return;
        }

        if (IsMissingScopeError(message, "operator.read") &&
            method is "sessions.list" or "usage.status" or "usage.cost" or "node.list")
        {
            if (!_operatorReadScopeUnavailable)
            {
                _logger.Warn("Gateway token lacks operator.read; disabling sessions/usage/nodes polling");
            }

            _operatorReadScopeUnavailable = true;
            return;
        }

        if (IsUnknownMethodError(message))
        {
            switch (method)
            {
                case "usage.status":
                    _usageStatusUnsupported = true;
                    _logger.Warn("usage.status unsupported on gateway; falling back to usage");
                    _ = RequestLegacyUsageAsync();
                    return;
                case "usage.cost":
                    _usageCostUnsupported = true;
                    _logger.Warn("usage.cost unsupported on gateway");
                    return;
                case "sessions.preview":
                    _sessionPreviewUnsupported = true;
                    _logger.Warn("sessions.preview unsupported on gateway");
                    return;
                case "node.list":
                    _nodeListUnsupported = true;
                    _logger.Warn("node.list unsupported on gateway");
                    return;
                case "models.list":
                    _modelsListUnsupported = true;
                    _logger.Warn("models.list unsupported on gateway");
                    return;
                case "node.pair.list":
                    _nodePairListUnsupported = true;
                    _logger.Warn("node.pair.list unsupported on gateway");
                    return;
                case "device.pair.list":
                    _devicePairListUnsupported = true;
                    _logger.Warn("device.pair.list unsupported on gateway");
                    return;
                case "agents.list":
                    _agentsListUnsupported = true;
                    _logger.Warn("agents.list unsupported on gateway");
                    return;
                case "agents.files.list":
                    _agentFilesListUnsupported = true;
                    _logger.Warn("agents.files.list unsupported on gateway");
                    return;
                case "agents.files.get":
                    _agentFileGetUnsupported = true;
                    _logger.Warn("agents.files.get unsupported on gateway");
                    return;
            }
        }

        if (IsSessionCommandMethod(method))
        {
            SessionCommandCompleted?.Invoke(this, new SessionCommandResult
            {
                Method = method,
                Ok = false,
                Error = message
            });
        }

        _logger.Warn($"{method} failed: {message}");
    }

    private void HandleProtocolMismatch(
        string detail,
        GatewayProtocolCompatibility? compatibility = null)
    {
        _protocolMismatch = true;
        AbortCurrentWebSocket(CurrentConnectionGeneration);
        _logger.Warn($"[HANDSHAKE] Gateway protocol mismatch: {TokenSanitizer.Sanitize(detail)}");
        ProtocolCompatibilityChanged?.Invoke(
            this,
            compatibility ?? GatewayProtocolCompatibility.FromGatewayExpectation(expectedProtocol: null));
        RaiseConnectionFailure(GatewayErrorKind.ProtocolMismatch);
        RaiseStatusChanged(ConnectionStatus.Error);
    }

    private static bool TryGetSessionsPayload(JsonElement payload, out JsonElement sessions)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("sessions", out sessions))
        {
            return true;
        }

        if (payload.ValueKind == JsonValueKind.Object || payload.ValueKind == JsonValueKind.Array)
        {
            sessions = payload;
            return true;
        }

        sessions = default;
        return false;
    }

    private static bool TryGetNodesPayload(JsonElement payload, out JsonElement nodes)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("nodes", out nodes))
        {
            return true;
        }

        if (payload.ValueKind == JsonValueKind.Array || payload.ValueKind == JsonValueKind.Object)
        {
            nodes = payload;
            return true;
        }

        nodes = default;
        return false;
    }

    private static readonly Regex AuthPayloadTokenPattern = new(
        @"""(token|deviceToken|bootstrapToken)""\s*:\s*""[^""]+""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string RedactAuthPayload(string authJson)
    {
        return AuthPayloadTokenPattern.Replace(
            authJson,
            m => $"\"{m.Groups[1].Value}\":\"[REDACTED]\"");
    }

    private static string? TryGetErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;
        if (error.ValueKind == JsonValueKind.String) return error.GetString();
        if (error.ValueKind != JsonValueKind.Object) return null;
        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        return null;
    }

    /// <summary>
    /// Extract the structured error detail code from the gateway error response.
    /// Checks error.details.code and error.data.details.code.
    /// </summary>
    private static string? TryGetErrorDetailCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetErrorDetailString(error, "code");
    }

    // Top-level error.code (distinct from the nested error.details.code). A gateway may deliver a
    // terminal-auth reason (e.g. AUTH_DEVICE_TOKEN_MISMATCH) here with a generic message.
    private static string? TryGetErrorTopLevelCode(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            return code.GetString();
        return null;
    }

    private static PairingConnectErrorDetails TryGetPairingConnectErrorDetails(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return default;

        var isPairingRequired = string.Equals(
            TryGetErrorDetailString(error, "code"),
            "PAIRING_REQUIRED",
            StringComparison.Ordinal);
        var requestId = TryGetSafeErrorDetailRequestId(error);
        return new PairingConnectErrorDetails(isPairingRequired, requestId);
    }

    private static string? TryGetErrorDetailString(JsonElement error, string property)
    {
        if (error.TryGetProperty("details", out var details) &&
            details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        if (error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("details", out details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    private static string? TryGetSafeErrorDetailRequestId(JsonElement error)
    {
        if (error.TryGetProperty("details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            var requestId = GetSafeRequestId(details, "requestId");
            if (requestId is not null)
                return requestId;
        }

        if (error.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("details", out details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            return GetSafeRequestId(details, "requestId");
        }

        return null;
    }

    private static string? GetSafeRequestId(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var requestId) || requestId.ValueKind != JsonValueKind.String)
            return null;

        var value = requestId.GetString()?.Trim();
        return value is not null && s_pairingRequestIdRegex.IsMatch(value) ? value : null;
    }

    private readonly record struct PairingConnectErrorDetails(bool IsPairingRequired, string? RequestId);

    private static bool IsUnknownMethodError(string errorMessage)
    {
        return errorMessage.Contains("unknown method", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAuthError(string errorMessage)
    {
        return errorMessage.Contains("token mismatch", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("origin not allowed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("too many failed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("bootstrap token invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalAuthDetailCode(string? code) => code is
        "AUTH_TOKEN_MISMATCH" or "AUTH_BOOTSTRAP_TOKEN_INVALID" or
        "AUTH_DEVICE_TOKEN_MISMATCH" or "AUTH_RATE_LIMITED" or
        "AUTH_TOKEN_NOT_CONFIGURED" or "DEVICE_AUTH_SIGNATURE_EXPIRED";

    private static bool IsMissingScopeError(string errorMessage, string scope)
    {
        if (string.IsNullOrWhiteSpace(errorMessage) || string.IsNullOrWhiteSpace(scope))
            return false;

        var expected = $"missing scope: {scope}";
        return errorMessage.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSessionCommandMethod(string method)
    {
        return method is "sessions.patch" or "sessions.reset" or "sessions.delete" or "sessions.compact";
    }

    private static string? TryGetHandshakeDeviceId(JsonElement payload)
    {
        if (payload.TryGetProperty("deviceId", out var deviceIdProp) &&
            deviceIdProp.ValueKind == JsonValueKind.String)
        {
            return deviceIdProp.GetString();
        }

        if (payload.TryGetProperty("device", out var deviceProp) &&
            deviceProp.ValueKind == JsonValueKind.Object)
        {
            if (deviceProp.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                return idProp.GetString();
            }

            if (deviceProp.TryGetProperty("deviceId", out var didProp) && didProp.ValueKind == JsonValueKind.String)
            {
                return didProp.GetString();
            }
        }

        return null;
    }

    private static string[] TryGetHandshakeScopes(JsonElement payload)
    {
        if (payload.TryGetProperty("auth", out var authPayload) &&
            authPayload.ValueKind == JsonValueKind.Object &&
            authPayload.TryGetProperty("scopes", out var authScopes) &&
            authScopes.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(authScopes);
        }

        if (payload.TryGetProperty("scopes", out var scopesProp) &&
            scopesProp.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(scopesProp);
        }

        return [];
    }

    private static string[] ReadStringArray(JsonElement array)
    {
        var buffer = new string[array.GetArrayLength()];
        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    buffer[count++] = value;
            }
        }

        return buffer[..count];
    }

    /// <summary>
    /// Resolves the effective <c>sessionKey</c> for a chat-related RPC,
    /// preferring a non-empty caller-supplied value over the handshake
    /// <paramref name="resolvedMainSessionKey"/>. Throws
    /// <see cref="InvalidOperationException"/> if neither is usable —
    /// callers MUST NOT fall back to a literal like <c>"main"</c>, which
    /// can drift from the canonical key the gateway echoes back.
    /// </summary>
    /// <remarks>
    /// Extracted as an internal static so unit tests can exercise the
    /// pre-handshake throw without needing a live WebSocket — see
    /// <c>OpenClawGatewayClientSessionKeyTests</c>.
    /// </remarks>
    internal static string ResolveEffectiveSessionKey(
        string? callerSessionKey, string? resolvedMainSessionKey, string operationName)
    {
        var effective = string.IsNullOrWhiteSpace(callerSessionKey)
            ? resolvedMainSessionKey
            : callerSessionKey.Trim();
        if (string.IsNullOrWhiteSpace(effective))
            throw new InvalidOperationException(
                $"{operationName} requires a sessionKey, but the gateway handshake has not resolved one yet.");
        return effective;
    }

    private static string? TryGetHandshakeMainSessionKey(JsonElement payload)
    {
        if (!payload.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!snapshot.TryGetProperty("sessionDefaults", out var sessionDefaults) || sessionDefaults.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Prefer the canonical "mainSessionKey" (e.g. "agent:main:main") over
        // the legacy alias "mainKey" (e.g. "main"). The gateway accepts both
        // for chat.send routing, but the chat/session events it emits back
        // are keyed by the canonical form. Using the alias here would cause
        // the tray's local timeline (keyed by the alias) to diverge from the
        // gateway's echo (keyed by canonical), stranding optimistic state.
        if (TryGetCanonicalHandshakeMainSessionKey(payload, out var canonicalValue))
            return canonicalValue;

        if (sessionDefaults.TryGetProperty("mainKey", out var mainKey) &&
            mainKey.ValueKind == JsonValueKind.String)
        {
            var value = mainKey.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool TryGetCanonicalHandshakeMainSessionKey(JsonElement payload, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty("snapshot", out var snapshot)
            || snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("sessionDefaults", out var sessionDefaults)
            || sessionDefaults.ValueKind != JsonValueKind.Object
            || !sessionDefaults.TryGetProperty("mainSessionKey", out var canonical)
            || canonical.ValueKind != JsonValueKind.String)
            return false;

        value = canonical.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? TryGetHandshakeDeviceToken(JsonElement payload)
    {
        return TryGetHandshakeDeviceTokenCore(payload, preferredRole: null);
    }

    private static IEnumerable<(string Role, string Token, string[]? Scopes)> EnumerateHandshakeDeviceTokens(JsonElement payload)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
            yield break;

        if (!authPayload.TryGetProperty("deviceTokens", out var deviceTokens) ||
            deviceTokens.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in deviceTokens.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.TryGetProperty("role", out var roleElement) ||
                roleElement.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("deviceToken", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var role = roleElement.GetString();
            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(role) ||
                string.IsNullOrWhiteSpace(token) ||
                !IsSupportedDeviceTokenRole(role))
            {
                continue;
            }

            var scopes = entry.TryGetProperty("scopes", out var roleScopes) && roleScopes.ValueKind == JsonValueKind.Array
                ? ReadStringArray(roleScopes)
                : [];
            yield return (role, token, scopes);
        }
    }

    private static bool IsSupportedDeviceTokenRole(string role) =>
        role.Equals(OperatorRole, StringComparison.OrdinalIgnoreCase) ||
        role.Equals("node", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetHandshakeDeviceTokenCore(JsonElement payload, string? preferredRole)
    {
        return TryGetHandshakeDeviceTokenCore(payload, preferredRole, allowDirectDeviceTokenFallback: true);
    }

    private static string? TryGetHandshakeDeviceTokenCore(JsonElement payload, string? preferredRole, bool allowDirectDeviceTokenFallback)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredRole))
        {
            if (authPayload.TryGetProperty("deviceTokens", out var deviceTokens) &&
                deviceTokens.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in deviceTokens.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    if (entry.TryGetProperty("role", out var role) &&
                        role.ValueKind == JsonValueKind.String &&
                        string.Equals(role.GetString(), preferredRole, StringComparison.OrdinalIgnoreCase) &&
                        entry.TryGetProperty("deviceToken", out var roleToken) &&
                        roleToken.ValueKind == JsonValueKind.String)
                    {
                        var roleTokenValue = roleToken.GetString();
                        if (!string.IsNullOrWhiteSpace(roleTokenValue))
                            return roleTokenValue;
                    }
                }
            }

            if (!allowDirectDeviceTokenFallback)
            {
                return null;
            }
        }

        if (!authPayload.TryGetProperty("deviceToken", out var deviceToken) || deviceToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = deviceToken.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string[]? TryGetHandshakeDeviceTokenScopesCore(JsonElement payload, string? preferredRole)
    {
        return TryGetHandshakeDeviceTokenScopesCore(payload, preferredRole, allowDirectDeviceTokenFallback: true);
    }

    private static string[]? TryGetHandshakeDeviceTokenScopesCore(JsonElement payload, string? preferredRole, bool allowDirectDeviceTokenFallback)
    {
        if (!payload.TryGetProperty("auth", out var authPayload) || authPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredRole))
        {
            if (authPayload.TryGetProperty("deviceTokens", out var deviceTokens) &&
                deviceTokens.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in deviceTokens.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    if (entry.TryGetProperty("role", out var role) &&
                        role.ValueKind == JsonValueKind.String &&
                        string.Equals(role.GetString(), preferredRole, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.TryGetProperty("scopes", out var roleScopes) && roleScopes.ValueKind == JsonValueKind.Array
                            ? ReadStringArray(roleScopes)
                            : [];
                    }
                }
            }

            if (!allowDirectDeviceTokenFallback)
            {
                return null;
            }
        }

        if (authPayload.TryGetProperty("deviceToken", out var deviceToken) &&
            deviceToken.ValueKind == JsonValueKind.String &&
            authPayload.TryGetProperty("scopes", out var scopes) &&
            scopes.ValueKind == JsonValueKind.Array)
        {
            return ReadStringArray(scopes);
        }

        return null;
    }

    private void HandleEventForConnection(
        JsonElement root,
        int rawMessageLength,
        long sourceConnectionGeneration)
    {
        if (!root.TryGetProperty("event", out var eventProp)) return;
        var eventType = eventProp.GetString();
        _logger.Info($"[EVENT] Received event: {eventType}");

        switch (eventType)
        {
            case "connect.challenge":
                HandleConnectChallengeForConnection(
                    root,
                    sourceConnectionGeneration);
                break;
            case "agent":
                HandleAgentEvent(root, rawMessageLength);
                break;
            case "health":
                if (root.TryGetProperty("payload", out var hp) &&
                    hp.TryGetProperty("channels", out var ch))
                {
                    PublishGatewaySelf(GatewaySelfInfo.FromHealthPayload(hp));
                    ParseChannelHealth(ch);
                }
                break;
            case "chat":
            case "session.message":
                HandleChatEvent(root, rawMessageLength);
                break;
            case "session":
                HandleSessionEvent(root);
                break;
            case "node.pair.requested":
            case "node.pair.resolved":
                // Refresh node pair list when pairing state changes. Also
                // refresh node.list because resolved decisions (in particular
                // "removed") drop the node from the gateway's known set, so
                // any UI mirroring node.list would otherwise show stale data
                // until the next poll.
                _ = RequestNodePairListAsync();
                _ = RequestNodesAsync();
                break;
            case "device.pair.requested":
            case "device.pair.resolved":
                // Refresh device pair list when pairing state changes
                _ = RequestDevicePairListAsync();
                break;
            case "presence":
                // Presence snapshot broadcast when clients connect/disconnect
                if (root.TryGetProperty("payload", out var presPayload))
                    TryParsePresenceFromBroadcast(presPayload);
                break;
            case "sessions.changed":
                // Gateway broadcasts this after session mutations (patch, send, etc.).
                // Re-request the full sessions list so we pick up model/thinking changes.
                _logger.Info("[EVENT] sessions.changed received — refreshing sessions list");
                _ = RequestSessionsAsync();
                break;
            case "cron":
                // Gateway pushes cron events when jobs run/change — refresh the list
                _ = RequestCronListAsync();
                _ = RequestCronStatusAsync();
                break;
            case "exec.approval.requested":
            case "exec.approval.resolved":
                // Gateway broadcasts approval lifecycle as TOP-LEVEL events,
                // but the chat data provider's approval rendering subscribes
                // to AgentEventReceived with Stream=="approval" (the only
                // path that reaches MapApprovalEvent and the terminal-phase
                // banner-clear fall-through in OnAgentEventReceived). Without
                // this translation the gateway HTML dashboard sees approvals
                // and surfaces an Allow/Deny modal, while the native chat
                // logs the event and silently drops it. Translate here so
                // every downstream consumer sees a uniform agent-stream
                // envelope. See OpenClawChatDataProvider.MapApprovalEvent.
                HandleExecApprovalEvent(root, eventType!);
                break;
        }
    }

    // Translate a top-level exec.approval.{requested,resolved} envelope into
    // a synthetic AgentEventInfo with Stream="approval" so the existing chat
    // approval-banner code path lights up unchanged. The wire payload does
    // not carry a "phase" field — the phase is encoded in the top-level event
    // name and (for resolved) the decision — so we always derive it here
    // rather than reading payload.phase. If a future protocol revision adds
    // an explicit payload.phase, this is the place to honor it.
    private void HandleExecApprovalEvent(JsonElement root, string topLevelEventType)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            _logger.Warn($"[Approval] {topLevelEventType} missing/invalid payload — dropping");
            return;
        }

        // Wire shape (top-level exec.approval.{requested,resolved}):
        //   { "id": "<uuid>",
        //     "request": { "command": "...", "host": "gateway",
        //                  "sessionKey": "agent:main:main", "agentId": "main", ... },
        //     ["decision": "allow-once" | "deny", "resolvedBy": "...", "ts": ...],
        //     ["createdAtMs": ..., "expiresAtMs": ...] }
        //
        // The chat data provider's MapApprovalEvent and terminal-phase handler
        // read flat fields named ``phase``, ``approvalId``, ``approvalSlug``,
        // ``host``, ``command``, ``title``, ``message``. Project the wire
        // shape into that flat form so the same code path that handles the
        // (older, agent-stream) approval format works here too.

        static string SafeStr(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "")
                : "";

        var approvalId = SafeStr(payload, "id");
        var decision = SafeStr(payload, "decision");

        string command = "";
        string host = "";
        string sessionKey = "";
        string agentId = "";
        if (payload.TryGetProperty("request", out var req) && req.ValueKind == JsonValueKind.Object)
        {
            command = SafeStr(req, "command");
            host = SafeStr(req, "host");
            sessionKey = SafeStr(req, "sessionKey");
            agentId = SafeStr(req, "agentId");
        }

        // Derive a chat-provider phase. "requested" stays as-is. For the
        // resolved envelope, map an exact ``deny`` decision to ``denied``
        // and any allow-* decision to ``resolved`` — both are accepted by
        // OpenClawChatDataProvider.IsTerminalApprovalPhase, so the banner
        // clears for remote allows (e.g. dashboard, plugin auto-approve) as
        // well as deny. NOTE: ``allowed`` is NOT in the terminal phase
        // allowlist; emitting it here would leak the banner. Use an
        // explicit allowlist instead of a loose ``StartsWith("allow")``
        // heuristic so future decision strings (typos, new variants) fall
        // through to the catch-all ``resolved`` with a warning, rather
        // than silently mis-classifying.
        string phase;
        if (topLevelEventType == "exec.approval.requested")
        {
            phase = "requested";
        }
        else if (string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase))
        {
            phase = "denied";
        }
        else if (string.Equals(decision, "allow-once", StringComparison.OrdinalIgnoreCase)
              || string.Equals(decision, "allow-always", StringComparison.OrdinalIgnoreCase))
        {
            phase = "resolved";
        }
        else
        {
            if (!string.IsNullOrEmpty(decision))
                _logger.Warn($"[Approval] {topLevelEventType} carries unknown decision '{decision}' — mapping to terminal phase='resolved'");
            phase = "resolved";
        }

        // Build the flat object the chat provider expects.
        var flat = new JsonObject
        {
            ["phase"] = phase,
            ["approvalId"] = approvalId,
            ["host"] = host,
            ["command"] = command,
            ["agentId"] = agentId,
        };
        if (!string.IsNullOrEmpty(decision))
            flat["decision"] = decision;

        // Clone into a JsonElement that owns its backing memory.
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(flat.ToJsonString());
            data = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Approval] Failed to serialize translated {topLevelEventType}: {ex.Message}");
            return;
        }

        var ts = payload.TryGetProperty("ts", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number
            ? tsProp.GetDouble() : 0;

        // The chat data provider rejects agent events with an empty
        // sessionKey. Wire payloads normally carry one inside ``request``;
        // fall back to the handshake-resolved main session key as a safety
        // net so process-wide approvals are still attributed to the active
        // chat thread.
        var fellBackToMain = false;
        if (string.IsNullOrEmpty(sessionKey))
        {
            var mainKey = MainSessionKey;
            if (!string.IsNullOrEmpty(mainKey))
            {
                sessionKey = mainKey!;
                fellBackToMain = true;
            }
            else
            {
                _logger.Warn($"[Approval] {topLevelEventType} missing sessionKey (request and MainSessionKey both empty); chat provider will drop the translated event.");
            }
        }

        var evt = new AgentEventInfo
        {
            RunId = "",
            Seq = 0,
            Stream = "approval",
            Ts = ts,
            Data = data,
            SessionKey = sessionKey,
            Summary = null,
        };

        _logger.Info($"[Approval] Translated {topLevelEventType} -> agent(stream=approval, phase={phase}, approvalId={approvalId}, sessionKey={(string.IsNullOrEmpty(sessionKey) ? "<empty>" : sessionKey)}{(fellBackToMain ? " [fallback=MainSessionKey]" : "")})");

        // Multicast invoke: snapshot the invocation list and dispatch each
        // subscriber under its own try/catch so a throwing handler does NOT
        // short-circuit subsequent ones. A single wrapping try/catch around
        // ``AgentEventReceived?.Invoke`` would catch the exception but skip
        // every handler after the throwing one — silently regressing the
        // very ``HTML dashboard sees it / native chat drops it`` bug this
        // translation exists to fix.
        var handler = AgentEventReceived;
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<AgentEventInfo>)d)(this, evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Approval] Subscriber {d.Method.DeclaringType?.Name}.{d.Method.Name} threw on translated {topLevelEventType}: {ex.Message}");
            }
        }
    }

    private void HandleConnectChallenge(JsonElement root) =>
        HandleConnectChallengeForConnection(root, CurrentConnectionGeneration);

    private void HandleConnectChallengeForConnection(
        JsonElement root,
        long sourceConnectionGeneration)
    {
        if (!IsCurrentConnectionGeneration(sourceConnectionGeneration))
            return;

        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            _logger.Warn("[HANDSHAKE] Ignoring malformed challenge without an object payload.");
            return;
        }

        string? nonce = null;
        if (payload.TryGetProperty("nonce", out var nonceProp))
        {
            if (nonceProp.ValueKind != JsonValueKind.String)
            {
                _logger.Warn("[HANDSHAKE] Ignoring malformed challenge with a non-string nonce.");
                return;
            }
            nonce = nonceProp.GetString();
        }
        var ts = ConnectAuthTimestamp.ReadChallengeTimestamp(payload);

        if (!_handshakeChallengeGate.TryBegin(sourceConnectionGeneration))
        {
            _logger.Warn("[HANDSHAKE] Ignoring duplicate challenge on the current socket.");
            return;
        }

        _challengeTimestampMs = ts;
        _currentChallengeNonce = nonce;
        
        _logger.Info($"[HANDSHAKE] Received connect.challenge: nonce={nonce}, ts={ts}");
        _ = SendConnectSafeAsync(nonce, sourceConnectionGeneration);
    }

    private async Task SendConnectSafeAsync(string? nonce, long connectionGeneration)
    {
        try
        {
            if (HandshakeAuthorizationAsync is not null)
            {
                var authorization = await HandshakeAuthorizationAsync(CancellationToken)
                    .ConfigureAwait(false);
                if (!IsCurrentConnectionGeneration(connectionGeneration))
                    return;
                if (!authorization.Allowed)
                {
                    if (!_handshakeChallengeGate.TryBlock(connectionGeneration))
                        return;
                    _logger.Warn(
                        $"[HANDSHAKE] Operator credential handoff blocked: {authorization.Detail}");
                    RaiseConnectionFailure(authorization.FailureKind);
                    if (authorization.FailureKind is GatewayErrorKind.Auth
                        or GatewayErrorKind.DeviceTokenMismatch
                        or GatewayErrorKind.TokenDrift)
                    {
                        RaiseAuthenticationFailed(
                            authorization.Detail ?? "Connection authorization failed.");
                    }
                    AbortCurrentWebSocket(connectionGeneration);
                    RaiseStatusChanged(ConnectionStatus.Error);
                    return;
                }
                if (!_handshakeChallengeGate.TryAuthorize(connectionGeneration))
                    return;
            }
            else if (!_handshakeChallengeGate.TryAuthorize(connectionGeneration))
            {
                return;
            }

            var sent = await SendConnectMessageAsync(
                    nonce,
                    connectionGeneration,
                    CancellationToken)
                .ConfigureAwait(false);
            if (!sent && IsCurrentConnectionGeneration(connectionGeneration))
                AbortCurrentWebSocket(connectionGeneration);
        }
        catch (OperationCanceledException) when (
            CancellationToken.IsCancellationRequested ||
            !IsCurrentConnectionGeneration(connectionGeneration))
        {
        }
        catch (Exception ex)
        {
            _logger.Error($"[HANDSHAKE] FATAL: SendConnectMessageAsync threw: {ex}");
            if (_handshakeChallengeGate.TryBlock(connectionGeneration))
            {
                RaiseConnectionFailure(GatewayErrorKind.Network);
                AbortCurrentWebSocket(connectionGeneration);
                RaiseStatusChanged(ConnectionStatus.Error);
            }
        }
    }

    private void HandleAgentEvent(JsonElement root, int rawMessageLength)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;

        // HIGH: never log raw agent event JSON — it can carry prompts,
        // tool args/outputs, and URLs. Log shape only (type + length).
        try
        {
            var streamHint = payload.TryGetProperty("stream", out var sh) ? sh.GetString() ?? "" : "";
            _logger.Debug($"Agent event received: stream={streamHint} len={rawMessageLength}");
            // For item events, also surface kind+phase metadata (no payload
            // content) so we can correlate which item kinds flow through.
            // Trace-level: useful only when investigating event-shape issues
            // and noisy under normal use.
            if (string.Equals(streamHint, "item", StringComparison.OrdinalIgnoreCase) &&
                payload.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Object)
            {
                var k = dataEl.TryGetProperty("kind", out var kp) ? kp.GetString() ?? "" : "";
                var ph = dataEl.TryGetProperty("phase", out var pp) ? pp.GetString() ?? "" : "";
                _logger.Trace($"Agent event item: kind={k} phase={ph}");
            }
        }
        catch (Exception ex)
        {
            // Shape extraction is best-effort diagnostic only; payload still
            // continues to downstream handlers. Log at Debug so a malformed
            // event shape is visible without escalating to user-visible.
            _logger.Debug($"[GatewayClient] Agent event shape extraction failed: {ex.GetType().Name}: {ex.Message}");
        }

        // sessionKey is inside payload, not root. We deliberately do NOT
        // substitute a fallback like "unknown" or "main" — empty must
        // propagate so the provider can drop the event and surface the
        // protocol gap, rather than silently routing into a synthetic bucket.
        var sessionKey = "";
        if (payload.TryGetProperty("sessionKey", out var sk))
            sessionKey = sk.GetString() ?? "";
        if (string.IsNullOrEmpty(sessionKey))
            _logger.Warn("[GatewayClient] Agent event missing sessionKey; will be dropped downstream.");
        var isMain = !string.IsNullOrEmpty(sessionKey)
            && (sessionKey == "main" || sessionKey.Contains(":main:"));

        // Emit raw agent event (cloned for thread safety)
        try
        {
            var evt = new AgentEventInfo
            {
                RunId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "",
                Seq = payload.TryGetProperty("seq", out var seqProp) && seqProp.ValueKind == JsonValueKind.Number ? seqProp.GetInt32() : 0,
                Stream = payload.TryGetProperty("stream", out var streamProp2) ? streamProp2.GetString() ?? "" : "",
                Ts = ExtractChatTimestampMs(payload),
                Data = payload.TryGetProperty("data", out var dataProp) ? dataProp.Clone() : default,
                SessionKey = sessionKey,
                Summary = payload.TryGetProperty("summary", out var sumProp) ? sumProp.GetString() : null
            };
            AgentEventReceived?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to emit agent event: {ex.Message}");
        }

        // Parse activity from stream field (existing behavior)
        if (payload.TryGetProperty("stream", out var streamProp))
        {
            var stream = streamProp.GetString();

            if (stream == "job")
            {
                HandleJobEvent(payload, sessionKey, isMain);
            }
            else if (stream == "tool")
            {
                HandleToolEvent(payload, sessionKey, isMain);
            }
        }

        // Check for notification content
        if (payload.TryGetProperty("content", out var content))
        {
            var text = content.GetString() ?? "";
            if (!string.IsNullOrEmpty(text))
            {
                EmitNotification(text);
            }
        }
    }

    private void HandleJobEvent(JsonElement payload, string sessionKey, bool isMain)
    {
        var state = "unknown";
        if (payload.TryGetProperty("data", out var data) &&
            data.TryGetProperty("state", out var stateProp))
            state = stateProp.GetString() ?? "unknown";

        var activity = new AgentActivity
        {
            SessionKey = sessionKey,
            IsMain = isMain,
            Kind = ActivityKind.Job,
            State = state,
            Label = $"Job: {state}"
        };

        if (state == "done" || state == "error")
            activity.Kind = ActivityKind.Idle;

        _logger.Info($"Agent activity: {activity.Label} (session: {sessionKey})");
        ActivityChanged?.Invoke(this, activity);

        // Update tracked session
        UpdateTrackedSession(sessionKey, isMain, state == "done" || state == "error" ? null : $"Job: {state}");
    }

    private void HandleToolEvent(JsonElement payload, string sessionKey, bool isMain)
    {
        var phase = "";
        var toolName = "";
        var label = "";

        if (payload.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("phase", out var phaseProp))
                phase = phaseProp.GetString() ?? "";
            if (data.TryGetProperty("name", out var nameProp))
                toolName = nameProp.GetString() ?? "";

            // Extract detail from args
            if (data.TryGetProperty("args", out var args))
            {
                if (args.TryGetProperty("command", out var cmd))
                {
                    // Avoid string[] allocation: find the first newline directly, then
                    // pass only that first-line slice (or the whole string) to TruncateText.
                    var cmdStr = cmd.GetString();
                    if (cmdStr != null)
                    {
                        var nl = cmdStr.IndexOf('\n');
                        label = MenuDisplayHelper.TruncateText(nl >= 0 ? cmdStr[..nl] : cmdStr, 60);
                    }
                }
                else if (args.TryGetProperty("path", out var path))
                    label = ShortenPath(path.GetString() ?? "");
                else if (args.TryGetProperty("file_path", out var filePath))
                    label = ShortenPath(filePath.GetString() ?? "");
                else if (args.TryGetProperty("query", out var query))
                    label = MenuDisplayHelper.TruncateText(query.GetString(), 60);
                else if (args.TryGetProperty("url", out var url))
                    label = MenuDisplayHelper.TruncateText(url.GetString(), 60);
            }
        }

        if (string.IsNullOrEmpty(label))
            label = toolName;

        var kind = ClassifyTool(toolName);

        // On tool result, briefly show then go idle
        if (phase == "result")
            kind = ActivityKind.Idle;

        var activity = new AgentActivity
        {
            SessionKey = sessionKey,
            IsMain = isMain,
            Kind = kind,
            State = phase,
            ToolName = toolName,
            Label = label
        };

        // HIGH: the activity Label may include user-provided values
        // (commands, queries, file paths, URLs from tool args). Log only
        // the tool name + phase — the label is for UI consumption.
        _logger.Info($"Tool: {toolName} ({phase})");
        ActivityChanged?.Invoke(this, activity);

        // Update tracked session
        if (kind != ActivityKind.Idle)
        {
            UpdateTrackedSession(sessionKey, isMain, $"{activity.Glyph} {label}");
        }
    }

    private void HandleChatEvent(JsonElement root, int rawMessageLength)
    {
        // HIGH 4: never log chat content. Log shape only — the raw payload
        // can include user prompts, assistant text, tool output, and even
        // bearer tokens routed through the gateway in some flows.
        _logger.Debug($"Chat event received: len={rawMessageLength}");
        
        if (!root.TryGetProperty("payload", out var payload)) return;
        EmitRawChatEvent(payload);

        // Extract sessionKey for the timeline-driving event. As with agent
        // events, do NOT substitute a fallback like "main" — empty must
        // propagate so the provider's empty-key drop policy can surface the
        // protocol gap instead of silently routing into a synthetic bucket.
        var sessionKey = "";
        if (payload.TryGetProperty("sessionKey", out var skProp))
            sessionKey = skProp.GetString() ?? "";
        if (string.IsNullOrEmpty(sessionKey))
            _logger.Warn("[GatewayClient] Chat event missing sessionKey; will be dropped downstream.");

        // Best-effort usage extraction — gateway emits this only on terminal
        // (state="final") events in practice; we still read it defensively
        // from common locations so any reasonable shape lights up the chat
        // footer pills.
        var (inTok, outTok, respTok, ctxPct) = ExtractChatUsage(payload);
        var tsMs = ExtractChatTimestampMs(payload);

        // Try new format: payload.message.role + payload.message.content[].text
        if (payload.TryGetProperty("message", out var message))
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                _logger.Warn("[GatewayClient] Chat event message payload was not an object; dropping frame.");
                return;
            }

            var role = message.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
            var state = payload.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
            if (tsMs == 0)
                tsMs = ExtractChatTimestampMs(message);

            // Usage block may also live on the inner ``message`` object.
            if (inTok is null && outTok is null && respTok is null && ctxPct is null)
                (inTok, outTok, respTok, ctxPct) = ExtractChatUsage(message);

            var text = ExtractMessageText(message, role);
            var toolContent = ExtractToolContent(message, role, text);
            var contentParts = ExtractOrderedMessageContent(message, role, toolContent);
            if (string.IsNullOrEmpty(text)
                && contentParts.All(static part => part.Kind != ChatMessageContentPartKind.Media))
            {
                return;
            }
            if (ChatMessageInfo.IsSilentAssistantDirective(role, text)) return;

            var messageOpenClawMetadata = ExtractOpenClawMetadata(message);
            var payloadOpenClawMetadata = ExtractOpenClawMetadata(payload);
            EmitChatMessageReceived(
                sessionKey,
                role,
                text,
                state,
                tsMs,
                inTok,
                outTok,
                respTok,
                ctxPct,
                messageOpenClawMetadata.Id ?? payloadOpenClawMetadata.Id,
                messageOpenClawMetadata.Seq ?? payloadOpenClawMetadata.Seq,
                messageOpenClawMetadata.Kind ?? payloadOpenClawMetadata.Kind,
                messageOpenClawMetadata.TokensBefore ?? payloadOpenClawMetadata.TokensBefore,
                messageOpenClawMetadata.TokensAfter ?? payloadOpenClawMetadata.TokensAfter,
                contentParts);

            if (role == "assistant" && string.Equals(state, "final", StringComparison.OrdinalIgnoreCase))
            {
                // HIGH 4: log shape only — content previously
                // surfaced in the operator log.
                _logger.Info($"Assistant response: role={role} state={state} len={text.Length}");
                if (!string.IsNullOrWhiteSpace(text))
                    EmitChatNotification(text, sessionKey);
            }
        }
        
        // Legacy format: payload.text + payload.role
        else if (payload.TryGetProperty("text", out var textProp))
        {
            var role = payload.TryGetProperty("role", out var roleProp) ? roleProp.GetString() ?? "" : "";
            var state = payload.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
            var projection = AssistantMediaDirectiveParser.Project(role, textProp.GetString());
            var text = projection.Text;

            if (!string.IsNullOrEmpty(text)
                || projection.ContentParts.Any(static part => part.Kind == ChatMessageContentPartKind.Media))
            {
                if (ChatMessageInfo.IsSilentAssistantDirective(role, text)) return;

                var openClawMetadata = ExtractOpenClawMetadata(payload);
                EmitChatMessageReceived(
                    sessionKey,
                    role,
                    text,
                    state,
                    tsMs,
                    inTok,
                    outTok,
                    respTok,
                    ctxPct,
                    openClawMetadata.Id,
                    openClawMetadata.Seq,
                    openClawMetadata.Kind,
                    openClawMetadata.TokensBefore,
                    openClawMetadata.TokensAfter,
                    projection.ContentParts);

                if (role == "assistant" &&
                    (string.IsNullOrWhiteSpace(state) ||
                     string.Equals(state, "final", StringComparison.OrdinalIgnoreCase)))
                {
                    // HIGH 4: log shape only.
                    _logger.Info($"Assistant response (legacy): role={role} state={state} len={text.Length}");
                    if (!string.IsNullOrWhiteSpace(text))
                        EmitChatNotification(text, sessionKey);
                }
            }
        }
    }

    /// <summary>
    /// Defensive extraction of a usage / token block from a chat event
    /// payload. Walks several known shapes: <c>usage.{input,output,total,
    /// inputTokens,outputTokens,totalTokens,promptTokens,completionTokens}</c>
    /// plus a few alternate top-level keys (<c>tokens</c>, <c>contextPercent</c>,
    /// <c>contextUsage</c>). Returns nulls when nothing matches; callers
    /// surface those as omitted footer pills.
    /// </summary>
    private static (int? input, int? output, int? response, int? contextPct)
        ExtractChatUsage(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return (null, null, null, null);

        int? input = null, output = null, response = null, ctx = null;

        static int? ReadInt(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var i) => i,
                JsonValueKind.Number => (int)v.GetDouble(),
                _ => null
            };
        }

        // Walk usage / tokens nested objects.
        foreach (var key in new[] { "usage", "tokens", "tokenUsage" })
        {
            if (!node.TryGetProperty(key, out var u) || u.ValueKind != JsonValueKind.Object)
                continue;
            input ??= ReadInt(u, "input") ?? ReadInt(u, "inputTokens") ?? ReadInt(u, "promptTokens");
            output ??= ReadInt(u, "output") ?? ReadInt(u, "outputTokens") ?? ReadInt(u, "completionTokens");
            response ??= ReadInt(u, "total") ?? ReadInt(u, "totalTokens") ?? ReadInt(u, "response") ?? ReadInt(u, "responseTokens");
            ctx ??= ReadInt(u, "contextPercent") ?? ReadInt(u, "context") ?? ReadInt(u, "ctxPercent");
        }

        // Top-level fallbacks.
        input ??= ReadInt(node, "inputTokens") ?? ReadInt(node, "promptTokens");
        output ??= ReadInt(node, "outputTokens") ?? ReadInt(node, "completionTokens");
        response ??= ReadInt(node, "totalTokens") ?? ReadInt(node, "responseTokens");
        ctx ??= ReadInt(node, "contextPercent") ?? ReadInt(node, "ctxPercent");

        // Synthesize total from input+output when only the parts are known.
        if (response is null && input is int inN && output is int outN)
            response = inN + outN;

        return (input, output, response, ctx);
    }

    private void EmitChatMessageReceived(
        string sessionKey,
        string role,
        string text,
        string? state,
        long tsMs,
        int? inputTokens = null,
        int? outputTokens = null,
        int? responseTokens = null,
        int? contextPct = null,
        string? openClawId = null,
        int? openClawSeq = null,
        string? openClawKind = null,
        long? compactionTokensBefore = null,
        long? compactionTokensAfter = null,
        IReadOnlyList<ChatMessageContentPartInfo>? contentParts = null)
    {
        if (ChatMessageInfo.IsSilentAssistantDirective(role, text))
            return;

        try
        {
            ChatMessageReceived?.Invoke(this, new ChatMessageInfo
            {
                SessionKey = sessionKey,
                Role = role,
                Text = text,
                ContentParts = contentParts ?? Array.Empty<ChatMessageContentPartInfo>(),
                State = state,
                Ts = tsMs,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ResponseTokens = responseTokens,
                ContextPercent = contextPct,
                OpenClawId = openClawId,
                OpenClawSeq = openClawSeq,
                OpenClawKind = openClawKind,
                CompactionTokensBefore = compactionTokensBefore,
                CompactionTokensAfter = compactionTokensAfter
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"ChatMessageReceived handler threw: {ex.Message}");
        }
    }

    private static (
        string? Id,
        int? Seq,
        string? Kind,
        long? TokensBefore,
        long? TokensAfter) ExtractOpenClawMetadata(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("__openclaw", out var openClaw) ||
            openClaw.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null, null);
        }

        var id = openClaw.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
        var seq = openClaw.TryGetProperty("seq", out var seqProp) &&
                  seqProp.ValueKind == JsonValueKind.Number &&
                  seqProp.TryGetInt32(out var parsedSeq)
            ? parsedSeq
            : (int?)null;
        var kind = openClaw.TryGetProperty("kind", out var kindProp) &&
                   kindProp.ValueKind == JsonValueKind.String
            ? kindProp.GetString()
            : null;
        var tokensBefore = openClaw.TryGetProperty("tokensBefore", out var beforeProp) &&
                           beforeProp.ValueKind == JsonValueKind.Number &&
                           beforeProp.TryGetInt64(out var parsedBefore)
            ? parsedBefore
            : (long?)null;
        var tokensAfter = openClaw.TryGetProperty("tokensAfter", out var afterProp) &&
                          afterProp.ValueKind == JsonValueKind.Number &&
                          afterProp.TryGetInt64(out var parsedAfter)
            ? parsedAfter
            : (long?)null;
        return (id, seq, kind, tokensBefore, tokensAfter);
    }

    private static long ExtractChatTimestampMs(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var key in new[] { "timestamp", "ts" })
        {
            if (!node.TryGetProperty(key, out var value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var raw))
            {
                continue;
            }

            var ms = raw > 10_000_000_000 ? raw : raw * 1000;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToUnixTimeMilliseconds();
            }
            catch
            {
                continue;
            }
        }

        return 0;
    }

    private void EmitRawChatEvent(JsonElement payload)
    {
        try
        {
            var stream = "chat";
            if (payload.TryGetProperty("message", out var message) &&
                message.TryGetProperty("role", out var roleProp))
            {
                stream = roleProp.GetString() ?? stream;
            }
            else if (payload.TryGetProperty("role", out var legacyRoleProp))
            {
                stream = legacyRoleProp.GetString() ?? stream;
            }

            var evt = new AgentEventInfo
            {
                RunId = payload.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "",
                Seq = payload.TryGetProperty("seq", out var seqProp) && seqProp.ValueKind == JsonValueKind.Number ? seqProp.GetInt32() : 0,
                Stream = stream,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = payload.Clone(),
                SessionKey = payload.TryGetProperty("sessionKey", out var sk) ? sk.GetString() : null
            };
            ChatEventReceived?.Invoke(this, evt);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to emit chat event: {ex.Message}");
        }
    }

    private void EmitChatNotification(string text, string? sessionKey = null)
    {
        var displayText = text.Length > 200 ? text[..200] + "…" : text;
        var notification = new OpenClawNotification
        {
            Message = displayText,
            FullMessage = text,
            IsChat = true,
            SessionKey = sessionKey
        };
        var (title, type) = _categorizer.Classify(notification, _userRules);
        notification.Title = title;
        notification.Type = type;
        NotificationReceived?.Invoke(this, notification);
    }

    private void HandleSessionEvent(JsonElement root)
    {
        // Re-request sessions list when session events come through
        _ = RequestSessionsAsync();
    }

    // --- State tracking ---

    private void UpdateTrackedSession(string sessionKey, bool isMain, string? currentActivity)
    {
        SessionInfo[] snapshot;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionKey, out var session))
            {
                session = new SessionInfo
                {
                    Key = sessionKey,
                    IsMain = isMain,
                    Status = "active"
                };
                _sessions[sessionKey] = session;
            }

            session.CurrentActivity = currentActivity;
            session.LastSeen = DateTime.UtcNow;

            snapshot = GetSessionListInternal();
        }

        SessionsUpdated?.Invoke(this, snapshot);
    }

    public SessionInfo[] GetSessionList()
    {
        lock (_sessionsLock)
        {
            return GetSessionListInternal();
        }
    }

    private SessionInfo[] GetSessionListInternal()
    {
        // Allocate the result array directly and copy in, then sort in-place.
        // Avoids the intermediate List<T> that new List(collection).ToArray() would produce.
        var arr = new SessionInfo[_sessions.Count];
        _sessions.Values.CopyTo(arr, 0);
        // Defensive copy: subscribers read the snapshot with no lock, and the tracked
        // instances keep being mutated in place under _sessionsLock — hand out clones.
        for (var i = 0; i < arr.Length; i++) arr[i] = arr[i].Clone();
        Array.Sort(arr, static (a, b) =>
        {
            // Main session first, then by last seen
            if (a.IsMain != b.IsMain) return a.IsMain ? -1 : 1;
            return b.LastSeen.CompareTo(a.LastSeen);
        });
        return arr;
    }

    // --- Parsing helpers ---

    private void ParseChannelHealth(JsonElement channels)
    {
        // Debug: log raw channel data
        _logger.Debug($"Raw channel health JSON: {channels.GetRawText()}");
        var healthList = ChannelHealthParser.Parse(channels);

        _logger.Info(healthList.Length > 0
            ? $"Channel health: {string.Join(", ", healthList.Select(c => $"{c.Name}={c.Status}"))}"
            : "Channel health: no channels");
        ChannelHealthUpdated?.Invoke(this, healthList);
    }

    private void PublishGatewaySelf(GatewaySelfInfo info)
    {
        if (!info.HasAnyDetails)
            return;

        GatewaySelfUpdated?.Invoke(this, info);
    }

    private void ParseSessions(JsonElement sessions)
    {
        try
        {
            SessionInfo[] snapshot;
            lock (_sessionsLock)
            {
                // Merge instead of clear — collect incoming keys, update/add, then remove absent
                var incomingKeys = new HashSet<string>();

                if (sessions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sessions.EnumerateArray())
                    {
                        var key = ParseSessionItem(item);
                        if (key != null) incomingKeys.Add(key);
                    }
                }
                else if (sessions.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sessions.EnumerateObject())
                    {
                        var sessionKey = prop.Name;

                        if (sessionKey is "recent" or "count" or "path" or "defaults" or "ts")
                            continue;

                        if (!sessionKey.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                            !sessionKey.Contains(':') &&
                            !sessionKey.Contains("agent") &&
                            !sessionKey.Contains("session"))
                            continue;

                        var item = prop.Value;

                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var strVal = item.GetString() ?? "";
                            if (strVal.StartsWith("/") || strVal.Contains("/."))
                                continue;
                        }
                        else if (item.ValueKind == JsonValueKind.Number)
                        {
                            continue;
                        }

                        // Update or create session
                        if (!_sessions.TryGetValue(sessionKey, out var session))
                        {
                            session = new SessionInfo { Key = sessionKey };
                        }

                        UpdateSessionMainStatus(session, sessionKey, item);

                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            PopulateSessionFromObject(session, item);
                        }
                        else if (item.ValueKind == JsonValueKind.String)
                        {
                            session.Status = item.GetString() ?? "";
                        }

                        _sessions[sessionKey] = session;
                        incomingKeys.Add(sessionKey);
                    }
                }

                // Remove sessions no longer present in the gateway response
                {
                    var staleKeys = new List<string>();
                    foreach (var key in _sessions.Keys)
                    {
                        if (!incomingKeys.Contains(key))
                            staleKeys.Add(key);
                    }
                    foreach (var key in staleKeys)
                        _sessions.Remove(key);
                }

                snapshot = GetSessionListInternal();
            }

            SessionsUpdated?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse sessions: {ex.Message}");
        }
    }

    private string? ParseSessionItem(JsonElement item)
    {
        var sessionKey = "unknown";
        if (item.TryGetProperty("key", out var key))
            sessionKey = key.GetString() ?? "unknown";

        // Update or create
        if (!_sessions.TryGetValue(sessionKey, out var session))
        {
            session = new SessionInfo { Key = sessionKey };
        }

        UpdateSessionMainStatus(session, sessionKey, item);

        PopulateSessionFromObject(session, item);

        _sessions[session.Key] = session;
        return session.Key;
    }

    private bool IsMainSessionKey(string sessionKey)
    {
        var mainSessionKey = MainSessionKey;
        if (!string.IsNullOrWhiteSpace(mainSessionKey))
        {
            if (string.Equals(sessionKey, mainSessionKey, StringComparison.Ordinal))
                return true;
            if (Volatile.Read(ref _mainSessionKeyIsCanonical))
                return false;

            // Legacy hello-ok snapshots exposed only the routing alias. Older
            // gateways canonicalized that alias with the default main agent.
            return string.Equals(sessionKey, $"agent:main:{mainSessionKey}", StringComparison.Ordinal);
        }

        // Compatibility for pre-handshake and older gateways. Once the
        // handshake resolves a canonical key it is the only authority.
        return sessionKey.Equals("main", StringComparison.Ordinal)
               || sessionKey.Equals("agent:main:main", StringComparison.Ordinal);
    }

    private void UpdateSessionMainStatus(SessionInfo session, string sessionKey, JsonElement item)
    {
        if (!string.IsNullOrWhiteSpace(MainSessionKey)
            && Volatile.Read(ref _mainSessionKeyIsCanonical))
        {
            session.IsMain = IsMainSessionKey(sessionKey);
            session.IsMainResolved = true;
            return;
        }

        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("isMain", out var isMain)
            && isMain.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            session.IsMain = isMain.GetBoolean();
            session.IsMainResolved = true;
            return;
        }

        // Sparse pre-handshake updates preserve both true and false row values;
        // only an unclassified session receives the bounded legacy key fallback.
        if (!session.IsMainResolved)
        {
            session.IsMain = IsMainSessionKey(sessionKey);
            session.IsMainResolved = true;
        }
    }

    private void PopulateSessionFromObject(SessionInfo session, JsonElement item)
    {
        if (item.TryGetProperty("status", out var status))
            session.Status = status.ValueKind == JsonValueKind.String
                ? status.GetString() ?? "unknown"
                : "unknown";
        if (item.TryGetProperty("hasActiveRun", out var hasActiveRun))
        {
            session.HasActiveRun = hasActiveRun.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? hasActiveRun.GetBoolean()
                : null;
        }
        if (item.TryGetProperty("model", out var model))
        {
            var newModel = model.GetString();
            if (session.Model != newModel)
                _logger.Info($"[SESSION] {session.Key}: model changed '{session.Model}' → '{newModel}'");
            session.Model = newModel;
        }
        if (item.TryGetProperty("label", out _)) session.Label = GetString(item, "label");
        if (item.TryGetProperty("channel", out _)) session.Channel = GetString(item, "channel");
        if (item.TryGetProperty("displayName", out _)) session.DisplayName = GetString(item, "displayName");
        if (item.TryGetProperty("derivedTitle", out _)) session.DerivedTitle = GetString(item, "derivedTitle");
        if (item.TryGetProperty("modelProvider", out _))
            session.Provider = GetString(item, "modelProvider");
        else if (item.TryGetProperty("provider", out _))
            session.Provider = GetString(item, "provider");
        if (item.TryGetProperty("subject", out _)) session.Subject = GetString(item, "subject");
        if (item.TryGetProperty("groupChannel", out _))
            session.Room = GetString(item, "groupChannel");
        else if (item.TryGetProperty("room", out _))
            session.Room = GetString(item, "room");
        if (item.TryGetProperty("space", out _)) session.Space = GetString(item, "space");
        if (item.TryGetProperty("chatType", out _)) session.ChatType = GetString(item, "chatType");
        if (item.TryGetProperty("classification", out _)) session.Classification = GetString(item, "classification");
        if (item.TryGetProperty("agentId", out _)) session.AgentId = GetString(item, "agentId");
        if (item.TryGetProperty("accountId", out _)) session.AccountId = GetString(item, "accountId");
        if (item.TryGetProperty("peerKind", out _)) session.PeerKind = GetString(item, "peerKind");
        if (item.TryGetProperty("isBackground", out var isBackground)
            && isBackground.ValueKind is JsonValueKind.True or JsonValueKind.False)
            session.IsBackground = isBackground.GetBoolean();
        if (item.TryGetProperty("execNode", out _)) session.ExecNode = GetString(item, "execNode");
        if (item.TryGetProperty("parentSessionKey", out _))
            session.ParentSessionKey = GetString(item, "parentSessionKey");
        if (item.TryGetProperty("spawnDepth", out var spawnDepth))
            session.SpawnDepth = spawnDepth.ValueKind == JsonValueKind.Number
                && spawnDepth.TryGetInt32(out var parsedSpawnDepth)
                    ? parsedSpawnDepth
                    : null;
        if (item.TryGetProperty("origin", out var origin))
            session.OriginLabel = origin.ValueKind == JsonValueKind.Object
                ? GetString(origin, "label")
                : null;
        if (item.TryGetProperty("worktree", out _)) session.Worktree = ParseSessionWorktree(item);
        if (item.TryGetProperty("sessionId", out var sessionId))
            session.SessionId = sessionId.GetString();
        if (item.TryGetProperty("thinkingLevel", out var thinking))
            session.ThinkingLevel = thinking.GetString();
        if (item.TryGetProperty("verboseLevel", out var verbose))
            session.VerboseLevel = verbose.GetString();
        if (item.TryGetProperty("systemSent", out var systemSent) &&
            (systemSent.ValueKind == JsonValueKind.True || systemSent.ValueKind == JsonValueKind.False))
            session.SystemSent = systemSent.GetBoolean();
        if (item.TryGetProperty("abortedLastRun", out var abortedLastRun) &&
            (abortedLastRun.ValueKind == JsonValueKind.True || abortedLastRun.ValueKind == JsonValueKind.False))
            session.AbortedLastRun = abortedLastRun.GetBoolean();
        session.InputTokens = GetLong(item, "inputTokens");
        session.OutputTokens = GetLong(item, "outputTokens");
        session.TotalTokens = GetLong(item, "totalTokens");
        session.ContextTokens = GetLong(item, "contextTokens");

        var updated = ParseUnixTimestampMs(item, "updatedAt");
        if (updated.HasValue)
        {
            session.UpdatedAt = updated.Value;
        }

        if (item.TryGetProperty("startedAt", out var started))
        {
            if (started.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(started.GetString(), out var dt))
                    session.StartedAt = dt;
            }
            else if (started.ValueKind == JsonValueKind.Number)
            {
                var ms = started.GetInt64();
                session.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            }
        }
    }

    private static SessionWorktreeInfo? ParseSessionWorktree(JsonElement item)
    {
        if (!item.TryGetProperty("worktree", out var worktree) || worktree.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(worktree, "id");
        var branch = GetString(worktree, "branch");
        var repoRoot = GetString(worktree, "repoRoot");
        return id is null && branch is null && repoRoot is null
            ? null
            : new SessionWorktreeInfo { Id = id, Branch = branch, RepoRoot = repoRoot };
    }

    private void ParseNodeList(JsonElement nodesPayload)
    {
        try
        {
            JsonElement nodes = nodesPayload;
            if (nodesPayload.ValueKind == JsonValueKind.Object)
            {
                if (nodesPayload.TryGetProperty("nodes", out var nestedNodes))
                    nodes = nestedNodes;
                else if (nodesPayload.TryGetProperty("items", out var nestedItems))
                    nodes = nestedItems;
            }

            if (nodes.ValueKind != JsonValueKind.Array)
                return;

            var buffer = new GatewayNodeInfo[nodes.GetArrayLength()];
            var count = 0;
            foreach (var nodeElement in nodes.EnumerateArray())
            {
                if (nodeElement.ValueKind != JsonValueKind.Object)
                    continue;

                var nodeId = FirstNonEmpty(
                    GetString(nodeElement, "nodeId"),
                    GetString(nodeElement, "deviceId"),
                    GetString(nodeElement, "id"),
                    GetString(nodeElement, "clientId"));
                if (string.IsNullOrWhiteSpace(nodeId))
                    continue;

                var status = FirstNonEmpty(
                    GetString(nodeElement, "status"),
                    GetString(nodeElement, "state"),
                    "unknown");
                var connected = GetOptionalBool(nodeElement, "connected");
                var online = GetOptionalBool(nodeElement, "online");
                var paired = GetOptionalBool(nodeElement, "paired");
                var capabilities = nodeElement.TryGetProperty("caps", out _)
                    ? GetStringArray(nodeElement, "caps")
                    : GetStringArray(nodeElement, "capabilities");
                var commands = GetStringArray(nodeElement, "commands");
                var disabledCommands = GetStringArray(nodeElement, "disabledCommands");
                var permissions = GetBoolDictionary(nodeElement, "permissions");
                var pendingDeclaredCapabilities = GetStringArray(nodeElement, "pendingDeclaredCaps");
                var pendingDeclaredCommands = GetStringArray(nodeElement, "pendingDeclaredCommands");
                var pendingDeclaredPermissions = GetBoolDictionary(nodeElement, "pendingDeclaredPermissions");
                var hasApprovalFields =
                    nodeElement.TryGetProperty("approvalState", out _) ||
                    nodeElement.TryGetProperty("pendingRequestId", out _) ||
                    nodeElement.TryGetProperty("pendingDeclaredCaps", out _) ||
                    nodeElement.TryGetProperty("pendingDeclaredCommands", out _) ||
                    nodeElement.TryGetProperty("pendingDeclaredPermissions", out _);
                var unverifiedDeclaredCommands = hasApprovalFields
                    ? []
                    : GetStringArray(nodeElement, "declaredCommands");

                var clientMode = GetString(nodeElement, "clientMode");

                // Distinguish "user gave this node a name" from "we fell back
                // to the id". The rename dialog uses this so it can prefill
                // empty when the node has no explicit name (rather than
                // pre-seeding the textbox with the id, which would otherwise
                // get persisted as the new display name on Enter).
                var explicitName = FirstNonEmpty(
                    GetString(nodeElement, "displayName"),
                    GetString(nodeElement, "name"),
                    GetString(nodeElement, "label"));

                buffer[count++] = new GatewayNodeInfo
                {
                    NodeId = nodeId!,
                    DisplayName = !string.IsNullOrWhiteSpace(explicitName)
                        ? explicitName!
                        : FirstNonEmpty(GetString(nodeElement, "shortId"), nodeId)!,
                    HasExplicitDisplayName = !string.IsNullOrWhiteSpace(explicitName),
                    Mode = FirstNonEmpty(
                        GetString(nodeElement, "mode"),
                        clientMode,
                        "node")!,
                    Status = status!,
                    Platform = FirstNonEmpty(
                        GetString(nodeElement, "platform"),
                        GetString(nodeElement, "os")),
                    // Gateway NodeListNode wire schema uses *Ms suffix; older
                    // fallbacks kept for compatibility with mocks/tests.
                    // ConnectedAt is parsed independently below — do NOT fall
                    // back to it here, otherwise the UI shows the same value
                    // twice as both "Connected Xm ago" and "Seen Xm ago".
                    LastSeen = ParseUnixTimestampMs(nodeElement, "lastSeenAtMs") ??
                               ParseUnixTimestampMs(nodeElement, "lastSeenAt") ??
                               ParseUnixTimestampMs(nodeElement, "lastSeen") ??
                               ParseUnixTimestampMs(nodeElement, "updatedAt"),
                    ConnectedAt = ParseUnixTimestampMs(nodeElement, "connectedAtMs") ??
                                  ParseUnixTimestampMs(nodeElement, "connectedAt"),
                    ApprovedAt = ParseUnixTimestampMs(nodeElement, "approvedAtMs") ??
                                 ParseUnixTimestampMs(nodeElement, "approvedAt"),
                    LastSeenReason = GetString(nodeElement, "lastSeenReason"),
                    Capabilities = capabilities.ToList(),
                    Commands = commands.ToList(),
                    DisabledCommands = disabledCommands.ToList(),
                    Permissions = permissions,
                    CapabilityCount = capabilities.Length,
                    CommandCount = commands.Length,
                    ApprovalState = ParseNodeApprovalState(nodeElement),
                    PendingRequestId = GetSafeRequestId(nodeElement, "pendingRequestId"),
                    PendingDeclaredCapabilities = pendingDeclaredCapabilities.ToList(),
                    PendingDeclaredCommands = pendingDeclaredCommands.ToList(),
                    PendingDeclaredPermissions = pendingDeclaredPermissions,
                    UnverifiedDeclaredCommands = unverifiedDeclaredCommands.ToList(),
                    Version = GetString(nodeElement, "version"),
                    CoreVersion = GetString(nodeElement, "coreVersion"),
                    UiVersion = GetString(nodeElement, "uiVersion"),
                    ClientId = GetString(nodeElement, "clientId"),
                    ClientMode = clientMode,
                    DeviceFamily = GetString(nodeElement, "deviceFamily"),
                    ModelIdentifier = GetString(nodeElement, "modelIdentifier"),
                    RemoteIp = GetString(nodeElement, "remoteIp"),
                    PathEnv = GetString(nodeElement, "pathEnv"),
                    IsPaired = paired ?? false,
                    IsOnline = online ?? connected ?? status is "ok" or "online" or "connected" or "ready" or "active"
                };
            }

            var ordered = buffer[..count];
            Array.Sort(ordered, static (a, b) =>
            {
                int c = b.IsOnline.CompareTo(a.IsOnline);
                if (c != 0) return c;
                c = (b.LastSeen ?? DateTime.MinValue).CompareTo(a.LastSeen ?? DateTime.MinValue);
                if (c != 0) return c;
                return StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
            });

            NodesUpdated?.Invoke(this, ordered);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse node.list: {ex.Message}");
        }
    }

    private void ParseUsage(JsonElement usage)
    {
        try
        {
            _usage ??= new GatewayUsageInfo();
            if (usage.TryGetProperty("inputTokens", out var inp))
                _usage.InputTokens = inp.GetInt64();
            if (usage.TryGetProperty("outputTokens", out var outp))
                _usage.OutputTokens = outp.GetInt64();
            if (usage.TryGetProperty("totalTokens", out var tot))
                _usage.TotalTokens = tot.GetInt64();
            if (usage.TryGetProperty("cost", out var cost))
                _usage.CostUsd = cost.GetDouble();
            if (usage.TryGetProperty("requestCount", out var req))
                _usage.RequestCount = req.GetInt32();
            if (usage.TryGetProperty("model", out var model))
                _usage.Model = model.GetString();
            _usage.ProviderSummary = null;

            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage: {ex.Message}");
        }
    }

    private void ParseUsageStatus(JsonElement payload)
    {
        try
        {
            var status = new GatewayUsageStatusInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "updatedAt") ?? DateTime.UtcNow
            };

            if (payload.TryGetProperty("providers", out var providers) &&
                providers.ValueKind == JsonValueKind.Array)
            {
                foreach (var providerElement in providers.EnumerateArray())
                {
                    var provider = new GatewayUsageProviderInfo
                    {
                        Provider = GetString(providerElement, "provider") ?? "",
                        DisplayName = GetString(providerElement, "displayName") ?? GetString(providerElement, "provider") ?? "",
                        Plan = GetString(providerElement, "plan"),
                        Error = GetString(providerElement, "error")
                    };

                    if (providerElement.TryGetProperty("windows", out var windows) &&
                        windows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var windowElement in windows.EnumerateArray())
                        {
                            provider.Windows.Add(new GatewayUsageWindowInfo
                            {
                                Label = GetString(windowElement, "label") ?? "",
                                UsedPercent = GetDouble(windowElement, "usedPercent"),
                                ResetAt = ParseUnixTimestampMs(windowElement, "resetAt")
                            });
                        }
                    }

                    status.Providers.Add(provider);
                }
            }

            _usageStatus = status;
            UsageStatusUpdated?.Invoke(this, status);

            _usage ??= new GatewayUsageInfo();
            _usage.ProviderSummary = BuildProviderSummary(status);
            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage.status: {ex.Message}");
        }
    }

    private void ParseUsageCost(JsonElement payload)
    {
        try
        {
            var summary = new GatewayCostUsageInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "updatedAt") ?? DateTime.UtcNow,
                Days = JsonReadHelpers.GetInt(payload, "days")
            };

            if (payload.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
            {
                summary.Totals = new GatewayCostUsageTotalsInfo
                {
                    Input = GetLong(totals, "input"),
                    Output = GetLong(totals, "output"),
                    CacheRead = GetLong(totals, "cacheRead"),
                    CacheWrite = GetLong(totals, "cacheWrite"),
                    TotalTokens = GetLong(totals, "totalTokens"),
                    TotalCost = GetDouble(totals, "totalCost"),
                    MissingCostEntries = JsonReadHelpers.GetInt(totals, "missingCostEntries")
                };
            }

            if (payload.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Array)
            {
                foreach (var day in daily.EnumerateArray())
                {
                    summary.Daily.Add(new GatewayCostUsageDayInfo
                    {
                        Date = GetString(day, "date") ?? "",
                        Input = GetLong(day, "input"),
                        Output = GetLong(day, "output"),
                        CacheRead = GetLong(day, "cacheRead"),
                        CacheWrite = GetLong(day, "cacheWrite"),
                        TotalTokens = GetLong(day, "totalTokens"),
                        TotalCost = GetDouble(day, "totalCost"),
                        MissingCostEntries = JsonReadHelpers.GetInt(day, "missingCostEntries")
                    });
                }
            }

            _usageCost = summary;
            UsageCostUpdated?.Invoke(this, summary);

            _usage ??= new GatewayUsageInfo();
            _usage.TotalTokens = summary.Totals.TotalTokens;
            _usage.CostUsd = summary.Totals.TotalCost;
            UsageUpdated?.Invoke(this, _usage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse usage.cost: {ex.Message}");
        }
    }

    private void ParseSessionsPreview(JsonElement payload)
    {
        try
        {
            var previewPayload = new SessionsPreviewPayloadInfo
            {
                UpdatedAt = ParseUnixTimestampMs(payload, "ts") ?? DateTime.UtcNow
            };

            if (payload.TryGetProperty("previews", out var previews) &&
                previews.ValueKind == JsonValueKind.Array)
            {
                foreach (var previewElement in previews.EnumerateArray())
                {
                    var preview = new SessionPreviewInfo
                    {
                        Key = GetString(previewElement, "key") ?? "",
                        Status = GetString(previewElement, "status") ?? "unknown"
                    };

                    if (previewElement.TryGetProperty("items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            preview.Items.Add(new SessionPreviewItemInfo
                            {
                                Role = GetString(item, "role") ?? "other",
                                Text = GetString(item, "text") ?? ""
                            });
                        }
                    }

                    previewPayload.Previews.Add(preview);
                }
            }

            SessionPreviewUpdated?.Invoke(this, previewPayload);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse sessions.preview: {ex.Message}");
        }
    }

    private void ParseSessionCommandResult(string method, JsonElement payload)
    {
        var result = new SessionCommandResult
        {
            Method = method,
            Ok = true,
            Key = GetString(payload, "key"),
            Reason = GetString(payload, "reason")
        };

        if (payload.TryGetProperty("deleted", out var deleted) &&
            (deleted.ValueKind == JsonValueKind.True || deleted.ValueKind == JsonValueKind.False))
        {
            result.Deleted = deleted.GetBoolean();
        }

        if (payload.TryGetProperty("compacted", out var compacted) &&
            (compacted.ValueKind == JsonValueKind.True || compacted.ValueKind == JsonValueKind.False))
        {
            result.Compacted = compacted.GetBoolean();
        }

        if (payload.TryGetProperty("kept", out var kept) && kept.ValueKind == JsonValueKind.Number)
        {
            result.Kept = kept.GetInt32();
        }

        SessionCommandCompleted?.Invoke(this, result);
    }

    private static string BuildProviderSummary(GatewayUsageStatusInfo status)
    {
        if (status.Providers.Count == 0) return "";

        // At most 2 providers are shown; track them with two nullable strings to avoid
        // allocating a List<string> on every usage-status update.
        string? p0 = null, p1 = null;
        int included = 0;

        foreach (var provider in status.Providers)
        {
            if (included == 2) break;
            var displayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Provider : provider.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "provider";

            string part;
            if (!string.IsNullOrWhiteSpace(provider.Error))
            {
                part = $"{displayName}: error";
            }
            else
            {
                if (provider.Windows.Count == 0) continue;
                var window = provider.Windows.MaxBy(w => w.UsedPercent);
                if (window is null) continue;
                var remaining = Math.Clamp((int)Math.Round(100 - window.UsedPercent), 0, 100);
                part = $"{displayName}: {remaining}% left";
            }

            if (included == 0) p0 = part;
            else p1 = part;
            included++;
        }

        if (included == 0) return "";

        string? overflow = status.Providers.Count > 2 ? $"+{status.Providers.Count - 2}" : null;
        return (p1, overflow) switch
        {
            (null, null) => p0!,
            (null, _)    => $"{p0} · {overflow}",
            (_, null)    => $"{p0} · {p1}",
            _            => $"{p0} · {p1} · {overflow}",
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool? GetOptionalBool(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static long GetLong(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetInt64(out var longVal)) return longVal;
        if (value.TryGetDouble(out var doubleVal)) return (long)doubleVal;
        return 0;
    }

    private static double GetDouble(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (value.TryGetDouble(out var doubleVal)) return doubleVal;
        return 0;
    }

    private static int GetArrayLength(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return 0;
        return value.GetArrayLength();
    }

    private static string[] GetStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var buffer = new string[value.GetArrayLength()];
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                buffer[count++] = text;
        }

        return count == 0 ? [] : buffer[..count];
    }

    private static GatewayNodeApprovalState ParseNodeApprovalState(JsonElement node)
    {
        var value = GetString(node, "approvalState");
        if (value is null)
            return GatewayNodeApprovalState.Unknown;

        return value.ToLowerInvariant() switch
        {
            "approved" => GatewayNodeApprovalState.Approved,
            "pending-approval" => GatewayNodeApprovalState.PendingApproval,
            "pending-reapproval" => GatewayNodeApprovalState.PendingReapproval,
            "unapproved" => GatewayNodeApprovalState.Unapproved,
            _ => GatewayNodeApprovalState.Unknown
        };
    }

    private static Dictionary<string, bool> GetBoolDictionary(JsonElement parent, string property)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var item in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;

            if (item.Value.ValueKind == JsonValueKind.True)
                result[item.Name] = true;
            else if (item.Value.ValueKind == JsonValueKind.False)
                result[item.Name] = false;
        }

        return result;
    }

    private static DateTime? ParseUnixTimestampMs(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetDouble(out var raw)) return null;

        // Accept either milliseconds or seconds.
        var ms = raw > 10_000_000_000 ? raw : raw * 1000;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }

    // --- Notification classification ---

    private static readonly NotificationCategorizer _categorizer = new();

    private void EmitNotification(string text)
    {
        var notification = new OpenClawNotification
        {
            Message = text.Length > 200 ? text[..200] + "…" : text
        };
        var (title, type) = _categorizer.Classify(notification, _userRules, _preferStructuredCategories);
        notification.Title = title;
        notification.Type = type;
        NotificationReceived?.Invoke(this, notification);
    }

    // --- Utility ---

    // FrozenDictionary gives O(1) case-insensitive lookup without allocating a
    // lowercased copy of toolName on every call.
    private static readonly FrozenDictionary<string, ActivityKind> s_toolKindMap =
        new Dictionary<string, ActivityKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["exec"]       = ActivityKind.Exec,
            ["read"]       = ActivityKind.Read,
            ["write"]      = ActivityKind.Write,
            ["edit"]       = ActivityKind.Edit,
            ["web_search"] = ActivityKind.Search,
            ["web_fetch"]  = ActivityKind.Search,
            ["browser"]    = ActivityKind.Browser,
            ["message"]    = ActivityKind.Message,
            ["tts"]        = ActivityKind.Tool,
            ["image"]      = ActivityKind.Tool,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static ActivityKind ClassifyTool(string toolName) =>
        s_toolKindMap.TryGetValue(toolName, out var kind) ? kind : ActivityKind.Tool;

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Walk from the end to find the last two path separators without
        // allocating an intermediate Replace'd string or a Split array.
        var span = path.AsSpan();
        int lastSep = span.LastIndexOfAny('/', '\\');
        if (lastSep < 0) return path; // single component — no separator

        var lastName = span[(lastSep + 1)..];

        // Check for a second-to-last separator in the prefix.
        int secondLastSep = span[..lastSep].LastIndexOfAny('/', '\\');

        if (secondLastSep < 0 || lastSep == 0)
        {
            // Two components or a leading-slash-only prefix (e.g. "folder/file" or "/file")
            // → return just the filename.
            return lastName.ToString();
        }

        // Three or more components → "…/parent/last"
        var parentName = span[(secondLastSep + 1)..lastSep];
        return string.Concat("…/".AsSpan(), parentName, "/".AsSpan(), lastName);
    }

    // ── Parse methods for new features ──

    private void ParseModelsList(JsonElement payload)
    {
        try
        {
            ModelsListUpdated?.Invoke(this, ParseModelsListPayload(payload));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse models.list: {ex.Message}");
        }
    }

    internal static ModelsListInfo ParseModelsListPayload(JsonElement payload)
    {
        var info = new ModelsListInfo();
        // Gateway returns { models: [...] } or just an array.
        var modelsArray = payload.ValueKind == JsonValueKind.Array
            ? payload
            : payload.TryGetProperty("models", out var m) ? m : default;

        if (modelsArray.ValueKind != JsonValueKind.Array)
            return info;

        foreach (var item in modelsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            // Read readiness flags defensively; older gateways may omit
            // them, and the UI only uses them for labels/selectability.
            var hasConfiguredFlag = item.TryGetProperty("configured", out var cfg)
                                    && (cfg.ValueKind == JsonValueKind.True || cfg.ValueKind == JsonValueKind.False);
            bool available = true;
            if (TryReadBool(item, out var av, "available")) available = av;
            else if (TryReadBool(item, out var un, "unavailable")) available = !un;

            var model = new ModelInfo
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Name = item.TryGetProperty("name", out var name) ? name.GetString() : null,
                Provider = item.TryGetProperty("provider", out var prov) ? prov.GetString() : null,
                ContextWindow = ReadPositiveInt32(item, "contextWindow"),
                ContextTokens = ReadPositiveInt32(item, "contextTokens"),
                IsConfigured = hasConfiguredFlag && cfg.ValueKind == JsonValueKind.True,
                HasConfiguredFlag = hasConfiguredFlag,
                IsDefault = ReadBool(item, "default", "isDefault"),
                IsAvailable = available,
                RequiresAuth = ReadBool(item, "requiresAuth", "authRequired", "needsAuth", "authNeeded")
            };
            if (!string.IsNullOrEmpty(model.Id))
                info.Models.Add(model);
        }

        return info;
    }

    internal static ModelsListInfo MergeModelCatalog(ModelsListInfo configured, ModelsListInfo catalog)
    {
        var result = new ModelsListInfo();
        var byIdentity = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in configured.Models)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id)) continue;

            var identity = GetModelIdentity(source);
            if (!byIdentity.TryAdd(identity, CloneModelInfo(source))) continue;

            var configuredModel = byIdentity[identity];
            configuredModel.HasConfiguredFlag = true;
            configuredModel.IsConfigured = true;
            result.Models.Add(configuredModel);
        }

        foreach (var source in catalog.Models)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id)) continue;
            if (!source.IsAvailable && !source.RequiresAuth) continue;

            var identity = GetModelIdentity(source);
            if (byIdentity.TryGetValue(identity, out var configuredModel))
            {
                configuredModel.Name ??= source.Name;
                configuredModel.Provider ??= source.Provider;
                configuredModel.ContextWindow ??= source.ContextWindow;
                configuredModel.ContextTokens ??= source.ContextTokens;
                configuredModel.IsDefault |= source.IsDefault;
                configuredModel.RequiresAuth |= source.RequiresAuth;
                continue;
            }

            var catalogModel = CloneModelInfo(source);
            catalogModel.HasConfiguredFlag = true;
            catalogModel.IsConfigured = false;
            // Catalog-only entries stay visible for discovery, while the explicit
            // configuration flag keeps them disabled until they are allowlisted.
            byIdentity.Add(identity, catalogModel);
            result.Models.Add(catalogModel);
        }

        return result;
    }

    private static ModelInfo CloneModelInfo(ModelInfo source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Provider = source.Provider,
        ContextWindow = source.ContextWindow,
        ContextTokens = source.ContextTokens,
        IsConfigured = source.IsConfigured,
        HasConfiguredFlag = source.HasConfiguredFlag,
        IsDefault = source.IsDefault,
        IsAvailable = source.IsAvailable,
        RequiresAuth = source.RequiresAuth
    };

    private static string GetModelIdentity(ModelInfo model)
    {
        var id = model.Id.Trim();
        var provider = model.Provider?.Trim();
        if (string.IsNullOrEmpty(provider)) return id;

        var prefix = provider + "/";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? id
            : prefix + id;
    }

    private static int? ReadPositiveInt32(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }

    // Reads the first present boolean property among <paramref name="keys"/>.
    // Returns false when none are present (or are non-boolean).
    private static bool ReadBool(JsonElement obj, params string[] keys) =>
        TryReadBool(obj, out var value, keys) && value;

    private static bool TryReadBool(JsonElement obj, out bool value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
            }
        }
        value = false;
        return false;
    }

    private void ParseNodePairList(JsonElement payload)
    {
        try
        {
            var info = new PairingListInfo();
            var pending = payload.TryGetProperty("pending", out var p) ? p : default;
            if (pending.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pending.EnumerateArray())
                {
                    info.Pending.Add(new PairingRequest
                    {
                        RequestId = item.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "",
                        NodeId = item.TryGetProperty("nodeId", out var nid) ? nid.GetString() ?? "" : "",
                        DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                        Platform = item.TryGetProperty("platform", out var plat) ? plat.GetString() : null,
                        Version = item.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                        RemoteIp = item.TryGetProperty("remoteIp", out var ip) ? ip.GetString() : null,
                        IsRepair = item.TryGetProperty("isRepair", out var rep) && rep.ValueKind == JsonValueKind.True,
                        Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0
                    });
                }
            }
            NodePairListUpdated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse node.pair.list: {ex.Message}");
        }
    }

    private void ParseDevicePairList(JsonElement payload)
    {
        try
        {
            var info = new DevicePairingListInfo();
            var pending = payload.TryGetProperty("pending", out var p) ? p : default;
            if (pending.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pending.EnumerateArray())
                {
                    string[]? scopes = null;
                    if (item.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array)
                    {
                        var scopeList = new List<string>();
                        foreach (var s in sc.EnumerateArray())
                            if (s.GetString() is string sv) scopeList.Add(sv);
                        scopes = scopeList.ToArray();
                    }

                    info.Pending.Add(new DevicePairingRequest
                    {
                        RequestId = item.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "",
                        DeviceId = item.TryGetProperty("deviceId", out var did) ? did.GetString() ?? "" : "",
                        PublicKey = item.TryGetProperty("publicKey", out var pk) ? pk.GetString() : null,
                        DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                        Platform = item.TryGetProperty("platform", out var plat) ? plat.GetString() : null,
                        ClientId = item.TryGetProperty("clientId", out var cid) ? cid.GetString() : null,
                        ClientMode = item.TryGetProperty("clientMode", out var cm) ? cm.GetString() : null,
                        Role = item.TryGetProperty("role", out var role) ? role.GetString() : null,
                        Scopes = scopes,
                        RemoteIp = item.TryGetProperty("remoteIp", out var ip) ? ip.GetString() : null,
                        IsRepair = item.TryGetProperty("isRepair", out var rep) && rep.ValueKind == JsonValueKind.True,
                        Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : 0
                    });
                }
            }
            DevicePairListUpdated?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse device.pair.list: {ex.Message}");
        }
    }

    private void TryParsePresence(JsonElement payload)
    {
        try
        {
            if (!payload.TryGetProperty("snapshot", out var snapshot)) return;
            if (!snapshot.TryGetProperty("presence", out var presenceArray)) return;
            if (presenceArray.ValueKind != JsonValueKind.Array) return;

            var entries = ParsePresenceArray(presenceArray);
            _logger.Info($"Parsed {entries.Length} presence entries from handshake");
            PresenceUpdated?.Invoke(this, entries);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse presence from handshake: {ex.Message}");
        }
    }

    private void TryParsePresenceFromBroadcast(JsonElement payload)
    {
        try
        {
            // Broadcast may contain presence array directly or nested
            var presenceArray = payload.ValueKind == JsonValueKind.Array
                ? payload
                : payload.TryGetProperty("presence", out var p) ? p : default;

            if (presenceArray.ValueKind != JsonValueKind.Array) return;

            var entries = ParsePresenceArray(presenceArray);
            PresenceUpdated?.Invoke(this, entries);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse presence broadcast: {ex.Message}");
        }
    }

    private static PresenceEntry[] ParsePresenceArray(JsonElement array)
    {
        var list = new List<PresenceEntry>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(new PresenceEntry
            {
                Host = item.TryGetProperty("host", out var h) ? h.GetString() : null,
                Ip = item.TryGetProperty("ip", out var ip) ? ip.GetString() : null,
                Version = item.TryGetProperty("version", out var v) ? v.GetString() : null,
                Platform = item.TryGetProperty("platform", out var p) ? p.GetString() : null,
                DeviceFamily = item.TryGetProperty("deviceFamily", out var df) ? df.GetString() : null,
                ModelIdentifier = item.TryGetProperty("modelIdentifier", out var mi) ? mi.GetString() : null,
                Mode = item.TryGetProperty("mode", out var m) ? m.GetString() : null,
                LastInputSeconds = item.TryGetProperty("lastInputSeconds", out var lis) && lis.ValueKind == JsonValueKind.Number ? lis.GetInt32() : null,
                Reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null,
                Tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                Text = item.TryGetProperty("text", out var tx) ? tx.GetString() : null,
                Ts = item.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                DeviceId = item.TryGetProperty("deviceId", out var did) ? did.GetString() : null,
                Roles = item.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                    ? roles.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                Scopes = item.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
                    ? sc.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : null,
                InstanceId = item.TryGetProperty("instanceId", out var iid) ? iid.GetString() : null,
            });
        }
        return list.ToArray();
    }
}
