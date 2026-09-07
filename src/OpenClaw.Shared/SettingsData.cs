using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

/// <summary>
/// Serializable settings data model. Used for JSON round-trip persistence.
/// </summary>
public record class SettingsData
{
    /// <summary>
    /// Version for settings-file migrations that need to distinguish legacy
    /// serialized defaults from explicit operator choices.
    /// </summary>
    public int SettingsSchemaVersion { get; set; } = 1;

    public string? GatewayUrl { get; set; }
    public bool UseSshTunnel { get; set; } = false;
    public string? SshTunnelUser { get; set; }
    public string? SshTunnelHost { get; set; }
    public int SshTunnelSshPort { get; set; } = 22;
    public int SshTunnelRemotePort { get; set; } = 18789;
    public int SshTunnelLocalPort { get; set; } = 18789;
    public bool AutoStart { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;
    /// <summary>
    /// One-shot gate: set to true after the post-onboarding "first-run" bootstrap
    /// kickoff message has been injected into the chat exactly once. Subsequent
    /// chat-window launches skip injection.
    /// </summary>
    public bool HasInjectedFirstRunBootstrap { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public string? NotificationSound { get; set; }
    public bool NotifyHealth { get; set; } = true;
    public bool NotifyUrgent { get; set; } = true;
    public bool NotifyReminder { get; set; } = true;
    public bool NotifyEmail { get; set; } = true;
    public bool NotifyCalendar { get; set; } = true;
    public bool NotifyBuild { get; set; } = true;
    public bool NotifyStock { get; set; } = true;
    public bool NotifyInfo { get; set; } = true;
    /// <summary>
    /// When true (default), inbound device/node pairing requests are surfaced as a focused
    /// approval dialog plus an awareness toast. When false they appear only in the Connections
    /// page "Pending approvals" banner (passive). Auto-approval of the local node is unaffected.
    /// </summary>
    public bool ShowPairingApprovalDialog { get; set; } = true;
    public bool EnableNodeMode { get; set; } = false;
    public bool NodeCanvasEnabled { get; set; } = true;
    public bool NodeScreenEnabled { get; set; } = true;
    public bool NodeCameraEnabled { get; set; } = true;
    public bool ScreenRecordingConsentGiven { get; set; } = false;
    public bool CameraRecordingConsentGiven { get; set; } = false;
    public bool NodeLocationEnabled { get; set; } = true;
    public bool LocationConsentGiven { get; set; } = false;
    /// <summary>
    /// Maximum time (ms) to wait for a human response to a screen/camera/location
    /// capture consent prompt before failing closed (treating the capture as
    /// denied). Protects unattended or non-interactive callers - a local MCP
    /// HTTP client, an automated agent, a hosted test - from hanging forever on
    /// a consent window nobody can answer. Never auto-grants: a timeout always
    /// resolves to denial, the same fail-closed direction Windows' own secure
    /// desktop consent prompts (UAC) take when nobody responds. A real user who
    /// takes longer than this to decide can simply retry; the prompt reappears.
    /// Default 120000ms (2 minutes).
    /// </summary>
    public int CaptureConsentTimeoutMs { get; set; } = 120_000;
    public bool NodeBrowserProxyEnabled { get; set; } = true;

    /// <summary>
    /// Optional override for the browser-control host port the node-side
    /// <c>browser.proxy</c> capability connects to. When null (default) the port is
    /// derived as gateway port + 2 on 127.0.0.1, matching a co-located gateway.
    /// <para><b>Superseded:</b> prefer <c>GatewayRecord.BrowserControlPort</c> (per-gateway),
    /// which is resolved from the active gateway and cannot misroute across a gateway switch.
    /// This global field is preserved for settings-file backward compatibility only.</para>
    /// </summary>
    public int? BrowserControlPort { get; set; }

    /// <summary>
    /// Master switch for the <c>system.run</c> + <c>system.run.prepare</c>
    /// commands. When <c>false</c>, those commands are dropped from the
    /// capability's declared command list (so they don't appear in the
    /// connect handshake or in MCP <c>tools/list</c>) and invocations are
    /// rejected defensively. Per-command exec approvals still apply when
    /// enabled. Default <c>true</c> for backward compatibility — exec has
    /// been part of the Windows node since day one.
    /// </summary>
    public bool NodeSystemRunEnabled { get; set; } = true;
    public bool NodeSttEnabled { get; set; } = false;
    /// <summary>STT language: "auto" for Whisper auto-detect, or a BCP-47 tag like "en-US".</summary>
    public string SttLanguage { get; set; } = "auto";
    /// <summary>Whisper model name: "tiny", "base", or "small".</summary>
    public string SttModelName { get; set; } = "base";
    /// <summary>Seconds of silence before auto-submit in voice chat mode.</summary>
    public float SttSilenceTimeout { get; set; } = 2.5f;
    /// <summary>Enable TTS playback of responses during voice sessions.</summary>
    public bool VoiceTtsEnabled { get; set; } = true;
    /// <summary>Show tool-call and usage chips inline in the chat timeline.</summary>
    public bool ShowChatToolCalls { get; set; } = true;
    /// <summary>Play audio feedback chimes on listen start/stop.</summary>
    public bool VoiceAudioFeedback { get; set; } = true;
    public bool NodeTtsEnabled { get; set; } = false;
    /// <summary>
    /// Opt-in switch for the <c>local-inference</c> capability
    /// (<c>ollama.models</c> / <c>ollama.chat</c>). When <c>true</c>, any
    /// paired active gateway - local or remote - can invoke the separately
    /// installed Windows Ollama service through this node. Default
    /// <c>false</c>: sharing a locally installed inference service is an
    /// explicit user choice, distinct from the app-managed Local AI gateway
    /// provider (which is unaffected by this setting).
    /// </summary>
    public bool NodeOllamaInferenceEnabled { get; set; } = false;
    public string TtsProvider { get; set; } = OpenClaw.Shared.Capabilities.TtsCapability.PiperProvider;
    /// <summary>Persisted: whether the Hub's NavigationView pane is expanded
    /// (true) or collapsed/compact (false). Default true.</summary>
    public bool HubNavPaneOpen { get; set; } = true;
    /// <summary>Optional Windows TTS voice id (or display name). Empty = system default.</summary>
    public string? TtsWindowsVoiceId { get; set; }
    /// <summary>
    /// ElevenLabs API key storage slot. When persisted by the Windows tray's
    /// SettingsManager this is an opaque dpapi:-prefixed blob, not plaintext.
    /// </summary>
    public string? TtsElevenLabsApiKey { get; set; }
    public string? TtsElevenLabsModel { get; set; }
    public string? TtsElevenLabsVoiceId { get; set; }
    /// <summary>
    /// MiniMax API key storage slot. When persisted by the Windows tray's
    /// SettingsManager this is an opaque dpapi:-prefixed blob, not plaintext.
    /// </summary>
    public string? TtsMiniMaxApiKey { get; set; }
    public string? TtsMiniMaxModel { get; set; } = "speech-2.8-hd";
    public string? TtsMiniMaxVoiceId { get; set; }
    public string? TtsMiniMaxRegion { get; set; } = "global_en";
    /// <summary>Piper voice identifier, e.g. "en_US-amy-low". Voice file is downloaded on first use.</summary>
    public string TtsPiperVoiceId { get; set; } = "en_US-amy-low";
    /// <summary>Run the local MCP HTTP server. Independent of EnableNodeMode.</summary>
    public bool EnableMcpServer { get; set; } = false;
    /// <summary>
    /// Automatically self-repair an app-owned local WSL gateway when it becomes unreachable:
    /// probe it and, if the gateway process is down, restart the managed WSL distro, re-arm the
    /// keepalive, and reconnect — without user action. Strictly limited to setup-managed local
    /// WSL gateways (never SSH/remote). Kill switch: set false to disable automatic repair.
    /// </summary>
    public bool EnableManagedLocalGatewayAutoRepair { get; set; } = true;
    /// <summary>
    /// Hostnames the A2UI image renderer is allowed to fetch over HTTPS.
    /// Empty by default — agents can still ship inline data: images. Add hosts
    /// (e.g., "cdn.example.com") via the Settings window.
    /// </summary>
    public List<string>? A2UIImageHosts { get; set; }
    /// <summary>
    /// Legacy flag (replaced by EnableMcpServer + the EnableNodeMode pair).
    /// Kept for one-time migration on Load; not written on Save.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? McpOnlyMode { get; set; }
    public string? PreferredGatewayId { get; set; }
    public bool HasSeenActivityStreamTip { get; set; } = false;
    public string? SkippedUpdateTag { get; set; }
    public bool NotifyChatResponses { get; set; } = true;
    public bool PreferStructuredCategories { get; set; } = true;
    /// <summary>
    /// When true, the Hub Chat tab and tray Chat popup host the legacy
    /// WebView2-based gateway chat UI instead of the native chat surface.
    /// Default false (native chat). Surfaced as a toggle in SettingsPage's
    /// "User interface" section.
    /// </summary>
    public bool UseLegacyWebChat { get; set; } = false;
    /// <summary>
    /// When false, the tray Sessions page hides clean completed sessions
    /// (currently status <c>done</c> / <c>completed</c>) while keeping failed,
    /// killed, timeout, and aborted runs visible. Default false reduces
    /// completed ACP/subagent clutter for upgraded installs.
    /// </summary>
    public bool ShowCompletedSessions { get; set; } = false;
    public string AppTheme { get; set; } = "System";
    public bool? ShowDiagnostics { get; set; }
    /// <summary>
    /// Optional OTLP collector endpoint for diagnostic telemetry.
    /// Null or empty means telemetry is disabled.
    /// </summary>
    public string? OpenTelemetryEndpoint { get; set; }
    /// <summary>
    /// OTLP transport protocol for the configured OpenTelemetry endpoint.
    /// </summary>
    public string OpenTelemetryProtocol { get; set; } = "grpc";
    public List<UserNotificationRule>? UserRules { get; set; }

    // ── MXC sandbox ─────────────────────────────────────────────────────
    /// <summary>
    /// Master switch for system.run containment. When <c>true</c> (default),
    /// system.run uses MXC containment when available and uses the compatibility
    /// host fallback when MXC is unavailable unless strict blocking is enabled. Unsupported
    /// sandbox request features are rejected while sandboxing remains enabled.
    /// When <c>false</c>, system.run always runs on the host as it did before
    /// MXC support was added.
    /// </summary>
    public bool SystemRunSandboxEnabled { get; set; } = true;

    /// <summary>
    /// When sandboxing is enabled but MXC is unavailable, block system.run
    /// instead of using the compatibility host fallback. Default <c>false</c>
    /// preserves the pre-MXC host fallback unless the operator opts into strict
    /// fail-closed behavior.
    /// </summary>
    public bool SystemRunBlockHostFallbackWhenMxcUnavailable { get; set; } = false;

    /// <summary>
    /// When sandboxed, allow system.run commands to reach the public internet.
    /// Default false — most shell commands are local-only.
    /// </summary>
    public bool SystemRunAllowOutbound { get; set; } = false;

    /// <summary>
    /// When sandboxed, allow system.run commands to use Windows UI system calls.
    /// This is required by PowerShell and some console utilities on current MXC
    /// processcontainer backends. Default false preserves Win32k syscall blocking.
    /// </summary>
    public bool SystemRunAllowWindowsUi { get; set; } = false;

    /// <summary>
    /// Clipboard access policy inside the sandbox. Default <c>None</c> — the
    /// sandboxed payload cannot see or change the user's clipboard.
    /// </summary>
    public SandboxClipboardMode SandboxClipboard { get; set; } = SandboxClipboardMode.None;

    /// <summary>
    /// Per-folder access grants. Each well-known user folder can be
    /// individually opened to the sandbox in read-only or read-write mode.
    /// Default for all: <c>null</c> (blocked).
    /// </summary>
    public SandboxFolderAccess? SandboxDocumentsAccess { get; set; }
    public SandboxFolderAccess? SandboxDownloadsAccess { get; set; }
    public SandboxFolderAccess? SandboxDesktopAccess { get; set; }

    /// <summary>
    /// User-picked custom folders to expose to the sandbox.
    /// </summary>
    public List<SandboxCustomFolder>? SandboxCustomFolders { get; set; }

    /// <summary>
    /// Maximum execution time per sandboxed command. Default 30s.
    /// Range enforced in UI: 5_000 .. 300_000 ms.
    /// </summary>
    public int SandboxTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Maximum stdout/stderr returned from a sandboxed command. Default 4 MiB.
    /// </summary>
    public long SandboxMaxOutputBytes { get; set; } = 4 * 1024 * 1024;

    // ── (Voice / STT settings consolidated into the block above.) ──

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    public static SettingsData? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SettingsData>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
