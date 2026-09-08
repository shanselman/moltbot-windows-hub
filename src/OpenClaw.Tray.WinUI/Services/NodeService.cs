using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Dispatching;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Mcp;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using OpenClawTray.A2UI.Actions;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.Helpers;
using OpenClawTray.Windows;

namespace OpenClawTray.Services;

/// <summary>
/// Windows Node service - manages node connection and capabilities
/// </summary>
public sealed class NodeService : IDisposable, IAsyncDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SettingsManager? _settings;
    private readonly SemaphoreSlim _consentLock = new(1, 1);
    private readonly object _disposeLock = new();
    private TaskCompletionSource<bool>? _screenConsentInFlight;
    private TaskCompletionSource<bool>? _cameraConsentInFlight;
    private TaskCompletionSource<bool>? _locationConsentInFlight;
    private Task? _disposeTask;
    private WindowsNodeClient? _nodeClient;
    private CanvasWindow? _canvasWindow;
    // Invariant: _a2uiCanvasWindow is only read/written from the UI dispatcher
    // (DispatcherQueue.TryEnqueue callbacks). Today's WinUI dispatcher serializes,
    // so no memory barrier is needed — but introducing a non-dispatcher caller
    // would silently see stale references. Stay on-thread or marshal.
    private A2UICanvasWindow? _a2uiCanvasWindow;
    private MediaResolver? _mediaResolver;
    private ActionDispatcher? _actionDispatcher;
    private ScreenCaptureService? _screenCaptureService;
    private ScreenRecordingService? _screenRecordingService;
    private CameraCaptureService? _cameraCaptureService;
    // Concurrent navigates from rapid-fire agent requests can race on the
    // bucket structure of a HashSet. Use ConcurrentDictionary as a thread-safe
    // set; value byte is unused.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _allowedNavigationHosts =
        new(StringComparer.OrdinalIgnoreCase);

    // Navigation-prompt rate-limit / coalescing state. A token-holding agent
    // looping canvas.navigate could otherwise stack arbitrarily many topmost
    // MessageBoxW prompts.
    //   _navigationPromptGate: only one prompt visible at a time across all hosts.
    //   _pendingNavigationPrompts: in-flight prompt per HostKey — a second
    //     request for the same host inherits the user's decision instead of
    //     queueing a duplicate prompt.
    //   _navigationDenyCooldown: HostKey → expiresAt. After a Deny, repeated
    //     requests for the same host auto-deny silently for the cooldown
    //     window so a hostile loop can't keep nagging the user.
    private readonly SemaphoreSlim _navigationPromptGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<UrlNavigationApprovalDecision>> _pendingNavigationPrompts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _navigationDenyCooldown =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NavigationDenyCooldownDuration = TimeSpan.FromSeconds(30);

    // STT: rate-limit successive stt.listen invocations to prevent a
    // compromised gateway from looping mic capture at the 120 s cap.
    private static readonly TimeSpan SttListenMinInterval = TimeSpan.FromSeconds(1);
    private DateTimeOffset _lastSttListenStartUtc = DateTimeOffset.MinValue;
    
    // Capabilities
    private SystemCapability? _systemCapability;

    // Created once per NodeService lifetime and reused across capability
    // rebuilds. The coordinator serializes approvals with a per-instance
    // semaphore, so a fresh instance per rebuild would let an in-flight
    // approval on the old instance overlap with one on the new instance.
    private IExecApprovalV2Handler? _execApprovalsV2Handler;
    private ExecApprovalsStore? _execApprovalsStore;
    private CanvasCapability? _canvasCapability;
    private ScreenCapability? _screenCapability;
    private CameraCapability? _cameraCapability;
    private LocationCapability? _locationCapability;
    private DeviceCapability? _deviceCapability;
    private DeviceStatusProvider? _deviceStatusProvider;
    private BrowserProxyCapability? _browserProxyCapability;
    private SttCapability? _sttCapability;
    private TtsCapability? _ttsCapability;
    private OllamaCapability? _ollamaCapability;
    private OllamaInferenceBackend? _ollamaBackend;
    private TextToSpeechService? _textToSpeechService;
    private VoiceService? _voiceService;
    private readonly string _dataPath;
    // Identity store location for the role-aware DeviceIdentity. Defaults to
    // _dataPath when no separate path is supplied (preserves existing test
    // behavior that hands a single temp directory to NodeService). The Tray
    // app supplies its build-specific roaming data folder so node device tokens land in
    // the same DeviceIdentity store as operator tokens (Phase 1 model:
    // single shared location, role distinction inside).
    private readonly string _identityDataPath;
    private readonly Func<string?>? _sharedGatewayTokenResolver;
    private readonly Func<int?>? _browserControlPortResolver;
    private readonly Func<SshTunnelConfig?>? _activeGatewayTunnelResolver;
    private readonly Func<string?>? _activeGatewayUrlResolver;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _browserControlAuthorization;
    private string? _token;

    // Authoritative capability list — populated by RegisterCapabilities and
    // shared with both the gateway client (when present) and the MCP bridge.
    // Holding it here lets MCP-only mode skip the gateway client entirely.
    //
    // Mutated on the UI thread (Connect / StartLocalOnly / Disconnect rebuild
    // it); read by the MCP bridge on threadpool threads (every tools/list and
    // tools/call). Every read/write goes through _capabilitiesLock so a
    // bridge snapshot can't race a re-register.
    private readonly List<INodeCapability> _capabilities = new();
    private readonly object _capabilitiesLock = new();

    // MCP-only capabilities — visible to local MCP clients but NOT registered
    // on the gateway WebSocket. Used for app-level testing/control tools that
    // should not be callable by remote agents.
    private readonly List<INodeCapability> _mcpOnlyCapabilities = new();

    // Serializes AttachClient ↔ DisconnectAsync so a reconnect that overlaps a
    // disconnect can't leave stale subscriptions on an old client or double-
    // subscribe a re-attached client (Hanselman review #1). Held only for
    // the synchronous subscription bookkeeping; capability registration uses
    // its own lock above.
    private readonly object _clientLock = new();

    // Local MCP server — exposes the same capabilities to local MCP clients.
    // TODO: when the port becomes user-configurable (see docs/MCP_MODE.md
    // "Deferred"), McpServerUrl needs to read the live port off the running
    // server, not the constant. Settings UI is the only consumer today.
    public const int McpDefaultPort = 8765;
    // OPENCLAW_MCP_PORT lets test instances bind a free port instead of fighting
    // over the default. Falls back to McpDefaultPort when unset or unparseable.
    private static readonly int McpPort =
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_MCP_PORT"), out var p) && p > 0
            ? p
            : McpDefaultPort;
    private static readonly bool SuppressExternalBrowserLaunches =
        string.Equals(
            Environment.GetEnvironmentVariable("OPENCLAW_SUPPRESS_EXTERNAL_BROWSER"),
            "1",
            StringComparison.Ordinal);
    public static string McpServerUrl => $"http://127.0.0.1:{McpPort}/";
    /// <summary>
    /// Path of the MCP bearer-token file. The file is created on first MCP server
    /// start and persists across restarts; the same string is sent on every POST
    /// as <c>Authorization: Bearer &lt;contents&gt;</c>. Surfaced by Settings UI so
    /// users can hand the value off to local agents/CLIs without spelunking.
    /// </summary>
    public static string McpTokenPath =>
        System.IO.Path.Combine(SettingsManager.SettingsDirectoryPath, "mcp-token.txt");
    private volatile bool _enableMcpServer;
    private McpHttpServer? _mcpServer;
    private McpToolBridge? _mcpToolBridge;
    private string? _mcpStartupError;
    public bool IsMcpRunning => _mcpServer != null;
    public VoiceService? VoiceService => _voiceService;
    public TextToSpeechService? TextToSpeech => _textToSpeechService;
    public string McpEndpoint => McpServerUrl;
    /// <summary>Last MCP server startup error, or null if it started cleanly. Surfaced by Settings UI.</summary>
    public string? McpStartupError => _mcpStartupError;
    public void SetMcpStartupError(string? error) => _mcpStartupError = string.IsNullOrWhiteSpace(error) ? null : error;
    
    // Events
    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<SystemNotifyArgs>? NotificationRequested;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
    public event EventHandler<NodeInvokeCompletedEventArgs>? InvokeCompleted;
    public event EventHandler<NodeToolTelemetryCompletion>? ToolTelemetryCompleted;
    public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
    public event EventHandler<RecordingStateEventArgs>? RecordingStateChanged;
    public event EventHandler<NodeToastRequestedEventArgs>? ToastRequested;
    
    public bool IsScreenRecording { get; private set; }
    public bool IsCameraRecording { get; private set; }
    public bool IsAnyRecording => IsScreenRecording || IsCameraRecording;

    public bool IsConnected => _nodeClient?.IsConnected ?? false;
    public string? NodeId => _nodeClient?.NodeId;
    public bool IsPendingApproval => _nodeClient?.IsPendingApproval ?? false;
    public bool IsPaired => _nodeClient?.IsPaired ?? false;
    public string? ShortDeviceId => _nodeClient?.ShortDeviceId;
    public string? FullDeviceId => _nodeClient?.FullDeviceId;
    public string? GatewayUrl => _nodeClient?.GatewayUrl;

    /// <summary>Show the canvas window (creates it if needed).</summary>
    public void ShowCanvasWindow()
    {
        _dispatcherQueue.TryEnqueue(EnsureCanvasWindow);
    }
    
    public NodeService(
        IOpenClawLogger logger,
        DispatcherQueue dispatcherQueue,
        string dataPath,
        SettingsManager? settings = null,
        bool enableMcpServer = false,
        string? identityDataPath = null,
        Func<string?>? sharedGatewayTokenResolver = null,
        Func<int?>? browserControlPortResolver = null,
        Func<SshTunnelConfig?>? activeGatewayTunnelResolver = null,
        Func<string?>? activeGatewayUrlResolver = null,
        Func<Uri, CancellationToken, Task<bool>>? browserControlAuthorization = null,
        ExecApprovalsStore? execApprovalsStore = null)
    {
        _logger = logger;
        _dispatcherQueue = dispatcherQueue;
        _dataPath = dataPath;
        _identityDataPath = string.IsNullOrWhiteSpace(identityDataPath) ? dataPath : identityDataPath;
        _sharedGatewayTokenResolver = sharedGatewayTokenResolver;
        _browserControlPortResolver = browserControlPortResolver;
        _activeGatewayTunnelResolver = activeGatewayTunnelResolver;
        _activeGatewayUrlResolver = activeGatewayUrlResolver;
        _browserControlAuthorization = browserControlAuthorization;
        _execApprovalsStore = execApprovalsStore;
        _settings = settings;
        _enableMcpServer = enableMcpServer;
        _screenCaptureService = new ScreenCaptureService(logger);
        _screenRecordingService = new ScreenRecordingService(logger);
        _cameraCaptureService = new CameraCaptureService(logger);
    }
    
    // NodeService.ConnectAsync (gateway-connecting variant) was removed in
    // phase 5 of the connection-unification rollout. Node lifecycle is owned by
    // GatewayConnectionManager — call manager.EnsureNodeConnectedAsync (or
    // ConnectionManagerWindowsNodeConnector for the easy-button setup engine).
    // StartLocalOnlyAsync (MCP-only mode, no gateway) is unchanged.

    /// <summary>
    /// Bring up node capabilities and the local MCP server without opening a
    /// WebSocket to the gateway. Used for MCP-only mode where the tray app
    /// hosts capabilities for local MCP clients only.
    /// </summary>
    public Task StartLocalOnlyAsync()
    {
        // No gateway client at all — WebSocketClientBase requires non-empty
        // url/token, and we don't need it. Capabilities live on NodeService
        // and are consumed by the MCP bridge directly.
        _logger.Info("Starting Windows Node in MCP-only mode (no gateway)");
        _token = null;
        _mcpStartupError = null;

        try
        {
            RegisterCapabilities();
        }
        catch (Exception ex)
        {
            SetMcpStartupFailure(ex, "capability registration");
        }

        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Detach from the manager-owned node client and tear down capability state.
    /// Does NOT dispose <c>_nodeClient</c> — the client lifecycle is owned by
    /// <see cref="OpenClawTray.Services.Connection.GatewayConnectionManager"/> /
    /// <see cref="OpenClawTray.Services.Connection.NodeConnector"/>; call
    /// <c>GatewayConnectionManager.DisconnectAsync</c> to actually close the WebSocket.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await StopMcpServerAsync().ConfigureAwait(false);

        WindowsNodeClient? previous;
        lock (_clientLock)
        {
            previous = _nodeClient;
            _nodeClient = null;
        }
        if (previous != null)
        {
            // Unsubscribe but don't dispose — the connector owns the client.
            DetachClientHandlers(previous);
        }

        lock (_capabilitiesLock) { _capabilities.Clear(); }

        // Close canvas window
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _canvasWindow.Close());
            _canvasWindow = null;
        }

        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            _dispatcherQueue.TryEnqueue(() => _a2uiCanvasWindow.Close());
            _a2uiCanvasWindow = null;
        }
    }
    
    private void RegisterCapabilities()
    {
        // Hold the lock across the entire rebuild. The body is sync construction
        // (no awaits), so the lock is held briefly and an MCP tools/list arriving
        // mid-rebuild waits for a consistent snapshot rather than seeing a half-
        // populated list.
        lock (_capabilitiesLock)
        {
        _capabilities.Clear();

        // System capability (notifications + command execution). The
        // "Run system tools" toggle gates the run/run.prepare commands
        // inside the capability — the rest (notify/which/execApprovals)
        // stay registered regardless.
        _systemCapability = new SystemCapability(
            _logger,
            includeRunCommands: NodeCapabilityGating.ShouldRegisterSystemRun(_settings));
        _systemCapability.NotifyRequested += OnSystemNotify;
        _systemCapability.SetCommandRunner(BuildSystemRunRunner());
        // One coordinator per service lifetime (it serializes approvals with a
        // per-instance semaphore). Construction failures are not sticky, so a
        // later rebuild retries while this capability remains fail closed.
        _execApprovalsV2Handler ??= TryBuildExecApprovalsV2Coordinator();
        if (_execApprovalsStore is not null)
            _systemCapability.SetApprovalsStore(_execApprovalsStore);
        _systemCapability.SetV2Handler(_execApprovalsV2Handler ?? ExecApprovalV2NullHandler.Instance);

        Register(_systemCapability);

        if (NodeCapabilityGating.ShouldRegisterCanvas(_settings))
        {
            _canvasCapability = new CanvasCapability(_logger);
            _canvasCapability.PresentRequested += OnCanvasPresent;
            _canvasCapability.HideRequested += OnCanvasHide;
            _canvasCapability.NavigateRequested += OnCanvasNavigate;
            _canvasCapability.EvalRequested += OnCanvasEval;
            _canvasCapability.SnapshotRequested += OnCanvasSnapshot;
            _canvasCapability.A2UIPushRequested += OnCanvasA2UIPush;
            _canvasCapability.A2UIResetRequested += OnCanvasA2UIReset;
            _canvasCapability.A2UIDumpRequested += OnCanvasA2UIDumpAsync;
            _canvasCapability.CapsRequested += OnCanvasCapsAsync;
            Register(_canvasCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterScreen(_settings))
        {
            _screenCapability = new ScreenCapability(_logger);
            _screenCapability.CaptureRequested += OnScreenCapture;
            _screenCapability.RecordRequested += OnScreenRecord;
            Register(_screenCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterCamera(_settings))
        {
            _cameraCapability = new CameraCapability(_logger);
            _cameraCapability.ListRequested += OnCameraList;
            _cameraCapability.SnapRequested += OnCameraSnap;
            _cameraCapability.ClipRequested += OnCameraClip;
            Register(_cameraCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterLocation(_settings))
        {
            _locationCapability = new LocationCapability(_logger);
            _locationCapability.GetRequested += GetLocationAsync;
            Register(_locationCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterTts(_settings))
        {
            var settings = _settings ?? throw new InvalidOperationException("Settings are required to register text-to-speech.");
            _textToSpeechService ??= new TextToSpeechService(_logger, settings);
            _ttsCapability = new TtsCapability(_logger);
            _ttsCapability.SpeakRequested += OnTtsSpeakAsync;
            _ttsCapability.StatusRequested += OnTtsStatusAsync;
            Register(_ttsCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterStt(_settings))
        {
            // Whisper is the only STT engine. The legacy WinRT
            // SpeechRecognizer + desktop SAPI fallback was removed —
            // both stacks are old, can leak audio to the Microsoft
            // cloud (online speech), and don't activate in unpackaged
            // builds. When the Whisper model isn't downloaded yet, the
            // handlers return a clear error pointing the caller at the
            // Voice Settings page; there is no automatic fallback.
            var settings = _settings ?? throw new InvalidOperationException("Settings are required to register speech-to-text.");
            _voiceService ??= new VoiceService(_logger, settings);
            _sttCapability = new SttCapability(_logger);
            _sttCapability.TranscribeRequested += OnSttTranscribeAsync;
            _sttCapability.ListenRequested += OnSttListenAsync;
            _sttCapability.StatusRequested += OnSttStatusAsync;
            Register(_sttCapability);
        }

        if (NodeCapabilityGating.ShouldRegisterOllama(_settings))
        {
            _ollamaBackend ??= new OllamaInferenceBackend();
            _ollamaCapability ??= new OllamaCapability(_logger, _ollamaBackend);
            Register(_ollamaCapability);
        }
        else if (_ollamaCapability is not null)
        {
            _ollamaCapability.Revoke();
            _ollamaCapability = null;
        }

        // Device metadata/status capability - dispose previous provider on re-registration
        _deviceStatusProvider?.Dispose();
        _deviceStatusProvider = new DeviceStatusProvider(_logger);
        _deviceStatusProvider.StartCpuSampling();
        _deviceCapability = new DeviceCapability(_logger, _deviceStatusProvider);
        Register(_deviceCapability);

        // BrowserProxy talks to the HTTP/browser-control surface, which expects
        // the shared gateway token rather than the node WebSocket device token.
        var sharedGatewayToken = _sharedGatewayTokenResolver?.Invoke();
        var activeGatewayTunnel = _activeGatewayTunnelResolver?.Invoke();
        var browserControlPort = _browserControlPortResolver?.Invoke();
        var browserProxyBlock = NodeCapabilityGating.ResolveBrowserProxyRegistrationBlock(
            _settings,
            sharedGatewayToken,
            hasGatewayClient: _nodeClient != null,
            browserEndpointVerified: BrowserProxyActivation.IsSshBrowserEndpointVerified(
                activeGatewayTunnel,
                browserControlPort));
        if (browserProxyBlock == BrowserProxyActivation.RegistrationBlock.None)
        {
            // Tunnel state is resolved from the active GatewayRecord when a resolver is wired
            // (the normal app path), so a tunnel->direct gateway switch can't leave stale global
            // SettingsManager.UseSshTunnel routing browser.proxy (and the shared token) to the
            // old tunnel-local+2 endpoint. See BrowserProxyTunnelState for the exact contract.
            var tunnelState = BrowserProxyTunnelState.Resolve(
                activeResolverSupplied: _activeGatewayTunnelResolver != null,
                activeTunnel: activeGatewayTunnel,
                activeGatewayUrl: _activeGatewayUrlResolver?.Invoke(),
                settingsUseSshTunnel: _settings?.UseSshTunnel == true,
                settingsLocalPort: _settings?.SshTunnelLocalPort,
                settingsRemotePort: _settings?.SshTunnelRemotePort,
                settingsGatewayUrl: _settings?.GatewayUrl);
            _browserProxyCapability = new BrowserProxyCapability(
                _logger,
                _nodeClient!.GatewayUrl,
                sharedGatewayToken,
                sshRemoteGatewayPort: tunnelState.RemotePort,
                controlPortOverride: browserControlPort,
                useSshTunnel: tunnelState.Enabled,
                sshTunnelLocalPort: tunnelState.LocalPort,
                allowGatewayPortFallback: tunnelState.AllowGatewayPortFallback,
                authorizeEndpointAsync: _browserControlAuthorization);
            Register(_browserProxyCapability);
        }
        else if (browserProxyBlock != BrowserProxyActivation.RegistrationBlock.ToggleDisabled)
        {
            // Toggle is on but registration cannot proceed. Log the concrete reason so
            // "Enabled, not active yet" is never a silent dead end in diagnostics.
            _logger.Warn(
                $"[NodeService] browser capability not declared: {BrowserProxyActivation.DescribeRegistrationBlock(browserProxyBlock)}");
        }

        if (_nodeClient != null)
        {
            if (_settings?.NodeCameraEnabled != false)
                _nodeClient.SetPermission("camera.capture", true);
            if (_settings?.NodeScreenEnabled != false)
                _nodeClient.SetPermission("screen.record", true);
        }

        _logger.Info($"Capabilities registered: {string.Join(", ", _capabilities.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase))} ({_capabilities.Count} caps)");
        } // end lock

        StartMcpServer();
    }

    /// <summary>
    /// Register one capability with both NodeService and (when present) the
    /// gateway client. Single seam so adding a new capability touches one
    /// site and is exposed by every transport (gateway + MCP) automatically.
    /// </summary>
    private void Register(INodeCapability capability)
    {
        _capabilities.Add(capability);
        if (IsLocalOnlyCapability(capability))
        {
            _logger.Warn($"Capability {capability.Category} contains local-only commands and will not be registered with the gateway node transport.");
            return;
        }

        _nodeClient?.RegisterCapability(capability);
    }

    private static bool IsLocalOnlyCapability(INodeCapability capability) =>
        capability.Commands.Any(command =>
            command.StartsWith("app.connection.", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Register a capability that is only visible to local MCP clients, not
    /// the gateway. Used for app-level testing/control tools.
    /// </summary>
    public void RegisterMcpOnlyCapability(INodeCapability capability)
    {
        lock (_capabilitiesLock)
        {
            _mcpOnlyCapabilities.Add(capability);
        }
    }

    /// <summary>
    /// Adopt a <see cref="WindowsNodeClient"/> created by an outside party
    /// (typically <see cref="OpenClaw.Connection.NodeConnector"/>)
    /// and register all current capabilities on it. Called via
    /// <see cref="OpenClaw.Connection.INodeConnector.ClientCreated"/>
    /// every time the connector spins up a fresh client (initial connect AND
    /// reconnect). Idempotent on the capability list — the same capability
    /// objects get registered against the new client; <c>WindowsNodeClient</c>
    /// dedupes by category+command into its <c>_registration</c> structure.
    ///
    /// Must run synchronously before the client's outbound "connect" message
    /// is serialized — otherwise the gateway sees this node as having no
    /// advertised commands and the agent can't invoke anything.
    /// </summary>
    public void AttachClient(WindowsNodeClient client, string? bearerToken = null)
    {
        if (client is null) return;

        _token = bearerToken;

        // Hanselman review #1: serialize subscription bookkeeping so AttachClient ↔
        // DisconnectAsync ↔ a follow-up AttachClient can't leave stale handlers on
        // an old client or double-subscribe the same client. Unconditional
        // unsubscribe-then-subscribe makes the wiring idempotent regardless of
        // whether the previous client is the same instance or null.
        WindowsNodeClient? previous;
        lock (_clientLock)
        {
            previous = _nodeClient;
            if (previous != null && !ReferenceEquals(previous, client))
            {
                DetachClientHandlers(previous);
            }

            _nodeClient = client;

            // Wire NodeService event re-emitters to the manager-owned client.
            // App.OnPairingStatusChanged + OnNodeStatusChanged subscribe to NodeService events;
            // those subscriptions are stable across reconnects because the subscriptions are
            // on NodeService, not on the underlying WindowsNodeClient. (Pre-unification this
            // wiring lived in NodeService.ConnectAsync — moved here so the unified
            // manager-owned lifecycle still drives NodeService's event surface.)
            // -= before += so a re-attach of the same client (e.g. AttachClient called
            // again after a DisconnectAsync that nulled _nodeClient) doesn't double-subscribe.
            client.StatusChanged -= OnNodeStatusChanged;
            client.Disposed -= OnNodeClientDisposed;
            client.PairingStatusChanged -= OnPairingStatusChanged;
            client.HealthReceived -= OnNodeHealthReceived;
            client.GatewaySelfUpdated -= OnGatewaySelfUpdated;
            client.InvokeCompleted -= OnNodeInvokeCompleted;
            client.ToolTelemetryCompleted -= OnToolTelemetryCompleted;
            client.StatusChanged += OnNodeStatusChanged;
            client.Disposed += OnNodeClientDisposed;
            client.PairingStatusChanged += OnPairingStatusChanged;
            client.HealthReceived += OnNodeHealthReceived;
            client.GatewaySelfUpdated += OnGatewaySelfUpdated;
            client.InvokeCompleted += OnNodeInvokeCompleted;
            client.ToolTelemetryCompleted += OnToolTelemetryCompleted;
        }

        var gatewayUrl = client.GatewayUrl;
        var configuredGatewayUrl = GetConfiguredGatewayUrl();
        var token = bearerToken;
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasWindow != null && !_canvasWindow.IsClosed)
                _canvasWindow.SetTrustedGatewayOrigin(gatewayUrl, token, configuredGatewayUrl);
        });

        bool capabilitiesBuilt;
        lock (_capabilitiesLock)
        {
            capabilitiesBuilt = _capabilities.Count > 0;
        }

        _logger.Info($"[NodeService] AttachClient: capabilitiesBuilt={capabilitiesBuilt}, _capabilities.Count={_capabilities.Count}");

        // Always rebuild from current settings. The previous reconnect path
        // re-registered the cached _capabilities instances, but _capabilities
        // is only cleared in DisconnectAsync — which is never invoked on the
        // reconnect path used by App.OnSettingsSaved (CapabilityReload calls
        // ReconnectAsync, not DisconnectAsync). That left toggles like
        // NodeCanvasEnabled / NodeSystemRunEnabled silently requiring a full
        // app restart to take effect. RegisterCapabilities() clears the list,
        // rebuilds with current settings, and registers on the new _nodeClient
        // — correct for first-attach AND reconnect.
        _logger.Info("[NodeService] AttachClient: rebuilding capabilities from current settings");
        RegisterCapabilities();

        // Log final registration state for diagnostics
        _logger.Info($"[NodeService] AttachClient DONE: client.Registration.Capabilities={client.RegisteredCapabilityCount}, client.Registration.Commands={client.RegisteredCommandCount}");
    }

    private void DetachClientHandlers(WindowsNodeClient client)
    {
        client.StatusChanged -= OnNodeStatusChanged;
        client.Disposed -= OnNodeClientDisposed;
        client.PairingStatusChanged -= OnPairingStatusChanged;
        client.HealthReceived -= OnNodeHealthReceived;
        client.GatewaySelfUpdated -= OnGatewaySelfUpdated;
        client.InvokeCompleted -= OnNodeInvokeCompleted;
        client.ToolTelemetryCompleted -= OnToolTelemetryCompleted;
    }

    /// <summary>
    /// Build the exec approvals handler. Fail closed: if the
    /// coordinator cannot be built, return the null handler (typed unavailable
    /// deny) rather than falling back silently to the legacy path. The result
    /// is cached for the NodeService lifetime so the coordinator stays a
    /// singleton across capability rebuilds. The approval prompt dialog is
    /// wired and presentation is gated by the attended desktop evaluator.
    /// </summary>
    private IExecApprovalV2Handler? TryBuildExecApprovalsV2Coordinator()
    {
        try
        {
            var store = _execApprovalsStore ?? new ExecApprovalsStore(_dataPath, _logger);
            store.MigrateLegacyFileIfNeeded();
            var coordinator = new ExecApprovalsCoordinator(
                store,
                new DesktopCanPresentEvaluator(),
                new ExecApprovalV2DialogPromptHandler(_dispatcherQueue, _logger),
                _logger);
            _execApprovalsStore = store;
            _logger.Info("[EXEC-APPROVALS] V2 path enabled (prompt dialog wired; attended desktop required)");
            return coordinator;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "[EXEC-APPROVALS] V2 coordinator unavailable; " +
                "failing closed until the next capability rebuild retries", ex);
            return null;
        }
    }

    /// <summary>
    /// Build the <see cref="ICommandRunner"/> for system.run. Returns an
    /// <see cref="MxcCommandRunner"/> wrapping <see cref="DirectAppContainerExecutor"/>.
    /// The runner honors <see cref="SettingsData.SystemRunSandboxEnabled"/>
    /// by attempting MXC containment when available, preserving compatibility
    /// host fallback when MXC is unavailable unless strict fallback blocking is
    /// enabled, and rejecting unsupported sandbox request features while
    /// sandboxing remains enabled.
    /// </summary>
    private ICommandRunner BuildSystemRunRunner()
    {
        var hostRunner = new LocalCommandRunner(_logger);
        var executor = new DirectAppContainerExecutor(GetOrProbeMxcAvailability, _logger);

        // Do NOT probe synchronously here: this runs while _capabilitiesLock is held
        // (RegisterCapabilities), and a blocking wxc-exec --probe (~15s) would stall
        // capability registration / reconnect. Log from a non-blocking peek; the
        // first real probe happens lazily on the first system.run via the
        // availability gate / executor provider below (off any of our locks).
        var peeked = PeekMxcAvailability();
        if (peeked is null)
        {
            _logger.Info(
                $"[mxc] system.run runner = MxcCommandRunner " +
                $"(executor={executor.Name}; MXC availability probe deferred to first use; " +
                $"sandboxEnabled={(_settings?.SystemRunSandboxEnabled ?? true)})");
        }
        else if (peeked.CanRunSystemRunSandbox)
        {
            _logger.Info(
                $"[mxc] system.run runner = MxcCommandRunner " +
                $"(executor={executor.Name}, sandboxEnabled={(_settings?.SystemRunSandboxEnabled ?? true)})");
        }
        else
        {
            // Supported BaseContainer process containment is unavailable. The runner's top-level
            // !_isSandboxAvailable() guard will either block or use the
            // compatibility host fallback, depending on settings. The executor is
            // constructed only to satisfy the constructor contract and is never
            // invoked.
            var reason = string.Join("; ", peeked.SystemRunSandboxUnsupportedReasons);
            var unavailableMode = (_settings?.SystemRunBlockHostFallbackWhenMxcUnavailable ?? false)
                ? "commands will be blocked by strict fallback settings"
                : "commands will run through host fallback";
            _logger.Info($"[mxc] system.run runner = MxcCommandRunner (BaseContainer unavailable, {unavailableMode}: {reason})");
        }

        var settingsDirectory = SettingsManager.SettingsDirectoryPath;
        return new MxcCommandRunner(
            executor,
            hostRunner,
            () => SnapshotSettings(),
            () => settingsDirectory,
            // Re-probe on demand when sandbox availability is checked: returns the
            // cached definitive verdict, or re-probes (single-flight) after a
            // transient error / a prior SandboxUnavailableException-driven invalidation.
            () => GetOrProbeMxcAvailability().CanRunSystemRunSandbox,
            invalidateAvailability: InvalidateMxcAvailability,
            _logger);
    }

    /// <summary>
    /// Snapshot the live <see cref="SettingsManager"/> into the wire-shaped
    /// <see cref="SettingsData"/> that MxcCommandRunner / MxcPolicyBuilder consume.
    /// Defensive default keeps sandbox enabled if _settings is null.
    /// </summary>
    private SettingsData SnapshotSettings()
    {
        if (_settings is null)
            return new SettingsData
            {
                SystemRunSandboxEnabled = true,
                SystemRunBlockHostFallbackWhenMxcUnavailable = false,
                SystemRunAllowOutbound = false,
                SystemRunAllowWindowsUi = false,
            };

        return new SettingsData
        {
            SystemRunSandboxEnabled = _settings.SystemRunSandboxEnabled,
            SystemRunBlockHostFallbackWhenMxcUnavailable = _settings.SystemRunBlockHostFallbackWhenMxcUnavailable,
            SystemRunAllowOutbound = _settings.SystemRunAllowOutbound,
            SystemRunAllowWindowsUi = _settings.SystemRunAllowWindowsUi,
            // Sandbox page fields — read by MxcPolicyBuilder.ForSystemRun.
            SandboxClipboard = _settings.SandboxClipboard,
            SandboxDocumentsAccess = _settings.SandboxDocumentsAccess,
            SandboxDownloadsAccess = _settings.SandboxDownloadsAccess,
            SandboxDesktopAccess = _settings.SandboxDesktopAccess,
            // Deep-copy each SandboxCustomFolder so a concurrent UI thread mutation of
            // Access (between snapshot and policy build) can't race with us. The class
            // is mutable so a shallow copy of the list would share references.
            SandboxCustomFolders = _settings.SandboxCustomFolders is { Count: > 0 } src
                ? src.Select(f => new SandboxCustomFolder { Path = f.Path, Access = f.Access }).ToList()
                : null,
            SandboxTimeoutMs = _settings.SandboxTimeoutMs,
            SandboxMaxOutputBytes = _settings.SandboxMaxOutputBytes,
        };
    }

    private MxcAvailability? _mxcAvailability;
    private DateTime _mxcNextProbeAllowedAtUtc;
    private Task<MxcAvailability>? _mxcProbeInFlight;
    private readonly object _mxcAvailabilityLock = new();

    /// <summary>
    /// Minimum interval before re-probing after a transient probe error. Bounds the
    /// cost of re-spawning <c>wxc-exec --probe</c> while a probe error persists,
    /// while still letting a momentary glitch self-heal quickly.
    /// </summary>
    private static readonly TimeSpan MxcProbeRetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Return the current MXC availability, probing if needed. Definitive verdicts
    /// (supported / host-unsupported) are cached for the process lifetime; a transient
    /// probe error (<see cref="MxcAvailability.ProbeErrored"/>) is NOT cached
    /// permanently — once the retry window opens we re-probe so a momentary glitch
    /// doesn't pin the whole process to uncontained execution.
    /// </summary>
    /// <remarks>
    /// The blocking probe (<c>wxc-exec --probe</c>, up to ~15s) is NEVER run while
    /// holding <see cref="_mxcAvailabilityLock"/>: concurrent callers share a single
    /// in-flight probe (single-flight) and wait on it OUTSIDE the lock, so a slow
    /// probe can't serialize unrelated callers or stall lock users. The retry window
    /// opens only AFTER a probe completes, so a 15s timeout can't immediately permit a
    /// back-to-back re-probe.
    /// </remarks>
    private MxcAvailability GetOrProbeMxcAvailability()
    {
        Task<MxcAvailability> probe;
        lock (_mxcAvailabilityLock)
        {
            // Definitive verdict — cached for the process lifetime.
            if (_mxcAvailability is { ProbeErrored: false } definitive)
                return definitive;

            // Transient error — keep serving it (routes to uncontained) until the
            // retry window opens, so we don't re-probe on every command.
            if (_mxcAvailability is { ProbeErrored: true } errored
                && _mxcProbeInFlight is null
                && DateTime.UtcNow < _mxcNextProbeAllowedAtUtc)
                return errored;

            // Start a probe if none is running, otherwise join the in-flight one.
            probe = _mxcProbeInFlight ??= Task.Run(ProbeAndStoreMxcAvailability);
        }

        // Wait OUTSIDE the lock so other callers / lock users aren't blocked.
        return probe.GetAwaiter().GetResult();
    }

    private MxcAvailability ProbeAndStoreMxcAvailability()
    {
        MxcAvailability result;
        try
        {
            result = MxcAvailability.Probe(_logger);
        }
        catch (Exception ex)
        {
            // Probe() is designed not to throw, but never let an unexpected fault
            // poison the single-flight slot or leak out of the shared task.
            _logger.Warn($"[mxc] availability probe threw unexpectedly: {ex.GetType().Name}: {ex.Message}");
            result = new MxcAvailability(
                false, false, false, null,
                new[] { "MXC availability probe failed unexpectedly." }, probeErrored: true);
        }

        lock (_mxcAvailabilityLock)
        {
            _mxcAvailability = result;
            // Open the retry window only AFTER completion so a slow (timeout) probe
            // doesn't immediately allow a back-to-back re-probe.
            _mxcNextProbeAllowedAtUtc = DateTime.UtcNow + MxcProbeRetryInterval;
            _mxcProbeInFlight = null;
        }
        return result;
    }

    /// <summary>
    /// Non-blocking read of the cached MXC availability for diagnostics/logging.
    /// Returns null when nothing has been probed yet. Never spawns or waits on a
    /// probe, so it is safe to call while holding other locks (e.g. _capabilitiesLock).
    /// </summary>
    private MxcAvailability? PeekMxcAvailability()
    {
        lock (_mxcAvailabilityLock)
            return _mxcAvailability;
    }

    private void InvalidateMxcAvailability()
    {
        lock (_mxcAvailabilityLock)
        {
            _mxcAvailability = null;
            _mxcNextProbeAllowedAtUtc = default;
        }
    }

    private bool StartMcpServer()
    {
        if (!_enableMcpServer) return true;
        if (_mcpServer != null) return true;
        McpHttpServer? attempt = null;
        try
        {
            // Bridge reads the live _capabilities list every tools/list, so any
            // future Register(...) call is exposed via MCP automatically.
            // MCP-only capabilities (e.g. AppCapability) are merged in so
            // they appear in tools/list but never touch the gateway client.
            // The snapshot takes the same lock RegisterCapabilities holds,
            // so a tools/list arriving mid-rebuild observes either the old
            // or the new set — never a partially-cleared list.
            var bridge = new McpToolBridge(
                () => {
                    lock (_capabilitiesLock)
                    {
                        if (_mcpOnlyCapabilities.Count == 0)
                            return _capabilities.ToArray();
                        var merged = new List<INodeCapability>(_capabilities.Count + _mcpOnlyCapabilities.Count);
                        merged.AddRange(_capabilities);
                        merged.AddRange(_mcpOnlyCapabilities);
                        return merged.ToArray();
                    }
                },
                _logger,
                serverName: "openclaw-tray-mcp",
                serverVersion: AppVersionInfo.Version);
            bridge.ToolTelemetryCompleted += OnToolTelemetryCompleted;
            // Bearer-token auth. Token is created on first start and persists
            // alongside other build-specific app data (so OPENCLAW_TRAY_DATA_DIR
            // isolation in tests scopes the token too); CLI/agent registration
            // reads from the same path. Loopback bind + Origin/Host checks
            // remain in front; this layer rejects untrusted local processes
            // that could otherwise reach the predictable 127.0.0.1:port endpoint.
            var authToken = OpenClaw.Shared.Mcp.McpAuthToken.LoadOrCreate(McpTokenPath);
            // ACL hygiene check: warn if the token file is owned by someone
            // else, or if the DACL grants read to anyone outside
            // {current user, SYSTEM, Administrators}. Warning-only — restricting
            // ACLs is best-effort and a malicious local user can already do
            // worse than read this file. The point is operator visibility.
            var aclWarning = OpenClaw.Shared.Mcp.McpAuthToken.VerifyAcl(McpTokenPath);
            if (aclWarning != null) _logger.Warn($"[MCP] {aclWarning}");
            attempt = new McpHttpServer(bridge, McpPort, _logger, authToken);
            attempt.Start();
            _mcpServer = attempt;
            _mcpToolBridge = bridge;
            _mcpStartupError = null;
            return true;
        }
        catch (Exception ex)
        {
            // Categorize so Settings can show something actionable instead of
            // raw HRESULT text. HttpListener errors on Windows fall into a
            // small set of recurring causes on developer machines.
            _mcpStartupError = DescribeMcpStartupFailure(ex, McpPort);
            _logger.Error($"[MCP] Failed to start HTTP server on port {McpPort}: {_mcpStartupError}", ex);
            // Avoid leaking the half-constructed listener / CTS.
            try { attempt?.Dispose(); }
            catch (Exception cleanupEx)
            {
                _logger.Debug($"[MCP] Cleanup of half-started listener failed: {cleanupEx.Message}");
            }
            _mcpServer = null;
            return false;
        }
    }

    /// <summary>
    /// Translate a startup exception into a short, actionable message for the
    /// Settings UI. HttpListener wraps Win32 errors; a couple of NativeErrorCodes
    /// dominate and are worth calling out by name.
    /// </summary>
    internal static string DescribeMcpStartupFailure(Exception ex, int port) => ex switch
    {
        System.Net.HttpListenerException hle => hle.ErrorCode switch
        {
            5 => $"Access denied registering URL ACL on port {port}. Run `netsh http add urlacl url=http://127.0.0.1:{port}/ user={Environment.UserName}` from an elevated prompt.",
            32 or 183 => $"Port {port} is already in use. Stop the other process or change the MCP port.",
            _ => $"HTTP listener error {hle.ErrorCode}: {hle.Message}",
        },
        InvalidOperationException => $"Configuration error: {ex.Message}",
        _ => $"MCP server startup failed: {ex.Message}",
    };

    private void SetMcpStartupFailure(Exception ex, string phase)
    {
        _mcpStartupError = DescribeMcpStartupFailure(ex, McpPort);
        _logger.Error($"[MCP] Failed during {phase}: {_mcpStartupError}", ex);
    }

    private void StopMcpServer()
    {
        ObserveBackgroundFault(StopMcpServerAsync(), "[MCP] Dispose error");
    }

    private async Task StopMcpServerAsync()
    {
        // Awaited shutdown callers depend on this drain finishing before
        // capability-backing services are torn down.
        var server = _mcpServer;
        var bridge = _mcpToolBridge;
        _mcpServer = null;
        _mcpToolBridge = null;
        _mcpStartupError = null;

        if (server == null)
            return;

        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[MCP] Dispose error: {ex.Message}");
        }
        finally
        {
            if (bridge != null)
                bridge.ToolTelemetryCompleted -= OnToolTelemetryCompleted;
        }
    }

    public string ResetMcpToken()
    {
        var token = McpAuthToken.Reset(McpTokenPath);
        _mcpServer?.UpdateAuthToken(token);
        _logger.Info("[MCP] Bearer token rotated");
        return token;
    }

    /// <summary>
    /// Update the MCP server state at runtime (e.g. when the user toggles
    /// EnableMcpServer in the Settings UI). Starts or stops the HTTP server
    /// and ensures capabilities are registered for MCP-only mode.
    /// </summary>
    public void SetMcpEnabled(bool enabled)
    {
        lock (_clientLock)
        {
            _enableMcpServer = enabled;

            if (enabled)
            {
                if (_mcpServer != null) return; // already running

                _logger.Info("[MCP] SetMcpEnabled(true) — starting MCP server");
                _mcpStartupError = null;

                McpCapabilityEnablePlan capabilityPlan;
                lock (_capabilitiesLock)
                {
                    capabilityPlan = McpRuntimeStatePolicy.PlanCapabilityEnable(
                        hasGatewayClient: _nodeClient != null,
                        hasCapabilities: _capabilities.Count != 0);
                }

                try
                {
                    if (capabilityPlan == McpCapabilityEnablePlan.RebuildFromCurrentSettings)
                    {
                        RegisterCapabilities();
                    }
                    else
                    {
                        StartMcpServer();
                    }
                }

                catch (Exception ex)
                {
                    SetMcpStartupFailure(ex, "MCP enable");
                }

                if (_mcpServer == null && string.IsNullOrWhiteSpace(_mcpStartupError))
                {
                    _mcpStartupError = "MCP server startup failed: listener did not start.";
                    _logger.Error($"[MCP] {_mcpStartupError}");
                }
            }
            else
            {
                _logger.Info("[MCP] SetMcpEnabled(false) — stopping MCP server");
                // Always call StopMcpServer to clear stale startup errors even
                // if the server isn't running. StopMcpServer is lock-protected
                // and handles _mcpServer == null safely.
                StopMcpServer();
            }
        }
    }

    public void RefreshMcpOnlyCapabilities()
    {
        lock (_clientLock)
        {
            if (!_enableMcpServer || _mcpServer == null || _nodeClient != null)
                return;

            RegisterCapabilities();
        }
    }

    public void ApplyOllamaPermission(bool enabled)
    {
        lock (_capabilitiesLock)
        {
            if (!enabled)
            {
                if (_ollamaCapability is null)
                    return;

                _ollamaCapability.Revoke();
                _capabilities.Remove(_ollamaCapability);
                _ollamaCapability = null;
                return;
            }

            _ollamaBackend ??= new OllamaInferenceBackend();
            _ollamaCapability ??= new OllamaCapability(_logger, _ollamaBackend);
            if (_capabilities.Count != 0 &&
                !_capabilities.Contains(_ollamaCapability))
            {
                _capabilities.Add(_ollamaCapability);
            }
        }
    }

    public GatewayNodeInfo? GetLocalNodeInfo()
    {
        if (_nodeClient == null)
            return null;

        INodeCapability[] capabilitySnapshot;
        lock (_capabilitiesLock)
        {
            capabilitySnapshot = _capabilities.ToArray();
        }

        var capabilities = capabilitySnapshot.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var commands = capabilitySnapshot.SelectMany(c => c.Commands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new GatewayNodeInfo
        {
            NodeId = _nodeClient.NodeId ?? _nodeClient.FullDeviceId ?? "",
            DisplayName = $"Windows Node ({Environment.MachineName})",
            Mode = "node",
            Status = IsConnected ? "connected" : "disconnected",
            Platform = "windows",
            DeviceFamily = "Windows",
            LastSeen = DateTime.UtcNow,
            IsOnline = IsConnected,
            Capabilities = capabilities,
            Commands = commands,
            DisabledCommands = BuildDisabledCommands(),
            CapabilityCount = capabilities.Count,
            CommandCount = commands.Count,
            Permissions = BuildLocalPermissions()
        };
    }

    private List<string> BuildDisabledCommands()
    {
        var disabled = new List<string>();
        if (_settings?.NodeCanvasEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.SafeCompanionCommands.Where(command => command.StartsWith("canvas.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeScreenEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.MacNodeParityCommands.Where(command => command.StartsWith("screen.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeCameraEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.MacNodeParityCommands.Where(command => command.StartsWith("camera.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeLocationEnabled == false)
            disabled.AddRange(CommandCenterCommandGroups.SafeCompanionCommands.Where(command => command.StartsWith("location.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeBrowserProxyEnabled == false)
            disabled.Add("browser.proxy");
        if (_settings?.NodeSystemRunEnabled == false)
            disabled.AddRange(new[] { "system.run", "system.run.prepare" });
        if (_settings?.NodeSttEnabled != true)
            disabled.Add(SttCapability.TranscribeCommand);
        if (_settings?.NodeTtsEnabled != true)
            disabled.AddRange(CommandCenterCommandGroups.DangerousCommands.Where(command => command.StartsWith("tts.", StringComparison.OrdinalIgnoreCase)));
        if (_settings?.NodeSttEnabled != true)
            disabled.AddRange(new[] { "stt.listen", "stt.status" });
        if (_settings?.NodeOllamaInferenceEnabled != true)
            disabled.AddRange(new[] { OllamaCapability.ModelsCommand, OllamaCapability.ChatCommand });
        return disabled;
    }

    private Dictionary<string, bool> BuildLocalPermissions()
    {
        var permissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (_settings?.NodeCameraEnabled != false)
            permissions["camera.capture"] = true;
        if (_settings?.NodeScreenEnabled != false)
            permissions["screen.record"] = true;
        return permissions;
    }
    
    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        _logger.Info($"Node status changed: {status}");
        if (status == ConnectionStatus.Connected)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                    _canvasWindow.SetTrustedGatewayOrigin(GatewayUrl, _token, GetConfiguredGatewayUrl());
            });
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                    _canvasWindow.SetTrustedGatewayOrigin(null);
            });
        }
        StatusChanged?.Invoke(this, status);
    }

    private void OnNodeClientDisposed(object? sender, EventArgs args)
    {
        var retired = false;
        lock (_clientLock)
        {
            if (sender is WindowsNodeClient client && ReferenceEquals(_nodeClient, client))
            {
                DetachClientHandlers(client);
                _nodeClient = null;
                _token = null;
                retired = true;
            }
        }

        if (!retired)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasWindow != null && !_canvasWindow.IsClosed)
                _canvasWindow.SetTrustedGatewayOrigin(null);
        });
    }
    
    private void OnPairingStatusChanged(object? sender, PairingStatusEventArgs args)
    {
        // Guard the slice — a malformed/missing/short device id would otherwise
        // throw out of the event handler and suppress PairingStatusChanged,
        // hiding the very pairing problem the listener is trying to diagnose.
        var displayId = string.IsNullOrEmpty(args.DeviceId)
            ? "unknown"
            : args.DeviceId[..Math.Min(16, args.DeviceId.Length)];
        _logger.Info($"Pairing status changed: {args.Status} (device: {displayId}...)");
        PairingStatusChanged?.Invoke(this, args);
    }

    private void OnNodeHealthReceived(object? sender, JsonElement payload)
    {
        if (payload.TryGetProperty("channels", out var channels))
        {
            var parsed = ChannelHealthParser.Parse(channels);
            _logger.Info(parsed.Length > 0
                ? $"Node health channels: {string.Join(", ", parsed.Select(c => $"{c.Name}={c.Status}"))}"
                : "Node health channels: none");
            ChannelHealthUpdated?.Invoke(this, parsed);
        }
    }

    private void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo info)
    {
        GatewaySelfUpdated?.Invoke(this, info);
    }

    private void OnNodeInvokeCompleted(object? sender, NodeInvokeCompletedEventArgs args)
    {
        InvokeCompleted?.Invoke(this, args);
    }

    private void OnToolTelemetryCompleted(object? sender, NodeToolTelemetryCompletion completion)
    {
        ToolTelemetryCompleted?.Invoke(this, completion);
    }
    
    #region System Capability Handlers
    
    private void OnSystemNotify(object? sender, SystemNotifyArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            NotificationRequested?.Invoke(this, args);
        });
    }

    #endregion
    
    #region Canvas Capability Handlers
    
    private void OnCanvasPresent(object? sender, CanvasPresentArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Web canvas is taking the foreground — close the native A2UI window so
                // the user only sees the most-recently-targeted surface (last-write-wins).
                CloseA2UICanvasWindow();

                // Create or reuse canvas window
                if (_canvasWindow == null || _canvasWindow.IsClosed)
                {
                    _canvasWindow = new CanvasWindow();
                    _canvasWindow.SetTrustedGatewayOrigin(GatewayUrl, _token, GetConfiguredGatewayUrl());
                }

                // Configure window
                _canvasWindow.Title = args.Title;
                _canvasWindow.SetSize(args.Width, args.Height);
                _canvasWindow.SetPosition(args.X, args.Y);
                _canvasWindow.SetAlwaysOnTop(args.AlwaysOnTop);

                // Load content
                if (!string.IsNullOrEmpty(args.Url))
                {
                    _canvasWindow.Navigate(args.Url);
                }
                else if (!string.IsNullOrEmpty(args.Html))
                {
                    _canvasWindow.LoadHtml(args.Html);
                }

                // Show window
                _canvasWindow.Activate();
                _canvasWindow.BringToFront(args.AlwaysOnTop);

                _logger.Info($"Canvas presented: {args.Width}x{args.Height}");
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas present failed", ex);
            }
        });
    }

    private void CloseWebCanvasWindow()
    {
        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            try { _canvasWindow.Close(); }
            catch (Exception ex) { _logger.Debug($"NodeService: CanvasWindow.Close failed: {ex.Message}"); }
        }
        _canvasWindow = null;
    }

    private void CloseA2UICanvasWindow()
    {
        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            try { _a2uiCanvasWindow.Close(); }
            catch (Exception ex) { _logger.Debug($"NodeService: A2UICanvasWindow.Close failed: {ex.Message}"); }
        }
        _a2uiCanvasWindow = null;
    }
    
    private void OnCanvasHide(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    _canvasWindow.Close();
                    _canvasWindow = null;
                    _logger.Info("Canvas hidden");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas hide failed", ex);
            }
        });
    }
    
    /// <summary>
    /// Service a <c>canvas.navigate</c> request inside the WebView canvas.
    /// CanvasCapability has already validated the URL; re-validate here before
    /// handing it to WebView2.
    /// </summary>
    private async Task<string> OnCanvasNavigate(string url)
    {
        if (!HttpUrlValidator.TryParse(url, out var canonical, out var validationError))
        {
            _logger.Warn($"OnCanvasNavigate rejected (validator): {validationError}");
            throw new InvalidOperationException($"Invalid url: {validationError}");
        }

        var risk = await EnrichWithDnsRiskAsync(HttpUrlRiskEvaluator.Evaluate(canonical!)).ConfigureAwait(false);
        if (risk.RequiresConfirmation)
        {
            _logger.Warn($"Canvas navigate unsupported in canvas: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(risk.CanonicalOrigin)}");
            return "unsupported_in_canvas";
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                CloseA2UICanvasWindow();
                EnsureCanvasWindow();
                if (_canvasWindow == null)
                    throw new InvalidOperationException("Canvas window unavailable");

                _canvasWindow.Navigate(canonical!);
                _canvasWindow.BringToFront(false);
                _logger.Info($"Canvas navigate -> canvas: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(canonical)}");
                tcs.TrySetResult("canvas");
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Failed to dispatch canvas.navigate to UI thread"));
        }

        return await WaitWithTimeout(tcs.Task, cts, "canvas.navigate");
    }

    /// <summary>
    /// Decide whether to launch given an enriched risk profile, prompting the
    /// user when required while bounding prompt frequency:
    ///   - HostKey in the deny cooldown → silently refuse (recent denial).
    ///   - HostKey already in the session allowlist → launch.
    ///   - Concurrent request for the same HostKey → await the existing prompt
    ///     and inherit its decision rather than stacking a duplicate prompt.
    ///   - Otherwise: hold the global single-prompt gate, show the prompt,
    ///     record cooldown on Deny.
    /// </summary>
    private async Task<bool> ShouldLaunchAfterPromptAsync(HttpUrlRiskProfile pinnedRisk)
    {
        if (!pinnedRisk.RequiresConfirmation || _allowedNavigationHosts.ContainsKey(pinnedRisk.HostKey))
            return true;

        if (_navigationDenyCooldown.TryGetValue(pinnedRisk.HostKey, out var expiresAt))
        {
            if (DateTimeOffset.UtcNow < expiresAt)
            {
                _logger.Warn($"Canvas navigate auto-denied (cooldown): {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)}");
                return false;
            }
            // Stale entry — drop it. A racing concurrent request can re-add via
            // the deny path below; this is just opportunistic cleanup.
            _navigationDenyCooldown.TryRemove(pinnedRisk.HostKey, out _);
        }

        // Coalesce: if a prompt is already pending for this host, await its
        // outcome instead of showing a second prompt for the same destination.
        // Use a TaskCompletionSource so all waiters resolve atomically.
        var tcs = new TaskCompletionSource<UrlNavigationApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _pendingNavigationPrompts.GetOrAdd(pinnedRisk.HostKey, tcs.Task);
        if (!ReferenceEquals(existing, tcs.Task))
        {
            var inherited = await existing.ConfigureAwait(false);
            return inherited.Kind != UrlNavigationApprovalDecisionKind.Deny;
        }

        try
        {
            // Serialize prompt display globally — multiple HostKeys racing must
            // not stack overlapping topmost MessageBoxes either.
            await _navigationPromptGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Re-check cooldown / allowlist now that we hold the gate — a
                // prior prompt's Deny may have populated the cooldown while we
                // were queued.
                if (_allowedNavigationHosts.ContainsKey(pinnedRisk.HostKey))
                {
                    var allowDecision = UrlNavigationApprovalDecision.AllowOnce();
                    tcs.TrySetResult(allowDecision);
                    return true;
                }
                if (_navigationDenyCooldown.TryGetValue(pinnedRisk.HostKey, out var nowExpires)
                    && DateTimeOffset.UtcNow < nowExpires)
                {
                    var denyDecision = UrlNavigationApprovalDecision.Deny("cooldown");
                    tcs.TrySetResult(denyDecision);
                    _logger.Warn($"Canvas navigate auto-denied (cooldown): {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)}");
                    return false;
                }

                var decision = await new UrlNavigationApprovalService(_logger)
                    .RequestAsync(pinnedRisk, BuildNavigationAgentIdentity())
                    .ConfigureAwait(false);
                tcs.TrySetResult(decision);

                if (decision.Kind == UrlNavigationApprovalDecisionKind.Deny)
                {
                    _navigationDenyCooldown[pinnedRisk.HostKey] = DateTimeOffset.UtcNow + NavigationDenyCooldownDuration;
                    _logger.Warn($"Canvas navigate denied before WebView navigation: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(pinnedRisk.CanonicalOrigin)} ({decision.Reason ?? "user denied"})");
                    return false;
                }
                // AllowHost (session-allowlist) is currently unreachable from
                // the Win32 prompt — Yes maps to AllowOnce only. The session
                // allowlist remains as scaffolding for a future Fluent
                // ContentDialog prompt (worklist T2-43).
                return true;
            }
            finally
            {
                _navigationPromptGate.Release();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _pendingNavigationPrompts.TryRemove(pinnedRisk.HostKey, out _);
        }
    }

    /// <summary>
    /// If the URL's host is a DNS name, resolve it and treat any non-public
    /// answer as a Reason. Returns the input profile unchanged for IP literals
    /// or when DNS resolution fails (a failed DNS lookup is its own
    /// confirmation trigger).
    /// </summary>
    private static async Task<HttpUrlRiskProfile> EnrichWithDnsRiskAsync(HttpUrlRiskProfile risk)
    {
        if (!Uri.TryCreate(risk.CanonicalUrl, UriKind.Absolute, out var uri)) return risk;
        if (System.Net.IPAddress.TryParse(uri.Host, out _)) return risk;

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, cts.Token).ConfigureAwait(false);
            var extra = new List<string>(risk.Reasons);
            bool anyNonPublic = false;
            foreach (var ip in addresses)
            {
                if (!HttpUrlRiskEvaluator.IsPublicAddress(ip))
                {
                    anyNonPublic = true;
                    extra.Add($"DNS resolved '{uri.Host}' to non-public address {ip}");
                }
            }
            if (!anyNonPublic) return risk;
            var merged = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return risk with { RequiresConfirmation = true, Reasons = merged };
        }
        catch (Exception ex)
        {
            // Failed lookup → require prompt: better to ask than to ship the
            // user to an unverifiable destination. The reason already carries the
            // exception message back to the caller (and on into the user-facing
            // confirmation), so the swallow here is intentional. Static method has
            // no logger; emit a Trace breadcrumb in addition to the Reasons
            // round-trip so the failure is also visible in debug traces.
            System.Diagnostics.Trace.WriteLine($"NodeService.EnrichWithDnsRiskAsync: DNS lookup failed for '{uri.Host}': {ex.GetType().Name}: {ex.Message}");
            var extra = new List<string>(risk.Reasons) { $"DNS resolution failed for '{uri.Host}': {ex.Message}" };
            return risk with { RequiresConfirmation = true, Reasons = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
    }

    /// <summary>
    /// Hand off to the OS default browser via ShellExecuteEx, then close any
    /// open in-app canvas surfaces (web or A2UI) on the dispatcher. Failures
    /// are logged but never thrown — callers expect a fire-and-forget shape.
    /// </summary>
    private void LaunchInDefaultBrowser(string canonical)
    {
        if (SuppressExternalBrowserLaunches)
        {
            _logger.Info($"Canvas navigate suppressed external browser launch: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(canonical)}");
            return;
        }

        // Process.Start with UseShellExecute=true wraps ShellExecuteEx, which
        // routes the URL to the user's registered http/https handler — never
        // to a script host or file association — given the validator already
        // restricted the scheme.
        //
        // Note: this used to close any open canvas windows after launch. That
        // made sense when canvas == WebView2 and navigate implied "you don't
        // need this frame anymore." With native A2UI the canvas is a control
        // surface (dashboard / launcher), not a browser frame — clicking a
        // link in a dashboard shouldn't nuke the dashboard. Lifecycle is now
        // explicit: agents that want the canvas dismissed after a navigate
        // should follow up with canvas.hide or deleteSurface.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = canonical,
                UseShellExecute = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            _logger.Info($"Canvas navigate → default browser: {OpenClaw.Shared.UrlLogSanitizer.Sanitize(canonical)}");
        }
        catch (Exception ex)
        {
            _logger.Error("Canvas navigate failed", ex);
        }
    }

    private string BuildNavigationAgentIdentity()
    {
        var device = _nodeClient?.ShortDeviceId ?? _nodeClient?.FullDeviceId ?? "local MCP";
        var gateway = _nodeClient?.GatewayUrl;
        return string.IsNullOrWhiteSpace(gateway)
            ? device
            : $"{device} via {GatewayUrlHelper.SanitizeForDisplay(gateway)}";
    }

    private async Task<string> OnCanvasEval(string script)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();

        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var result = await _canvasWindow.EvalAsync(script);
                    tcs.TrySetResult(result);
                }
                else if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
                {
                    // Native A2UI surface has no JS runtime; surface a structured error
                    // so callers can branch instead of pattern-matching free-text.
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_EVAL_UNAVAILABLE: native A2UI renderer has no JS runtime; use canvas.a2ui.dump for state introspection"));
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_NOT_OPEN: no canvas window is currently open"));
                }
            }
            catch (Exception ex)
            {
                // CanvasCapability.HandleEval is the authoritative Error logger
                // for this exception (it logs after the exception propagates via
                // TrySetException). Use Debug here as a dispatcher-path breadcrumb
                // to avoid mixed-severity duplicate logging for one fault.
                _logger.Debug($"NodeService: canvas.eval dispatcher caught exception: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));

        return await WaitWithTimeout(tcs.Task, cts, "canvas.eval");
    }

    private async Task<string> OnCanvasSnapshot(CanvasSnapshotArgs args)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();

        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                if (_canvasWindow != null && !_canvasWindow.IsClosed)
                {
                    var base64 = await _canvasWindow.CaptureSnapshotAsync(args.Format);
                    tcs.TrySetResult(base64);
                }
                else if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
                {
                    // Render the native XAML surface to PNG/JPEG via RenderTargetBitmap
                    // so vision pipelines and regression diffs keep working post-cutover.
                    var base64 = await _a2uiCanvasWindow.CaptureSnapshotAsync(args.Format);
                    tcs.TrySetResult(base64);
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        "CANVAS_NOT_OPEN: no canvas window is currently open"));
                }
            }
            catch (Exception ex)
            {
                // CanvasCapability.HandleSnapshot logs at Error after propagation
                // via TrySetException. Use Debug here as a dispatcher-path
                // breadcrumb to avoid mixed-severity duplicate logging.
                _logger.Debug($"NodeService: canvas.snapshot dispatcher caught exception: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));

        return await WaitWithTimeout(tcs.Task, cts, "canvas.snapshot");
    }

    private Task<string> OnCanvasA2UIDumpAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_a2uiCanvasWindow == null || _a2uiCanvasWindow.IsClosed)
                {
                    tcs.TrySetResult(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        renderer = "none",
                        a2uiVersion = "0.8",
                        surfaceCount = 0,
                        surfaces = new { },
                    }));
                    return;
                }
                tcs.TrySetResult(_a2uiCanvasWindow.GetStateSnapshot());
            }
            catch (Exception ex)
            {
                // CanvasCapability.HandleA2UIDump logs at Error after propagation
                // via TrySetException. Use Debug here as a dispatcher-path
                // breadcrumb to avoid mixed-severity duplicate logging.
                _logger.Debug($"NodeService: canvas.a2ui.dump dispatcher caught exception: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));
        return tcs.Task;
    }

    private Task<string> OnCanvasCapsAsync()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                bool nativeOpen = _a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed;
                bool webOpen = _canvasWindow != null && !_canvasWindow.IsClosed;
                var caps = new
                {
                    renderer = nativeOpen ? "native" : (webOpen ? "web" : "none"),
                    eval = webOpen,
                    snapshot = webOpen || nativeOpen,
                    // navigate is always available: when no web canvas is open
                    // we fall back to launching the OS default browser. The
                    // navigate response carries an "opener" field so the agent
                    // can tell which path was taken.
                    navigate = true,
                    a2ui = new
                    {
                        version = "0.8",
                        push = true,
                        reset = true,
                        introspect = nativeOpen,
                    },
                };
                tcs.TrySetResult(System.Text.Json.JsonSerializer.Serialize(caps));
            }
            catch (Exception ex)
            {
                // CanvasCapability.HandleCaps logs at Error after propagation via
                // TrySetException. Use Debug here as a dispatcher-path breadcrumb
                // to avoid mixed-severity duplicate logging.
                _logger.Debug($"NodeService: canvas.caps dispatcher caught exception: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        if (!enqueued)
            tcs.TrySetException(new InvalidOperationException("CANVAS_DISPATCHER_UNAVAILABLE: dispatcher queue rejected"));
        return tcs.Task;
    }

    /// <summary>
    /// Awaits a dispatcher-bridged TCS with a timeout so that canvas commands
    /// return a tool error instead of hanging indefinitely when the UI thread
    /// dispatcher is not pumping (e.g. headless CI). Cancels the CTS on timeout
    /// so the enqueued callback skips execution.
    /// </summary>
    private static async Task<string> WaitWithTimeout(Task<string> task, CancellationTokenSource cts, string command, int timeoutSeconds = 15)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != task)
        {
            cts.Cancel();
            throw new TimeoutException(
                $"CANVAS_TIMEOUT: {command} did not complete within {timeoutSeconds}s — the UI dispatcher may not be pumping");
        }
        return await task; // propagate the result or exception
    }

    private void EnsureCanvasWindow()
    {
        if (_canvasWindow == null || _canvasWindow.IsClosed)
        {
            _canvasWindow = new CanvasWindow();
            _canvasWindow.SetTrustedGatewayOrigin(GatewayUrl, _token, GetConfiguredGatewayUrl());
        }
        _canvasWindow?.Activate();
    }

    private string? GetConfiguredGatewayUrl() => _activeGatewayUrlResolver?.Invoke();

    // Mutable context shared with GatewayActionTransport. SessionKey is updated
    // from push props (when the agent supplies one); host/instance stay tied to
    // the node client identity. Default sessionKey is "main", matching Android's
    // resolveMainSessionKey() fallback.
    private sealed class GatewayActionContext : IGatewayActionContext
    {
        private readonly Func<WindowsNodeClient?> _client;
        private string _sessionKey = "main";
        public GatewayActionContext(Func<WindowsNodeClient?> client) { _client = client; }
        public string SessionKey
        {
            get => _sessionKey;
            set => _sessionKey = string.IsNullOrWhiteSpace(value) ? "main" : value.Trim();
        }
        public string Host => _client()?.DisplayName ?? $"Windows Node ({Environment.MachineName})";
        public string InstanceId => _client()?.FullDeviceId.ToLowerInvariant() ?? string.Empty;
    }

    private GatewayActionContext? _actionContext;

    /// <summary>
    /// Lazily build the action dispatcher + media resolver shared by the
    /// native A2UI canvas. The dispatcher routes outbound user actions to
    /// the gateway when connected, falling back to a logger-only sink for
    /// MCP-only mode (a future MCP notifications channel will replace it).
    /// </summary>
    private ActionDispatcher GetOrCreateActionDispatcher()
    {
        if (_actionDispatcher != null) return _actionDispatcher;

        _actionContext = new GatewayActionContext(() => _nodeClient);
        var transports = new IA2UIActionTransport[]
        {
            new GatewayActionTransport(() => _nodeClient, _actionContext, _logger),
            new LoggingActionTransport(_logger),
        };
        _actionDispatcher = new ActionDispatcher(transports, _logger);
        return _actionDispatcher;
    }

    /// <summary>
    /// Pull <c>sessionKey</c> out of the push props blob and update the action
    /// context so subsequent button clicks route to the same session. Silently
    /// no-ops when props is malformed or doesn't include a sessionKey — the
    /// previous (or default "main") value stays in effect.
    /// </summary>
    private void UpdateSessionKeyFromPushProps(string? propsJson)
    {
        if (_actionContext == null) return;
        if (string.IsNullOrWhiteSpace(propsJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(propsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sessionKey", out var sk) &&
                sk.ValueKind == JsonValueKind.String)
            {
                var v = sk.GetString();
                if (!string.IsNullOrWhiteSpace(v)) _actionContext.SessionKey = v!;
            }
        }
        catch (Exception ex)
        {
            // Bad props JSON is a gateway/agent bug, not an action-routing bug.
            // Keep the previous sessionKey rather than failing the push.
            _logger.Debug($"NodeService: Action push props JSON parse failed (sessionKey retained): {ex.Message}");
        }
    }

    private MediaResolver GetOrCreateMediaResolver()
    {
        if (_mediaResolver != null) return _mediaResolver;
        _mediaResolver = new MediaResolver(_logger);
        // Settings.A2UIImageHosts is the single source of truth for HTTPS image
        // fetching. Empty list = inline data: only, which is the safe default.
        if (_settings?.A2UIImageHosts is { Count: > 0 } hosts)
        {
            foreach (var host in hosts) _mediaResolver.AllowHost(host);
        }
        return _mediaResolver;
    }

    private void EnsureA2UICanvasWindow()
    {
        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed) return;

        // Native A2UI is taking the foreground — close the legacy WebView2 canvas
        // so its placeholder doesn't mask the rendered surface.
        CloseWebCanvasWindow();

        var actions = GetOrCreateActionDispatcher();
        var media = GetOrCreateMediaResolver();
        _a2uiCanvasWindow = new A2UICanvasWindow(actions, media, _logger);
        _a2uiCanvasWindow.Activate();
        _a2uiCanvasWindow.BringToFront(false);
    }

    private void OnCanvasA2UIPush(object? sender, CanvasA2UIArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                EnsureA2UICanvasWindow();
                if (_a2uiCanvasWindow == null)
                {
                    _logger.Error("Canvas A2UI push failed: native canvas window not available");
                    return;
                }
                // Pick up an explicit sessionKey from props if the agent supplied one,
                // so a Button click on this surface routes back to the same session.
                UpdateSessionKeyFromPushProps(args.Props);
                _a2uiCanvasWindow.Push(args.Jsonl ?? string.Empty);
                _a2uiCanvasWindow.BringToFront(false);
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas A2UI push failed", ex);
            }
        });
    }

    private void OnCanvasA2UIReset(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_a2uiCanvasWindow == null || _a2uiCanvasWindow.IsClosed)
                {
                    _logger.Debug("Canvas A2UI reset: no native canvas to reset");
                    return;
                }
                _a2uiCanvasWindow.Reset();
                _logger.Info("Canvas A2UI reset");
            }
            catch (Exception ex)
            {
                _logger.Error("Canvas A2UI reset failed", ex);
            }
        });
    }
    
    #endregion
    
    #region Screen Capability Handlers

    private void RequestNodeToast(
        string title,
        string message,
        string dedupeKey,
        AppNotificationSeverity severity = AppNotificationSeverity.Informational,
        string category = "node.invoke",
        bool mirrorInApp = false)
    {
        var appNotification = mirrorInApp
            ? AppNotificationMapper.FromNodeActivity(
                title,
                message,
                category,
                severity,
                dedupeKey)
            : null;
        ToastRequested?.Invoke(
            this,
            new NodeToastRequestedEventArgs(
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(message),
                appNotification));
    }
    
    private async Task<ScreenCaptureResult> OnScreenCapture(
        ScreenCaptureArgs args,
        CancellationToken cancellationToken)
    {
        if (_screenCaptureService == null)
        {
            throw new InvalidOperationException("Screen capture service not available");
        }
        
        return await SensitiveCaptureExecutor.ExecuteAsync(
            SensitiveCapturePlans.ScreenSnapshot,
            EnsureCaptureConsentAsync,
            ShowSensitiveCaptureIndicator,
            ct => _screenCaptureService.CaptureAsync(args, ct),
            cancellationToken);
    }

    private async Task<ScreenRecordResult> OnScreenRecord(
        ScreenRecordArgs args,
        CancellationToken cancellationToken)
    {
        if (_screenRecordingService == null)
        {
            throw new InvalidOperationException("Screen recording service not available");
        }

        await EnsureCaptureConsentAsync(CaptureConsentType.Screen, cancellationToken);
        await ShowRecordingCountdownAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        SetRecordingState(RecordingType.Screen, true, args.DurationMs);
        try
        {
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_ScreenRecordingStarted"),
                LocalizationHelper.GetString("Toast_ScreenRecordingStartedDetail"),
                "node:screen-recording-started");
            var result = await _screenRecordingService.RecordAsync(args, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_ScreenRecordingComplete"),
                LocalizationHelper.GetString("Toast_ScreenRecordingCompleteDetail"),
                "node:screen-recording-complete",
                AppNotificationSeverity.Success);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_ScreenRecordingFailed"),
                LocalizationHelper.GetString("Toast_ScreenRecordingFailedDetail"),
                "node:screen-recording-failed",
                AppNotificationSeverity.Error,
                mirrorInApp: true);
            throw;
        }
        finally
        {
            SetRecordingState(RecordingType.Screen, false);
        }
    }
    
    #endregion
    
    #region Camera Capability Handlers
    
    private Task<CameraInfo[]> OnCameraList(CancellationToken cancellationToken)
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }
        
        return _cameraCaptureService.ListCamerasAsync(cancellationToken);
    }
    
    private async Task<CameraSnapResult> OnCameraSnap(
        CameraSnapArgs args,
        CancellationToken cancellationToken)
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }

        try
        {
            return await SensitiveCaptureExecutor.ExecuteAsync(
                SensitiveCapturePlans.CameraSnap,
                EnsureCaptureConsentAsync,
                ShowSensitiveCaptureIndicator,
                ct => _cameraCaptureService.SnapAsync(args, ct),
                cancellationToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_CameraBlocked"),
                LocalizationHelper.GetString("Toast_CameraBlockedDetail"),
                "node:camera-blocked",
                AppNotificationSeverity.Error,
                mirrorInApp: true);
            throw new InvalidOperationException(
                "Camera access blocked. Enable camera access for desktop apps in Windows Privacy settings.",
                ex);
        }
    }

    private async Task<CameraClipResult> OnCameraClip(
        CameraClipArgs args,
        CancellationToken cancellationToken)
    {
        if (_cameraCaptureService == null)
        {
            throw new InvalidOperationException("Camera capture service not available");
        }

        await EnsureCaptureConsentAsync(CaptureConsentType.Camera, cancellationToken);
        await ShowRecordingCountdownAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        SetRecordingState(RecordingType.Camera, true, args.DurationMs);
        try
        {
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_CameraRecordingStarted"),
                LocalizationHelper.GetString("Toast_CameraRecordingStartedDetail"),
                "node:camera-recording-started");
            var result = await _cameraCaptureService.ClipAsync(args, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_CameraRecordingComplete"),
                LocalizationHelper.GetString("Toast_CameraRecordingCompleteDetail"),
                "node:camera-recording-complete",
                AppNotificationSeverity.Success);
            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            RequestNodeToast(
                LocalizationHelper.GetString("Toast_CameraBlocked"),
                LocalizationHelper.GetString("Toast_CameraBlockedDetail"),
                "node:camera-blocked",
                AppNotificationSeverity.Error,
                mirrorInApp: true);
            throw new InvalidOperationException(
                "Camera access blocked. Enable camera access for desktop apps in Windows Privacy settings.",
                ex);
        }
        finally
        {
            SetRecordingState(RecordingType.Camera, false);
        }
    }
    
    private Task<LocationResult> GetLocationAsync(
        LocationGetArgs args,
        CancellationToken cancellationToken)
    {
        return SensitiveCaptureExecutor.ExecuteAsync(
            SensitiveCapturePlans.LocationGet,
            EnsureCaptureConsentAsync,
            ShowSensitiveCaptureIndicator,
            ct => ReadLocationAsync(args, ct),
            cancellationToken);
    }

    private static async Task<LocationResult> ReadLocationAsync(
        LocationGetArgs args,
        CancellationToken cancellationToken)
    {
        var geolocator = new global::Windows.Devices.Geolocation.Geolocator
        {
            DesiredAccuracy = args.Accuracy == "precise"
                ? global::Windows.Devices.Geolocation.PositionAccuracy.High
                : global::Windows.Devices.Geolocation.PositionAccuracy.Default
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(args.TimeoutMs));
        var position = await geolocator.GetGeopositionAsync().AsTask(timeoutCts.Token);
        
        return new LocationResult
        {
            Latitude = position.Coordinate.Point.Position.Latitude,
            Longitude = position.Coordinate.Point.Position.Longitude,
            AccuracyMeters = position.Coordinate.Accuracy,
            TimestampMs = position.Coordinate.Timestamp.ToUnixTimeMilliseconds()
        };
    }

    private void ShowSensitiveCaptureIndicator(SensitiveCapturePlan plan)
    {
        RequestNodeToast(
            LocalizationHelper.GetString(plan.ToastTitleResourceKey),
            LocalizationHelper.GetString(plan.ToastDetailResourceKey),
            plan.NotificationKey);
    }

    private Task<TtsSpeakResult> OnTtsSpeakAsync(TtsSpeakArgs args, CancellationToken cancellationToken)
    {
        if (_textToSpeechService == null)
            throw new InvalidOperationException("Text-to-speech service not available");

        return _textToSpeechService.SpeakAsync(args, cancellationToken);
    }

    private Task<TtsStatusResult> OnTtsStatusAsync(CancellationToken cancellationToken)
    {
        if (_textToSpeechService == null)
            throw new InvalidOperationException("Text-to-speech service not available");

        return Task.FromResult(_textToSpeechService.GetStatus());
    }

    // ============================================================
    // ============================================================
    // STT handlers
    //
    // Single engine: VoiceService (Whisper.net + NAudio + Silero VAD).
    // The legacy WinRT/SAPI engine and engine selector were removed; the
    // current path stays local and uses one consent/error model.
    //
    // When the Whisper model isn't downloaded yet, every stt.* call
    // returns a clear error pointing the caller at the Voice Settings
    // page download button. There is no automatic fallback engine.
    //
    // Privacy: handlers never include caller-supplied args or runtime
    // details in error messages. SttCapability already wraps the
    // response surface; this layer only logs locally on failure.
    // ============================================================

    private bool IsWhisperReady() => _voiceService != null && _voiceService.IsWhisperReady;

    private static string ResolveListenLanguage(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var normalized = SttCapability.NormalizeLanguageTag(configured!);
            if (normalized != null) return normalized;
        }
        return SttCapability.AutoLanguage;
    }

    private async Task<SttTranscribeResult> OnSttTranscribeAsync(
        SttTranscribeArgs args,
        CancellationToken cancellationToken)
    {
        if (_voiceService == null)
            throw new InvalidOperationException("Voice service not available");
        // Check the file on disk, NOT IsWhisperReady (which is "loaded into
        // memory"). The TranscribeFixedDurationAsync path calls
        // EnsureInitializedAsync internally; that triggers the lazy
        // file→memory load. Failing here on a freshly-launched tray that
        // has the file but hasn't loaded it yet would be a paper cut for
        // every MCP caller.
        if (!_voiceService.IsModelDownloaded)
            throw new InvalidOperationException("Whisper model not downloaded");

        // True fixed-duration capture (no VAD-based early termination) so
        // the contract advertised by skill.md / McpToolBridge holds: callers
        // get exactly maxDurationMs of audio, transcribed in full. For
        // "stop when the user pauses" semantics, callers should use
        // stt.listen instead.
        var transcribeArgs = new SttTranscribeArgs
        {
            MaxDurationMs = args.MaxDurationMs,
            Language = !string.IsNullOrWhiteSpace(args.Language)
                ? args.Language!
                : ResolveListenLanguage(_settings?.SttLanguage)
        };
        return await _voiceService.TranscribeFixedDurationAsync(transcribeArgs, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SttListenResult> OnSttListenAsync(
        SttListenArgs args,
        CancellationToken cancellationToken)
    {
        // Defense-in-depth rate-limit: a compromised gateway could otherwise
        // loop stt.listen at the max 120 s window indefinitely.
        var now = DateTimeOffset.UtcNow;
        var sinceLast = now - _lastSttListenStartUtc;
        if (sinceLast < SttListenMinInterval)
        {
            throw new InvalidOperationException("Listen rate limit");
        }
        _lastSttListenStartUtc = now;

        if (_voiceService == null)
            throw new InvalidOperationException("Voice service not available");
        // See the OnSttTranscribeAsync comment: gate on file presence, not
        // on the in-memory load state. ListenOnceAsync handles the lazy load.
        if (!_voiceService.IsModelDownloaded)
            throw new InvalidOperationException("Whisper model not downloaded");

        var result = await _voiceService.ListenOnceAsync(args, cancellationToken).ConfigureAwait(false);
        result.EngineEffective = SttCapability.EngineWhisper;
        return result;
    }

    private Task<SttStatusResult> OnSttStatusAsync(CancellationToken cancellationToken)
    {
        var ready = IsWhisperReady();
        var readiness = ready ? "ready"
            : _voiceService == null ? "unavailable"
            : _voiceService.IsWhisperDownloadingModel ? "model-downloading"
            : _voiceService.IsModelDownloaded ? "initializing"
            : "model-not-downloaded";

        return Task.FromResult(new SttStatusResult
        {
            Engine = SttCapability.EngineWhisper,
            Readiness = readiness,
            ModelDownloadProgress = _voiceService?.WhisperModelDownloadProgress,
            IsListenWithVadSupported = ready,
            IsBoundedTranscribeSupported = ready
        });
    }
    
    #endregion

    #region Recording State

    private void SetRecordingState(RecordingType type, bool isActive, int durationMs = 0)
    {
        switch (type)
        {
            case RecordingType.Screen: IsScreenRecording = isActive; break;
            case RecordingType.Camera: IsCameraRecording = isActive; break;
        }

        RecordingStateChanged?.Invoke(this, new RecordingStateEventArgs
        {
            Type = type,
            IsActive = isActive,
            DurationMs = durationMs
        });
    }

    private async Task EnsureCaptureConsentAsync(
        CaptureConsentType type,
        CancellationToken cancellationToken)
    {
        if (HasCaptureConsent(type)) return;

        Task<bool>? existingConsentPrompt = null;
        TaskCompletionSource<bool>? ownedConsentPrompt = null;

        await _consentLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring lock: a prior caller may have resolved consent.
            if (HasCaptureConsent(type)) return;

            var inFlight = GetConsentPrompt(type);
            if (inFlight != null)
            {
                existingConsentPrompt = inFlight.Task;
            }
            else
            {
                ownedConsentPrompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SetConsentPrompt(type, ownedConsentPrompt);
            }
        }
        finally
        {
            _consentLock.Release();
        }

        if (existingConsentPrompt != null)
        {
            if (!await existingConsentPrompt.WaitAsync(cancellationToken))
                throw new InvalidOperationException("Capture denied: user has not given consent");
            return;
        }

        var clearPrompt = true;
        try
        {
            var dialogTask = ShowCaptureConsentDialogAsync(type, out var closeDialog);
            var consentTimeout = TimeSpan.FromMilliseconds(
                Math.Max(1000, _settings?.CaptureConsentTimeoutMs ?? 120_000));

            // Bind the fail-closed timeout to the prompt itself, not to this
            // caller's cancellationToken: the prompt (and ownedConsentPrompt)
            // are shared with any other concurrent caller of the same
            // consent type via _screen/_camera/_locationConsentInFlight, so
            // the timeout must keep running even if this owning caller is
            // canceled/disconnects first. Layering .WaitAsync(cancellationToken)
            // below lets this caller stop waiting on its own cancellation
            // without tearing down the shared bounded task other waiters (or
            // the abandoned-prompt observer below) still depend on.
            var boundedConsentTask = ConsentPromptTimeoutGate.WithTimeout(
                dialogTask,
                consentTimeout,
                onTimedOut: () =>
                {
                    _logger.Warn(
                        $"[CaptureConsent] {type} consent prompt timed out after " +
                        $"{consentTimeout.TotalSeconds:F0}s with no response; denying (fail-closed).");
                    closeDialog();
                });

            bool consented;
            try
            {
                // Fail closed if nobody answers within consentTimeout: an
                // unattended caller (local MCP, an automated agent, a hosted
                // test) must never hang the underlying request forever waiting
                // on a window no human can see or click.
                consented = await boundedConsentTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                clearPrompt = false;
                ObserveAbandonedConsentPrompt(boundedConsentTask, type, ownedConsentPrompt!);
                throw;
            }

            ownedConsentPrompt!.TrySetResult(consented);

            if (!consented)
                throw new InvalidOperationException("Capture denied: user has not given consent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            ownedConsentPrompt!.TrySetResult(false);
            throw;
        }
        finally
        {
            if (clearPrompt)
            {
                await ClearConsentPromptAsync(type, ownedConsentPrompt!);
            }
        }
    }

    private void ObserveAbandonedConsentPrompt(
        Task<bool> dialogTask,
        CaptureConsentType type,
        TaskCompletionSource<bool> consentPrompt)
    {
        _ = CompleteAbandonedConsentPromptAsync(dialogTask, type, consentPrompt);
    }

    private async Task CompleteAbandonedConsentPromptAsync(
        Task<bool> dialogTask,
        CaptureConsentType type,
        TaskCompletionSource<bool> consentPrompt)
    {
        try
        {
            consentPrompt.TrySetResult(await dialogTask);
        }
        catch (Exception ex)
        {
            _logger.Debug($"[CaptureConsent] Abandoned prompt completion failed: {ex.Message}");
            consentPrompt.TrySetResult(false);
        }
        finally
        {
            await ClearConsentPromptAsync(type, consentPrompt);
        }
    }

    private async Task ClearConsentPromptAsync(
        CaptureConsentType type,
        TaskCompletionSource<bool> consentPrompt)
    {
        await _consentLock.WaitAsync();
        try
        {
            if (ReferenceEquals(GetConsentPrompt(type), consentPrompt))
                SetConsentPrompt(type, null);
        }
        finally
        {
            _consentLock.Release();
        }
    }

    private bool HasCaptureConsent(CaptureConsentType type)
    {
        return type switch
        {
            CaptureConsentType.Screen => _settings?.ScreenRecordingConsentGiven == true,
            CaptureConsentType.Camera => _settings?.CameraRecordingConsentGiven == true,
            CaptureConsentType.Location => _settings?.LocationConsentGiven == true,
            _ => false
        };
    }

    private TaskCompletionSource<bool>? GetConsentPrompt(CaptureConsentType type)
    {
        return type switch
        {
            CaptureConsentType.Screen => _screenConsentInFlight,
            CaptureConsentType.Camera => _cameraConsentInFlight,
            CaptureConsentType.Location => _locationConsentInFlight,
            _ => null
        };
    }

    private void SetConsentPrompt(CaptureConsentType type, TaskCompletionSource<bool>? prompt)
    {
        switch (type)
        {
            case CaptureConsentType.Screen:
                _screenConsentInFlight = prompt;
                break;
            case CaptureConsentType.Camera:
                _cameraConsentInFlight = prompt;
                break;
            case CaptureConsentType.Location:
                _locationConsentInFlight = prompt;
                break;
        }
    }

    private Task<bool> ShowCaptureConsentDialogAsync(CaptureConsentType type, out Action closeDialog)
    {
        var dialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogGate = new object();
        Dialogs.RecordingConsentDialog? dialog = null;
        var closeRequested = false;

        // Lets EnsureCaptureConsentAsync force this prompt closed on timeout
        // (fail-closed denial) instead of leaving an unattended window open
        // forever. Best-effort: if the dialog hasn't been created yet, the
        // creation callback below checks closeRequested and skips showing it.
        closeDialog = () =>
        {
            Dialogs.RecordingConsentDialog? toClose;
            lock (dialogGate)
            {
                closeRequested = true;
                toClose = dialog;
            }
            if (toClose == null) return;
            _dispatcherQueue.TryEnqueue(() =>
            {
                try { toClose.Close(); }
                catch (Exception ex) { _logger.Debug($"[CaptureConsent] Timed-out dialog close failed: {ex.Message}"); }
            });
        };

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var created = new Dialogs.RecordingConsentDialog(type);
                bool alreadyTimedOut;
                lock (dialogGate)
                {
                    alreadyTimedOut = closeRequested;
                    dialog = created;
                }
                if (alreadyTimedOut)
                {
                    // Consent already timed out before the dialog could be
                    // created; never show a prompt whose answer no longer
                    // matters.
                    dialogTcs.TrySetResult(false);
                    created.Close();
                    return;
                }

                var consented = await created.ShowAsync();

                bool timedOutWhileAwaitingAnswer;
                lock (dialogGate)
                {
                    timedOutWhileAwaitingAnswer = closeRequested;
                }
                if (timedOutWhileAwaitingAnswer)
                {
                    // The consent timeout already committed this prompt to a
                    // fail-closed denial (and requested the window close)
                    // before ShowAsync() returned. A click that was already
                    // in flight at that instant must not persist a "late"
                    // grant for an outcome the caller already received as
                    // denied - keep the timeout's result and settings state
                    // consistent instead of racing a stray late click.
                    dialogTcs.TrySetResult(false);
                    return;
                }

                if (consented && _settings != null)
                {
                    switch (type)
                    {
                        case CaptureConsentType.Screen:
                            _settings.ScreenRecordingConsentGiven = true;
                            break;
                        case CaptureConsentType.Camera:
                            _settings.CameraRecordingConsentGiven = true;
                            break;
                        case CaptureConsentType.Location:
                            _settings.LocationConsentGiven = true;
                            break;
                    }
                    _settings.Save();
                }

                dialogTcs.TrySetResult(consented);
            }
            catch (Exception ex)
            {
                _logger.Error($"[CaptureConsent] Dialog error: {ex.Message}");
                dialogTcs.TrySetResult(false);
            }
        }))
        {
            throw new InvalidOperationException("Capture denied: unable to show consent prompt");
        }

        return dialogTcs.Task;
    }

    private async Task ShowRecordingCountdownAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var countdown = new Dialogs.RecordingCountdownWindow(3);
                await countdown.ShowCountdownAsync(cancellationToken);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"[RecordingCountdown] Error: {ex.Message}");
                tcs.TrySetResult(); // Don't block recording if countdown fails
            }
        }))
        {
            // If we can't show the countdown, proceed anyway
            return;
        }

        await tcs.Task.WaitAsync(cancellationToken);
    }

    #endregion
    
    public ValueTask DisposeAsync()
    {
        var task = EnsureDisposeTask();
        return new ValueTask(task);
    }

    public void Dispose()
    {
        ObserveBackgroundFault(EnsureDisposeTask(), "[NodeService] Dispose error");
    }

    private Task EnsureDisposeTask()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= DisposeCoreAsync();
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopMcpServerAsync().ConfigureAwait(false);

        WindowsNodeClient? client;
        lock (_clientLock)
        {
            client = _nodeClient;
            _nodeClient = null;
        }
        if (client != null)
        {
            DetachClientHandlers(client);
        }

        // Best-effort disposal during teardown; surface failures at Debug for diagnostics
        // but never let a cleanup throw block the rest of the teardown chain.
        try { _cameraCaptureService?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose CameraCaptureService failed: {ex.Message}"); }
        try { _screenRecordingService?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose ScreenRecordingService failed: {ex.Message}"); }
        try { _textToSpeechService?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose TextToSpeechService failed: {ex.Message}"); }
        var voiceService = _voiceService;
        _voiceService = null;
        if (voiceService != null)
        {
            try { await voiceService.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose VoiceService failed: {ex.Message}"); }
        }
        // MediaResolver owns SocketsHttpHandler + HttpClient (disposeHandler:true);
        // without disposal the connection pool survives node teardown/recreate.
        try { _mediaResolver?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose MediaResolver failed: {ex.Message}"); }
        _mediaResolver = null;
        // ActionDispatcher owns a SemaphoreSlim; without disposal the kernel
        // handle survives node teardown/recreate.
        try { _actionDispatcher?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose ActionDispatcher failed: {ex.Message}"); }
        _actionDispatcher = null;

        try { _navigationPromptGate.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose NavigationPromptGate failed: {ex.Message}"); }

        try { _deviceStatusProvider?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose DeviceStatusProvider failed: {ex.Message}"); }

        try { _ollamaCapability?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose OllamaCapability failed: {ex.Message}"); }
        try { _ollamaBackend?.Dispose(); } catch (Exception ex) { _logger.Debug($"NodeService: Dispose OllamaInferenceBackend failed: {ex.Message}"); }

        if (_canvasWindow != null && !_canvasWindow.IsClosed)
        {
            var window = _canvasWindow;
            _canvasWindow = null;
            _dispatcherQueue.TryEnqueue(() =>
            {
                try { window?.Close(); }
                catch (Exception ex) { _logger.Debug($"NodeService: Teardown CanvasWindow.Close failed: {ex.Message}"); }
            });
        }

        if (_a2uiCanvasWindow != null && !_a2uiCanvasWindow.IsClosed)
        {
            var window = _a2uiCanvasWindow;
            _a2uiCanvasWindow = null;
            _dispatcherQueue.TryEnqueue(() =>
            {
                try { window?.Close(); }
                catch (Exception ex) { _logger.Debug($"NodeService: Teardown A2UICanvasWindow.Close failed: {ex.Message}"); }
            });
        }

        GC.SuppressFinalize(this);
    }

    private void ObserveBackgroundFault(Task task, string message)
    {
        if (task.IsFaulted)
        {
            _logger.Warn($"{message}: {task.Exception.GetBaseException().Message}");
            return;
        }

        if (task.IsCanceled)
        {
            _logger.Warn($"{message}: canceled");
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => _logger.Warn($"{message}: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

public enum RecordingType
{
    Screen,
    Camera
}

public sealed class RecordingStateEventArgs : EventArgs
{
    public RecordingType Type { get; init; }
    public bool IsActive { get; init; }
    public int DurationMs { get; init; }
}
