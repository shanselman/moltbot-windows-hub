using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

/// <summary>
/// Manages application settings with JSON persistence.
/// </summary>
public class SettingsManager
{
    // OPENCLAW_TRAY_DATA_DIR overrides both this and App.DataPath so an isolated test
    // instance can run alongside the user's real tray without clobbering settings.
    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;
    private const string ProtectedSecretPrefix = "dpapi:";
    private const int CurrentSettingsSchemaVersion = 1;
    private static readonly byte[] ProtectedSecretEntropy = Encoding.UTF8.GetBytes("OpenClawTray.Settings.v1");
    public const string AppThemeSystem = "System";
    public const string AppThemeLight = "Light";
    public const string AppThemeDark = "Dark";

    public static string SettingsDirectoryPath => GetDefaultSettingsDirectory();
    public static string SettingsPath => Path.Combine(SettingsDirectoryPath, "settings.json");
    public string SettingsDirectory => _settingsDirectory;

    /// <summary>Raised after settings are persisted to disk.</summary>
    public event EventHandler? Saved;

    private readonly object _saveLock = new();
    private SettingsData _data = CreateDefaultData();

    // Connection
    public string GatewayUrl { get => _data.GatewayUrl ?? AppIdentity.SetupGatewayUrl; set => _data = _data with { GatewayUrl = value }; }
    public bool UseSshTunnel { get => _data.UseSshTunnel; set => _data = _data with { UseSshTunnel = value }; }
    public string SshTunnelUser { get => _data.SshTunnelUser ?? ""; set => _data = _data with { SshTunnelUser = value }; }
    public string SshTunnelHost { get => _data.SshTunnelHost ?? ""; set => _data = _data with { SshTunnelHost = value }; }
    public int SshTunnelSshPort { get => IsValidPort(_data.SshTunnelSshPort) ? _data.SshTunnelSshPort : 22; set => _data = _data with { SshTunnelSshPort = value }; }
    public int SshTunnelRemotePort { get => _data.SshTunnelRemotePort <= 0 ? 18789 : _data.SshTunnelRemotePort; set => _data = _data with { SshTunnelRemotePort = value }; }
    public int SshTunnelLocalPort { get => _data.SshTunnelLocalPort <= 0 ? 18789 : _data.SshTunnelLocalPort; set => _data = _data with { SshTunnelLocalPort = value }; }
    /// <inheritdoc cref="SettingsData.BrowserControlPort"/>
    public int? BrowserControlPort { get => _data.BrowserControlPort; set => _data = _data with { BrowserControlPort = value }; }
    public string? LegacyToken { get; private set; }
    public string? LegacyBootstrapToken { get; private set; }
    public bool HasLegacyGatewayCredentials =>
        !string.IsNullOrWhiteSpace(LegacyToken) ||
        !string.IsNullOrWhiteSpace(LegacyBootstrapToken);

    // Startup
    public bool AutoStart { get => _data.AutoStart; set => _data = _data with { AutoStart = value }; }
    public bool GlobalHotkeyEnabled { get => _data.GlobalHotkeyEnabled; set => _data = _data with { GlobalHotkeyEnabled = value }; }
    /// <summary>
    /// One-shot gate: set to true after the post-onboarding "first-run" bootstrap
    /// kickoff message has been injected into the chat exactly once.
    /// </summary>
    public bool HasInjectedFirstRunBootstrap { get => _data.HasInjectedFirstRunBootstrap; set => _data = _data with { HasInjectedFirstRunBootstrap = value }; }

    // Notifications
    public bool ShowNotifications { get => _data.ShowNotifications; set => _data = _data with { ShowNotifications = value }; }
    public string NotificationSound { get => _data.NotificationSound ?? "Default"; set => _data = _data with { NotificationSound = value }; }
    
    // Notification filters
    public bool NotifyHealth { get => _data.NotifyHealth; set => _data = _data with { NotifyHealth = value }; }
    public bool NotifyUrgent { get => _data.NotifyUrgent; set => _data = _data with { NotifyUrgent = value }; }
    public bool NotifyReminder { get => _data.NotifyReminder; set => _data = _data with { NotifyReminder = value }; }
    public bool NotifyEmail { get => _data.NotifyEmail; set => _data = _data with { NotifyEmail = value }; }
    public bool NotifyCalendar { get => _data.NotifyCalendar; set => _data = _data with { NotifyCalendar = value }; }
    public bool NotifyBuild { get => _data.NotifyBuild; set => _data = _data with { NotifyBuild = value }; }
    public bool NotifyStock { get => _data.NotifyStock; set => _data = _data with { NotifyStock = value }; }
    public bool NotifyInfo { get => _data.NotifyInfo; set => _data = _data with { NotifyInfo = value }; }

    // Enhanced categorization
    public bool NotifyChatResponses { get => _data.NotifyChatResponses; set => _data = _data with { NotifyChatResponses = value }; }
    public bool PreferStructuredCategories { get => _data.PreferStructuredCategories; set => _data = _data with { PreferStructuredCategories = value }; }
    public List<OpenClaw.Shared.UserNotificationRule> UserRules
    {
        get => _data.UserRules ??= new();
        set => _data = _data with { UserRules = value ?? new() };
    }

    // User interface
    /// <summary>
    /// When true, host the legacy WebView2 gateway chat UI instead of the
    /// native chat surface in both the Hub Chat tab and tray Chat popup.
    /// Default false (native).
    /// </summary>
    public bool UseLegacyWebChat { get => _data.UseLegacyWebChat; set => _data = _data with { UseLegacyWebChat = value }; }
    public bool ShowCompletedSessions { get => _data.ShowCompletedSessions; set => _data = _data with { ShowCompletedSessions = value }; }
    public string AppTheme { get => NormalizeAppTheme(_data.AppTheme); set => _data = _data with { AppTheme = NormalizeAppTheme(value) }; }
    public bool? ShowDiagnosticsOverride { get => _data.ShowDiagnostics; set => _data = _data with { ShowDiagnostics = value }; }
    public bool ShowDiagnosticsEffective => _data.ShowDiagnostics ?? OpenClawTray.Helpers.DiagnosticsGate.BuildDefault;
    public string OpenTelemetryEndpoint { get => _data.OpenTelemetryEndpoint ?? ""; set => _data = _data with { OpenTelemetryEndpoint = NormalizeOptionalString(value) }; }
    public string OpenTelemetryProtocol { get => OpenTelemetryEndpointProtocol.Normalize(_data.OpenTelemetryProtocol); set => _data = _data with { OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(value) }; }

    // Node mode(gateway WebSocket connection — separate from MCP)
    public bool EnableNodeMode { get => _data.EnableNodeMode; set => _data = _data with { EnableNodeMode = value }; }
    /// <summary>Master switch for the focused inbound-pairing approval dialog + awareness toast.</summary>
    public bool ShowPairingApprovalDialog { get => _data.ShowPairingApprovalDialog; set => _data = _data with { ShowPairingApprovalDialog = value }; }
    public bool NodeCanvasEnabled { get => _data.NodeCanvasEnabled; set => _data = _data with { NodeCanvasEnabled = value }; }
    public bool NodeScreenEnabled { get => _data.NodeScreenEnabled; set => _data = _data with { NodeScreenEnabled = value }; }
    public bool NodeCameraEnabled { get => _data.NodeCameraEnabled; set => _data = _data with { NodeCameraEnabled = value }; }
    public bool ScreenRecordingConsentGiven { get => _data.ScreenRecordingConsentGiven; set => _data = _data with { ScreenRecordingConsentGiven = value }; }
    public bool CameraRecordingConsentGiven { get => _data.CameraRecordingConsentGiven; set => _data = _data with { CameraRecordingConsentGiven = value }; }
    public bool NodeLocationEnabled { get => _data.NodeLocationEnabled; set => _data = _data with { NodeLocationEnabled = value }; }
    public bool LocationConsentGiven { get => _data.LocationConsentGiven; set => _data = _data with { LocationConsentGiven = value }; }
    /// <summary>Fail-closed timeout (ms) for an unanswered capture consent prompt. See <see cref="SettingsData.CaptureConsentTimeoutMs"/>.</summary>
    public int CaptureConsentTimeoutMs { get => _data.CaptureConsentTimeoutMs; set => _data = _data with { CaptureConsentTimeoutMs = value }; }
    public bool NodeBrowserProxyEnabled { get => _data.NodeBrowserProxyEnabled; set => _data = _data with { NodeBrowserProxyEnabled = value }; }
    /// <summary>
    /// Master switch for the <c>system.run</c> / <c>system.run.prepare</c>
    /// commands. Per-command exec approvals still apply when this is on;
    /// flipping it off removes those commands from the declared capability
    /// entirely. Default <c>true</c> (backward compatible).
    /// </summary>
    public bool NodeSystemRunEnabled { get => _data.NodeSystemRunEnabled; set => _data = _data with { NodeSystemRunEnabled = value }; }
    public bool NodeSttEnabled { get => _data.NodeSttEnabled; set => _data = _data with { NodeSttEnabled = value }; }
    /// <summary>STT language: "auto" for Whisper auto-detect, or a BCP-47 tag like "en-US".</summary>
    public string SttLanguage { get => string.IsNullOrWhiteSpace(_data.SttLanguage) ? "auto" : _data.SttLanguage; set => _data = _data with { SttLanguage = value }; }
    /// <summary>Whisper model size: "tiny", "base", or "small".</summary>
    public string SttModelName { get => string.IsNullOrWhiteSpace(_data.SttModelName) ? "base" : _data.SttModelName; set => _data = _data with { SttModelName = value }; }
    /// <summary>Seconds of silence before auto-submit in voice chat mode.</summary>
    public float SttSilenceTimeout { get => _data.SttSilenceTimeout > 0 ? _data.SttSilenceTimeout : 1.5f; set => _data = _data with { SttSilenceTimeout = value }; }
    /// <summary>Enable TTS playback of responses during voice sessions.</summary>
    public bool VoiceTtsEnabled { get => _data.VoiceTtsEnabled; set => _data = _data with { VoiceTtsEnabled = value }; }
    /// <summary>Show tool-call and usage chips inline in the chat timeline.</summary>
    public bool ShowChatToolCalls { get => _data.ShowChatToolCalls; set => _data = _data with { ShowChatToolCalls = value }; }
    /// <summary>Play audio feedback chimes on listen start/stop.</summary>
    public bool VoiceAudioFeedback { get => _data.VoiceAudioFeedback; set => _data = _data with { VoiceAudioFeedback = value }; }
    public bool NodeTtsEnabled { get => _data.NodeTtsEnabled; set => _data = _data with { NodeTtsEnabled = value }; }
    /// <summary>
    /// Opt-in: lets any paired active gateway (local or remote) invoke the
    /// separately installed Windows Ollama service through this node's
    /// <c>local-inference</c> capability. Default <c>false</c>. Distinct
    /// from, and does not change, the app-managed Local AI gateway provider.
    /// </summary>
    public bool NodeOllamaInferenceEnabled { get => _data.NodeOllamaInferenceEnabled; set => _data = _data with { NodeOllamaInferenceEnabled = value }; }
    public string TtsProvider { get => string.IsNullOrWhiteSpace(_data.TtsProvider) ? TtsCapability.PiperProvider : _data.TtsProvider; set => _data = _data with { TtsProvider = value }; }
    public string TtsElevenLabsApiKey { get => _data.TtsElevenLabsApiKey ?? ""; set => _data = _data with { TtsElevenLabsApiKey = value }; }
    public string TtsElevenLabsModel { get => _data.TtsElevenLabsModel ?? ""; set => _data = _data with { TtsElevenLabsModel = value }; }
    public string TtsElevenLabsVoiceId { get => _data.TtsElevenLabsVoiceId ?? ""; set => _data = _data with { TtsElevenLabsVoiceId = value }; }
    public string TtsMiniMaxApiKey { get => _data.TtsMiniMaxApiKey ?? ""; set => _data = _data with { TtsMiniMaxApiKey = value }; }
    public string TtsMiniMaxModel { get => string.IsNullOrWhiteSpace(_data.TtsMiniMaxModel) ? MiniMaxTextToSpeechClient.DefaultModel : _data.TtsMiniMaxModel; set => _data = _data with { TtsMiniMaxModel = value }; }
    public string TtsMiniMaxVoiceId { get => _data.TtsMiniMaxVoiceId ?? ""; set => _data = _data with { TtsMiniMaxVoiceId = value }; }
    public string TtsMiniMaxRegion { get => string.IsNullOrWhiteSpace(_data.TtsMiniMaxRegion) ? MiniMaxTextToSpeechClient.GlobalRegion : _data.TtsMiniMaxRegion; set => _data = _data with { TtsMiniMaxRegion = value }; }
    public string TtsWindowsVoiceId { get => _data.TtsWindowsVoiceId ?? ""; set => _data = _data with { TtsWindowsVoiceId = value }; }
    /// <summary>Hub NavigationView pane expanded (true) vs compact (false). Default true.</summary>
    public bool HubNavPaneOpen { get => _data.HubNavPaneOpen; set => _data = _data with { HubNavPaneOpen = value }; }
    /// <summary>Piper voice identifier, e.g. "en_US-amy-low".</summary>
    public string TtsPiperVoiceId { get => string.IsNullOrWhiteSpace(_data.TtsPiperVoiceId) ? "en_US-amy-low" : _data.TtsPiperVoiceId; set => _data = _data with { TtsPiperVoiceId = value }; }
    // Local MCP HTTP server (independent of EnableNodeMode)
    public bool EnableMcpServer { get => _data.EnableMcpServer; set => _data = _data with { EnableMcpServer = value }; }
    // Automatic self-repair of app-owned setup-managed local WSL gateways (kill switch).
    public bool EnableManagedLocalGatewayAutoRepair { get => _data.EnableManagedLocalGatewayAutoRepair; set => _data = _data with { EnableManagedLocalGatewayAutoRepair = value }; }
    /// <summary>
    /// Hostnames the A2UI image renderer is allowed to fetch over HTTPS.
    /// Empty by default — agents can still ship inline data: images. The
    /// runtime never bypasses this list, so it is the single switch keeping
    /// agent JSON from issuing arbitrary outbound HTTP from the tray process.
    /// </summary>
    public List<string> A2UIImageHosts
    {
        get => _data.A2UIImageHosts ??= new();
        set => _data = _data with { A2UIImageHosts = value ?? new() };
    }
    public bool HasSeenActivityStreamTip { get => _data.HasSeenActivityStreamTip; set => _data = _data with { HasSeenActivityStreamTip = value }; }
    public string SkippedUpdateTag { get => _data.SkippedUpdateTag ?? ""; set => _data = _data with { SkippedUpdateTag = value }; }
    public string? PreferredGatewayId { get => _data.PreferredGatewayId; set => _data = _data with { PreferredGatewayId = value }; }

    // ── MXC sandbox ─────────────────────────────────────────────────────
    /// <summary>Master switch for system.run containment. When true (default), system.run uses MXC when available and falls back to host execution when unavailable unless strict fallback blocking is enabled. When false, system.run runs on host like before.</summary>
    public bool SystemRunSandboxEnabled { get => _data.SystemRunSandboxEnabled; set => _data = _data with { SystemRunSandboxEnabled = value }; }
    /// <summary>When true, sandbox-enabled system.run blocks instead of using the compatibility host fallback if MXC is unavailable. Default false.</summary>
    public bool SystemRunBlockHostFallbackWhenMxcUnavailable { get => _data.SystemRunBlockHostFallbackWhenMxcUnavailable; set => _data = _data with { SystemRunBlockHostFallbackWhenMxcUnavailable = value }; }
    /// <summary>When sandboxed, allow system.run commands to reach the public internet. Default false.</summary>
    public bool SystemRunAllowOutbound { get => _data.SystemRunAllowOutbound; set => _data = _data with { SystemRunAllowOutbound = value }; }
    /// <summary>When sandboxed, allow Windows UI system calls required by PowerShell and some console utilities. Default false.</summary>
    public bool SystemRunAllowWindowsUi { get => _data.SystemRunAllowWindowsUi; set => _data = _data with { SystemRunAllowWindowsUi = value }; }
    // ── MXC sandbox: additional knobs (Sandbox page) ─────────────────
    public SandboxClipboardMode SandboxClipboard { get => _data.SandboxClipboard; set => _data = _data with { SandboxClipboard = value }; }
    public SandboxFolderAccess? SandboxDocumentsAccess { get => _data.SandboxDocumentsAccess; set => _data = _data with { SandboxDocumentsAccess = value }; }
    public SandboxFolderAccess? SandboxDownloadsAccess { get => _data.SandboxDownloadsAccess; set => _data = _data with { SandboxDownloadsAccess = value }; }
    public SandboxFolderAccess? SandboxDesktopAccess { get => _data.SandboxDesktopAccess; set => _data = _data with { SandboxDesktopAccess = value }; }
    public List<SandboxCustomFolder> SandboxCustomFolders
    {
        get => _data.SandboxCustomFolders ??= new();
        set => _data = _data with { SandboxCustomFolders = value ?? new() };
    }
    public int SandboxTimeoutMs { get => _data.SandboxTimeoutMs > 0 ? _data.SandboxTimeoutMs : 30_000; set => _data = _data with { SandboxTimeoutMs = value }; }
    public long SandboxMaxOutputBytes { get => _data.SandboxMaxOutputBytes > 0 ? _data.SandboxMaxOutputBytes : 4 * 1024 * 1024; set => _data = _data with { SandboxMaxOutputBytes = value }; }

    public SettingsManager() : this(GetDefaultSettingsDirectory())
    {
    }

    public SettingsManager(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
            throw new ArgumentException("Settings directory cannot be empty.", nameof(settingsDirectory));

        _settingsDirectory = settingsDirectory;
        _settingsFilePath = Path.Combine(settingsDirectory, "settings.json");
        Load();
    }

    private static string GetDefaultSettingsDirectory()
    {
        return AppIdentity.ResolveRoamingDataDirectory();
    }

    public void Load()
    {
        LegacyToken = null;
        LegacyBootstrapToken = null;
        _data = CreateDefaultData();

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                LoadLegacyGatewayCredentials(json);
                var loaded = SettingsData.FromJson(json);
                if (loaded != null)
                {
                    _data = NormalizeLoadedData(loaded, json);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load settings: {ex.Message}");
            LegacyToken = null;
            LegacyBootstrapToken = null;
        }
    }

    private static SettingsData CreateDefaultData() => new()
    {
        SettingsSchemaVersion = CurrentSettingsSchemaVersion,
        GatewayUrl = AppIdentity.SetupGatewayUrl,
        UseSshTunnel = false,
        SshTunnelUser = "",
        SshTunnelHost = "",
        SshTunnelSshPort = 22,
        SshTunnelRemotePort = 18789,
        SshTunnelLocalPort = 18789,
        AutoStart = true,
        GlobalHotkeyEnabled = true,
        HasInjectedFirstRunBootstrap = false,
        ShowNotifications = true,
        NotificationSound = "Default",
        NotifyHealth = true,
        NotifyUrgent = true,
        NotifyReminder = true,
        NotifyEmail = true,
        NotifyCalendar = true,
        NotifyBuild = true,
        NotifyStock = true,
        NotifyInfo = true,
        NotifyChatResponses = true,
        PreferStructuredCategories = true,
        UserRules = new(),
        UseLegacyWebChat = false,
        ShowCompletedSessions = false,
        AppTheme = AppThemeSystem,
        OpenTelemetryEndpoint = null,
        OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Grpc,
        EnableNodeMode = false,
        NodeCanvasEnabled = true,
        NodeScreenEnabled = true,
        NodeCameraEnabled = true,
        ScreenRecordingConsentGiven = false,
        CameraRecordingConsentGiven = false,
        NodeLocationEnabled = true,
        LocationConsentGiven = false,
        NodeBrowserProxyEnabled = true,
        NodeSystemRunEnabled = true,
        NodeSttEnabled = false,
        SttLanguage = "auto",
        SttModelName = "base",
        SttSilenceTimeout = 1.5f,
        VoiceTtsEnabled = true,
        VoiceAudioFeedback = true,
        NodeTtsEnabled = false,
        NodeOllamaInferenceEnabled = false,
        TtsProvider = TtsCapability.PiperProvider,
        TtsElevenLabsApiKey = "",
        TtsElevenLabsModel = "",
        TtsElevenLabsVoiceId = "",
        TtsMiniMaxApiKey = "",
        TtsMiniMaxModel = MiniMaxTextToSpeechClient.DefaultModel,
        TtsMiniMaxVoiceId = "",
        TtsMiniMaxRegion = MiniMaxTextToSpeechClient.GlobalRegion,
        TtsWindowsVoiceId = "",
        HubNavPaneOpen = true,
        TtsPiperVoiceId = "en_US-amy-low",
        EnableMcpServer = false,
        A2UIImageHosts = new(),
        HasSeenActivityStreamTip = false,
        SkippedUpdateTag = "",
        PreferredGatewayId = null,
        SystemRunSandboxEnabled = true,
        SystemRunBlockHostFallbackWhenMxcUnavailable = false,
        SystemRunAllowOutbound = false,
        SystemRunAllowWindowsUi = false,
        SandboxClipboard = SandboxClipboardMode.None,
        SandboxDocumentsAccess = null,
        SandboxDownloadsAccess = null,
        SandboxDesktopAccess = null,
        SandboxCustomFolders = new(),
        SandboxTimeoutMs = 30_000,
        SandboxMaxOutputBytes = 4 * 1024 * 1024
    };

    private static SettingsData NormalizeLoadedData(SettingsData loaded, string? rawJson = null)
    {
        var defaults = CreateDefaultData();
        var data = loaded with
        {
            SettingsSchemaVersion = CurrentSettingsSchemaVersion,
            GatewayUrl = loaded.GatewayUrl ?? defaults.GatewayUrl,
            SshTunnelUser = loaded.SshTunnelUser ?? defaults.SshTunnelUser,
            SshTunnelHost = loaded.SshTunnelHost ?? defaults.SshTunnelHost,
            SshTunnelSshPort = IsValidPort(loaded.SshTunnelSshPort) ? loaded.SshTunnelSshPort : defaults.SshTunnelSshPort,
            SshTunnelRemotePort = loaded.SshTunnelRemotePort <= 0 ? defaults.SshTunnelRemotePort : loaded.SshTunnelRemotePort,
            SshTunnelLocalPort = loaded.SshTunnelLocalPort <= 0 ? defaults.SshTunnelLocalPort : loaded.SshTunnelLocalPort,
            NotificationSound = loaded.NotificationSound ?? defaults.NotificationSound,
            SttLanguage = string.IsNullOrWhiteSpace(loaded.SttLanguage) ? defaults.SttLanguage : loaded.SttLanguage,
            SttModelName = string.IsNullOrWhiteSpace(loaded.SttModelName) ? defaults.SttModelName : loaded.SttModelName,
            SttSilenceTimeout = loaded.SttSilenceTimeout > 0 ? loaded.SttSilenceTimeout : defaults.SttSilenceTimeout,
            TtsProvider = string.IsNullOrWhiteSpace(loaded.TtsProvider) ? defaults.TtsProvider : loaded.TtsProvider,
            TtsElevenLabsApiKey = UnprotectSettingSecret(loaded.TtsElevenLabsApiKey) ?? defaults.TtsElevenLabsApiKey,
            TtsElevenLabsModel = loaded.TtsElevenLabsModel ?? defaults.TtsElevenLabsModel,
            TtsElevenLabsVoiceId = loaded.TtsElevenLabsVoiceId ?? defaults.TtsElevenLabsVoiceId,
            TtsMiniMaxApiKey = UnprotectSettingSecret(loaded.TtsMiniMaxApiKey) ?? defaults.TtsMiniMaxApiKey,
            TtsMiniMaxModel = loaded.TtsMiniMaxModel ?? defaults.TtsMiniMaxModel,
            TtsMiniMaxVoiceId = loaded.TtsMiniMaxVoiceId ?? defaults.TtsMiniMaxVoiceId,
            TtsMiniMaxRegion = loaded.TtsMiniMaxRegion ?? defaults.TtsMiniMaxRegion,
            TtsWindowsVoiceId = loaded.TtsWindowsVoiceId ?? defaults.TtsWindowsVoiceId,
            TtsPiperVoiceId = string.IsNullOrWhiteSpace(loaded.TtsPiperVoiceId) ? defaults.TtsPiperVoiceId : loaded.TtsPiperVoiceId,
            A2UIImageHosts = loaded.A2UIImageHosts is { Count: > 0 } hosts ? new List<string>(hosts) : new(),
            SkippedUpdateTag = loaded.SkippedUpdateTag ?? defaults.SkippedUpdateTag,
            PreferredGatewayId = loaded.PreferredGatewayId ?? defaults.PreferredGatewayId,
            AppTheme = NormalizeAppTheme(loaded.AppTheme),
            ShowDiagnostics = loaded.ShowDiagnostics,
            OpenTelemetryEndpoint = NormalizeOptionalString(loaded.OpenTelemetryEndpoint),
            OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(loaded.OpenTelemetryProtocol),
            UserRules = loaded.UserRules != null ? new List<UserNotificationRule>(loaded.UserRules) : new(),
            SandboxCustomFolders = CloneSandboxCustomFolders(loaded.SandboxCustomFolders),
            SystemRunBlockHostFallbackWhenMxcUnavailable = loaded.SystemRunBlockHostFallbackWhenMxcUnavailable,
            SandboxTimeoutMs = loaded.SandboxTimeoutMs > 0 ? loaded.SandboxTimeoutMs : defaults.SandboxTimeoutMs,
            SandboxMaxOutputBytes = loaded.SandboxMaxOutputBytes > 0 ? loaded.SandboxMaxOutputBytes : defaults.SandboxMaxOutputBytes,
            McpOnlyMode = null
        };

        // Legacy McpOnlyMode migration:
        //   true  -> node off (no gateway), MCP on
        //   false -> leave MCP off; the user has not opted in to a local HTTP server.
        if (loaded.McpOnlyMode is true)
        {
            data = data with
            {
                EnableMcpServer = true,
                EnableNodeMode = false
            };
        }

        return data;
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static List<SandboxCustomFolder> CloneSandboxCustomFolders(IEnumerable<SandboxCustomFolder>? folders) =>
        folders is null
            ? new List<SandboxCustomFolder>()
            : folders
                .Select(folder => new SandboxCustomFolder
                {
                    Path = folder.Path,
                    Access = folder.Access
                })
                .ToList();

    private void LoadLegacyGatewayCredentials(string json)
    {
        LegacyToken = null;
        LegacyBootstrapToken = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            LegacyToken = ReadLegacyString(document.RootElement, "Token");
            LegacyBootstrapToken = ReadLegacyString(document.RootElement, "BootstrapToken");
        }
        // slopwatch-ignore: SW003 Optional persisted state fallback is intentional; caller continues with defaults or prior state.
        catch (JsonException)
        {
            // SettingsData.FromJson handles invalid settings by falling back to defaults.
        }
    }

    private static string? ReadLegacyString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    /// <summary>
    /// Creates a detached snapshot of current settings. No DPAPI protection is
    /// applied here; Save applies it to a second clone for on-disk storage only.
    /// </summary>
    public SettingsData ToSettingsData() => _data with
    {
        GatewayUrl = GatewayUrl,
        SshTunnelUser = SshTunnelUser,
        SshTunnelHost = SshTunnelHost,
        SshTunnelRemotePort = SshTunnelRemotePort,
        SshTunnelLocalPort = SshTunnelLocalPort,
        NotificationSound = NotificationSound,
        SttLanguage = SttLanguage,
        SttModelName = SttModelName,
        SttSilenceTimeout = SttSilenceTimeout,
        TtsProvider = TtsProvider,
        TtsElevenLabsApiKey = TtsElevenLabsApiKey,
        TtsElevenLabsModel = string.IsNullOrWhiteSpace(TtsElevenLabsModel) ? null : TtsElevenLabsModel,
        TtsElevenLabsVoiceId = string.IsNullOrWhiteSpace(TtsElevenLabsVoiceId) ? null : TtsElevenLabsVoiceId,
        TtsMiniMaxApiKey = TtsMiniMaxApiKey,
        TtsMiniMaxModel = string.IsNullOrWhiteSpace(TtsMiniMaxModel) ? MiniMaxTextToSpeechClient.DefaultModel : TtsMiniMaxModel,
        TtsMiniMaxVoiceId = string.IsNullOrWhiteSpace(TtsMiniMaxVoiceId) ? null : TtsMiniMaxVoiceId,
        TtsMiniMaxRegion = string.IsNullOrWhiteSpace(TtsMiniMaxRegion) ? MiniMaxTextToSpeechClient.GlobalRegion : TtsMiniMaxRegion,
        TtsWindowsVoiceId = string.IsNullOrWhiteSpace(TtsWindowsVoiceId) ? null : TtsWindowsVoiceId,
        TtsPiperVoiceId = TtsPiperVoiceId,
        AppTheme = AppTheme,
        ShowDiagnostics = ShowDiagnosticsOverride,
        OpenTelemetryEndpoint = NormalizeOptionalString(OpenTelemetryEndpoint),
        OpenTelemetryProtocol = OpenTelemetryEndpointProtocol.Normalize(OpenTelemetryProtocol),
        A2UIImageHosts = A2UIImageHosts.Count == 0 ? null : new List<string>(A2UIImageHosts),
        SkippedUpdateTag = string.IsNullOrWhiteSpace(SkippedUpdateTag) ? null : SkippedUpdateTag,
        PreferredGatewayId = string.IsNullOrWhiteSpace(PreferredGatewayId) ? null : PreferredGatewayId,
        UserRules = new List<UserNotificationRule>(UserRules),
        SandboxCustomFolders = SandboxCustomFolders.Count == 0 ? null : CloneSandboxCustomFolders(SandboxCustomFolders),
        SandboxTimeoutMs = SandboxTimeoutMs,
        SandboxMaxOutputBytes = SandboxMaxOutputBytes,
        McpOnlyMode = null
    };

    public static string NormalizeAppTheme(string? value)
    {
        if (string.Equals(value, AppThemeLight, StringComparison.OrdinalIgnoreCase))
            return AppThemeLight;
        if (string.Equals(value, AppThemeDark, StringComparison.OrdinalIgnoreCase))
            return AppThemeDark;
        return AppThemeSystem;
    }

    private static string? NormalizeOptionalString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Save()
    {
        try
        {
            SaveOrThrow();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }

    internal void SaveOrThrow()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(_settingsDirectory);
            // Lock the tray data dir to current user + SYSTEM + Administrators —
            // it co-locates the MCP bearer token, settings.json (which embeds
            // gateway/bootstrap credentials), and diagnostics jsonl. Other apps
            // running as the same user could otherwise read these freely.
            OpenClaw.Shared.Mcp.McpAuthToken.TryRestrictDataDirectoryAcl(_settingsDirectory);

            var data = ToSettingsData();
            // Apply DPAPI protection to the API key for on-disk storage only
            data.TtsElevenLabsApiKey = ProtectSettingSecret(data.TtsElevenLabsApiKey);
            data.TtsMiniMaxApiKey = ProtectSettingSecret(data.TtsMiniMaxApiKey);

            var json = data.ToJson();
            File.WriteAllText(_settingsFilePath, json);

            Logger.Info("Settings saved");
            try
            {
                Saved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Settings saved, but a notification subscriber failed: {ex.Message}");
            }
        }
    }

    internal static string? ProtectSettingSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Data Protection API is required for protected settings secrets.");

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
        return ProtectedSecretPrefix + Convert.ToBase64String(protectedBytes);
    }

    internal static bool CanProtectSettingSecretsForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var bytes = Encoding.UTF8.GetBytes("openclaw-dpapi-probe");
            var protectedBytes = ProtectedData.Protect(bytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            var unprotectedBytes = ProtectedData.Unprotect(protectedBytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            return bytes.SequenceEqual(unprotectedBytes);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static string? UnprotectSettingSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (!value.StartsWith(ProtectedSecretPrefix, StringComparison.Ordinal))
            return value;

        if (!OperatingSystem.IsWindows())
        {
            Logger.Warn("Failed to decrypt protected settings secret: Windows Data Protection API is unavailable.");
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[ProtectedSecretPrefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, ProtectedSecretEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException ex)
        {
            Logger.Warn($"Failed to decode protected settings secret: {ex.Message}");
            return null;
        }
        catch (CryptographicException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
        catch (ArgumentException ex)
        {
            Logger.Warn($"Failed to decrypt protected settings secret: {ex.Message}");
            return null;
        }
    }

    public string GetEffectiveGatewayUrl()
    {
        if (!UseSshTunnel)
        {
            return GatewayUrl;
        }

        return $"ws://127.0.0.1:{SshTunnelLocalPort}";
    }
}
