using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Sessions;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Updatum;
using WinUIEx;
using SetupCompletedEventArgs = OpenClaw.SetupEngine.UI.SetupCompletedEventArgs;

namespace OpenClawTray;

public partial class App : Application, OpenClawTray.Services.IAppCommands, IPermissionsPageRuntimeHost
{
    internal static readonly UpdatumManager AppUpdater = new("openclaw", "openclaw-windows-node")
    {
        FetchOnlyLatestRelease = true,
        InstallUpdateSingleFileExecutableName = "OpenClaw.Tray.WinUI",
    };

    private ITrayController? _trayController;
    private IWindowManager? _windowManager;
    private GatewayConnectionManager? _connectionManager;
    private GatewayDirectConnectService? _gatewayDirectConnectService;
    private GatewayRegistry? _gatewayRegistry;
    private OpenClawTray.Services.ManagedLocalGatewayAutoRepairMonitor? _managedLocalAutoRepairMonitor;
    private ManagedLocalGatewayPortProvenanceService? _managedLocalPortProvenance;
    private OpenClawTray.Chat.OpenClawChatCoordinator? _chatCoordinator;
    private ILocalAiRuntime? _localAiRuntime;

    /// <summary>
    /// Root DI composition root, built once during startup and disposed during
    /// shutdown. The container only owns the presentation infrastructure it creates
    /// (navigation scope manager + any open page-view-model scope); App-owned
    /// services are registered as pre-built instances, so the container never
    /// disposes them and there is no double-dispose.
    /// </summary>
    private ServiceProvider? _services;

    /// <summary>
    /// Page type → view-model type map used by the navigation activation hook. The Settings
    /// page resolves its view model from DI and binds it as the page DataContext; pages absent
    /// from the map take the no-op activation path.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, Type> PageViewModelMap =
        new Dictionary<Type, Type>
        {
            [typeof(Pages.SettingsPage)] = typeof(SettingsPageViewModel),
            [typeof(Pages.PermissionsPage)] = typeof(PermissionsPageViewModel),
            [typeof(Pages.LocalAiPage)] = typeof(LocalAiPageViewModel),
        };

    /// <summary>The root service provider, or null before startup / after shutdown.</summary>
    internal IServiceProvider? Services => _services;

    /// <summary>The settings facade, or null before startup / after shutdown.</summary>
    internal ISettingsStore? SettingsStore => _services?.GetService<ISettingsStore>();

    /// <summary>Resolves the page activator used by <c>HubWindow</c>'s navigation hook.</summary>
    internal IPageActivator? PageActivator => _services?.GetService<IPageActivator>();
    /// <summary>
    /// Cached reference to the most recently constructed local-setup engine. Used by
    /// <see cref="OnPairingStatusChanged"/> to suppress the "copy pairing command" toast
    /// during Phase 14 auto-pair (Bug #2, manual test 2026-05-05). Null when no local
    /// setup has run in this app lifetime.
    /// </summary>
    /// <summary>The persistent gateway client. Used by the onboarding wizard for RPC calls.</summary>
    public IOperatorGatewayClient? GatewayClient => _connectionManager?.OperatorClient;
    public GatewayRegistry? Registry => _gatewayRegistry;
    public GatewayConnectionManager? ConnectionManager => _connectionManager;
    internal GatewayDirectConnectService? GatewayDirectConnectService =>
        _gatewayDirectConnectService;
    internal ManagedLocalGatewayPortProvenanceService? ManagedLocalPortProvenance =>
        _managedLocalPortProvenance;
    internal SettingsManager Settings => _settings ?? throw new InvalidOperationException("Settings are not initialized.");
    internal SettingsManager? SettingsOrNull => _settings;
    internal string DataDirectoryPath => DataPath;

    /// <summary>The active hub window, exposed so pages can obtain an HWND for file pickers.</summary>
    internal Microsoft.UI.Xaml.Window? ActiveHubWindow => _windowManager?.ActiveHubWindow;
    /// <summary>The current voice service instance (node or standalone).</summary>
    internal VoiceService? VoiceService => _nodeService?.VoiceService ?? _standaloneVoiceService;
    /// <summary>The full device ID of the local node service (if running).</summary>
    internal string? NodeFullDeviceId => _nodeService?.FullDeviceId;
    /// <summary>Live node service instance used by settings surfaces for MCP status.</summary>
    internal NodeService? ActiveNodeService => _nodeService;
    internal ExecApprovalsStore ExecApprovalsStore =>
        _execApprovalsStore ??= new ExecApprovalsStore(
            AppIdentity.ResolveRoamingDataDirectory(),
            new AppLogger());

    /// <summary>
    /// Session key that the chat surface should select on its next mount.
    /// Used when the user clicks a session from SessionsPage or a notification
    /// while the HubWindow may not yet exist. Consumed (cleared) by ChatPage.
    /// </summary>
    public string? PendingChatSessionKey { get; set; }

    public OpenClawTray.Chat.OpenClawChatDataProvider? ChatProvider => _chatCoordinator?.Provider;
    private volatile bool _hubNativeChatSurfaceActive;
    private volatile bool _trayNativeChatSurfaceActive;
    internal bool IsNativeChatSurfaceActive => _hubNativeChatSurfaceActive || _trayNativeChatSurfaceActive;

    internal void SetHubNativeChatSurfaceActive(bool active) => _hubNativeChatSurfaceActive = active;
    internal void SetTrayNativeChatSurfaceActive(bool active) => _trayNativeChatSurfaceActive = active;

    /// <summary>
    /// Raised after the tray-wide settings have been saved (either via the
    /// SettingsPage Save button or a direct toggle from the tray menu).
    /// Subscribers can refresh UI that depends on a setting (e.g. switching
    /// the chat surface between native chat and WebView2).
    /// </summary>
    public event EventHandler? SettingsChanged;
    public event EventHandler? ChatProviderChanged;
    private event EventHandler? PermissionsRuntimeChanged;

    /// <summary>
    /// Ensures the managed SSH tunnel is started using the current settings.
    /// Used by connection settings when the user picks the SSH topology.
    /// </summary>
    public void EnsureSshTunnelStarted()
    {
        if (_sshTunnelService == null || _settings == null)
            return;

        if (!_settings.UseSshTunnel)
        {
            _sshTunnelService.ResetNotConfigured();
            return;
        }

        var includeBrowserProxyForward = BrowserProxySshTunnelForwardPolicy.ShouldInclude(
            _settings.NodeBrowserProxyEnabled,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort);
        if (_settings.NodeBrowserProxyEnabled && !includeBrowserProxyForward)
        {
            Logger.Warn("SSH tunnel browser proxy forward disabled because the derived port would be invalid");
        }

        _sshTunnelService.EnsureStarted(
            _settings.SshTunnelUser,
            _settings.SshTunnelHost,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort,
            includeBrowserProxyForward,
            _settings.SshTunnelSshPort);
        _sshTunnelRecoveryBudget.Reset();
    }

    /// <summary>
    /// Returns the HWND of the active onboarding window, or IntPtr.Zero if none.
    /// Used by onboarding pages that need to host file pickers / dialogs.
    /// </summary>
    public IntPtr GetOnboardingWindowHandle() =>
        _windowManager?.GetOnboardingWindowHandle() ?? IntPtr.Zero;

    /// <summary>
    /// Returns the HWND of the Hub window, or IntPtr.Zero if it isn't open.
    /// Used by pages hosted in the Hub that need to parent a file picker
    /// or other Win32-style dialog. Pages should not hold a reference to
    /// the HubWindow directly (single-app-model rule); they call this
    /// when they need the handle and discard it afterwards.
    /// The window manager guards against the close-window race where the
    /// window is present but already in teardown.
    /// </summary>
    public IntPtr GetHubWindowHandle() =>
        _windowManager?.GetHubWindowHandle() ?? IntPtr.Zero;

    private SettingsManager? _settings;
    private OpenTelemetryEndpointConnection? _openTelemetryConnection;
    private SshTunnelService? _sshTunnelService;
    private readonly SshTunnelRecoveryBudget _sshTunnelRecoveryBudget = new();
    private GlobalHotkeyService? _globalHotkey;
    private Mutex? _mutex;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;
    private AppState? _appState;
    internal AppState? AppState => _appState;
    private UpdateCoordinator? _updateCoordinator;
    private GatewayService? _gatewayService;
    private PairingApprovalCoordinator? _pairingApprovalCoordinator;
    private OpenClawTray.Dialogs.PairingApprovalDialog? _pairingApprovalDialog;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pairingApprovalPollTimer;
    
    /// <summary>
    /// Cached connection status — sole writer is OnManagerStateChanged.
    /// Reads are safe from any thread; derives from the connection manager's state machine.
    /// </summary>
    private string? _lastManagerConnectedSideEffectsKey;
    private SettingsWriteOrigin? _trayPermissionWriteOrigin;
    private SettingsWriteOrigin? _appCapabilityPermissionWriteOrigin;
    private SettingsWriteOrigin? _trayAutoStartWriteOrigin;

    // FrozenDictionary for O(1) case-insensitive notification type → setting lookup — no per-call allocation.
    private static readonly System.Collections.Frozen.FrozenDictionary<string, Func<SettingsManager, bool>> s_notifTypeMap =
        new Dictionary<string, Func<SettingsManager, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["health"]    = s => s.NotifyHealth,
            ["urgent"]    = s => s.NotifyUrgent,
            ["reminder"]  = s => s.NotifyReminder,
            ["email"]     = s => s.NotifyEmail,
            ["calendar"]  = s => s.NotifyCalendar,
            ["build"]     = s => s.NotifyBuild,
            ["stock"]     = s => s.NotifyStock,
            ["info"]      = s => s.NotifyInfo,
            ["error"]     = s => s.NotifyUrgent,  // errors follow urgent setting
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private DiagnosticsClipboardService? _diagnosticsClipboard;
    private ToastService? _toastService;
    private AppNotificationService? _appNotificationService;
    internal AppNotificationService? AppNotifications => _appNotificationService;
    private string? _lastConnectionIssueNotificationKey;
    private readonly Dictionary<string, string> _reportedChannelIssueSignatures = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastSandboxRiskNotificationKey;
    private MxcAvailability? _sandboxRiskAvailabilityCache;
    private bool _sandboxRiskProbeInFlight;
    private int _sandboxRiskProbeGeneration;
    private DateTimeOffset _lastSandboxRiskProbeStartedAt;

    private const string ConnectionIssueNotificationId = "connection:issue";
    private const string ConnectionIssueNotificationDedupeKey = "connection:issue";
    private const string McpStartupNotificationId = "mcp:startup";
    private const string McpStartupNotificationDedupeKey = "mcp:startup";
    private const string SandboxRiskNotificationId = "sandbox:risk";
    private const string SandboxRiskNotificationDedupeKey = "sandbox:risk";
    private static readonly TimeSpan SandboxRiskProbeRefreshInterval = TimeSpan.FromMinutes(5);
    
    // Node service (optional, enabled in settings)
    private NodeService? _nodeService;
    private ExecApprovalsStore? _execApprovalsStore;
    private string[]? _startupArgs;
    private string? _pendingProtocolUri;
    private bool _isPostSetupRestart;
    private string? _postSetupLaunch;
    // OPENCLAW_TRAY_DATA_DIR isolates a test instance: settings, logs, run marker,
    // crash log, exec approvals, and the single-instance mutex name all derive from it.
    private static readonly string? DataDirOverride =
        Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } v ? v : null;
    private static readonly string DataPath = AppIdentity.ResolveLocalDataDirectory();
    private static readonly string DeepLinkPipeName =
        DeepLinkSecurityPolicy.BuildCurrentUserScopedPipeName(DataPath);
    // Operator/node identity store. Normal installs use the build variant's roaming data folder.
    // Isolated test/dev runs set OPENCLAW_TRAY_DATA_DIR to the direct OpenClaw data
    // folder, and SetupEngine/GatewayRegistry write per-gateway identities there.
    private static readonly string IdentityDataPath = DataDirOverride
        ?? Path.Combine(
            Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.DataDirectoryName);
    private readonly AppCrashLogger _crashLogger = new(Path.Combine(DataPath, "crash.log"));
    private static readonly AppRunMarker s_runMarker = new(Path.Combine(DataPath, "run.marker"));

    public App()
    {
        WaitForRestartSourceIfRequested(Environment.GetCommandLineArgs());
        StartupInputConfigurator.Configure();

        // Language override for localization testing (e.g., OPENCLAW_LANGUAGE=zh-CN)
        var langOverride = Environment.GetEnvironmentVariable("OPENCLAW_LANGUAGE");
        if (!string.IsNullOrEmpty(langOverride))
        {
            // SECURITY: Whitelist known locale codes to prevent locale injection
            string[] allowedLocales = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br"];
            if (allowedLocales.Contains(langOverride.ToLowerInvariant()))
                LocalizationHelper.SetLanguageOverride(langOverride);
            else
                Logger.Warn($"[App] Ignoring invalid OPENCLAW_LANGUAGE value: {langOverride}");
        }

        // Wire the GatewayHostAccess localization indirection to LocalizationHelper.
        // The classifier defaults to identity (returns the resource key as-is) for unit-test
        // contexts that lack a WinUI runtime; in-app we point it at the real resource lookup.
        GatewayHostAccessLocalization.GetString = LocalizationHelper.GetString;
        SessionTitleFormatter.ConfigureLocalization(LocalizationHelper.GetString);
        GatewayHostAccessLocalization.Format = (key, args) => LocalizationHelper.Format(key, args);

        InitializeComponent();
        
        s_runMarker.Check();
        s_runMarker.MarkStarted();
        
        // Hook up crash handlers
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void WaitForRestartSourceIfRequested(string[] args)
    {
        var pidValue = GetArgValue(args, "--wait-for-pid");
        if (!int.TryParse(pidValue, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            pid <= 0 ||
            pid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
                process.WaitForExit(TimeSpan.FromSeconds(60));
        }
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        catch (ArgumentException)
        {
            // The source process already exited.
        }
        catch (Exception ex)
        {
            // slopwatch-ignore: SW003 Diagnostic logging fallback is best-effort and logging failure must not cascade.
            try { Logger.Warn($"Post-setup restart wait for PID {pid} failed: {ex.Message}"); } catch { }
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _crashLogger.Log("UnhandledException", e.Exception);
        e.Handled = true; // Try to prevent crash
    }

    /// <summary>
    /// Returns true if <paramref name="arg"/> is a deep link for this build variant.
    /// Release and dev schemes stay disjoint so one install cannot steal the other's activation.
    /// </summary>
    private static bool IsDeepLinkArg(string arg) =>
        DeepLinkParser.ParseDeepLink(arg, AppIdentity.ProtocolScheme) != null;

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        _crashLogger.Log("DomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _crashLogger.Log("UnobservedTaskException", e.Exception);
        e.SetObserved(); // Prevent crash
    }
    
    private void OnProcessExit(object? sender, EventArgs e)
    {
        s_runMarker.MarkEnded();
        try
        {
            Logger.Info($"Process exiting (ExitCode={Environment.ExitCode})");
        }
        catch (Exception ex)
        {
            // Process is exiting; the logger writer may already be torn down.
            // Nothing we can do — Trace.WriteLine matches the standard set in
            // Services/Logger.cs's own ProcessExit handler; Console.Error is a
            // belt-and-suspenders backup in case no Trace listener is attached.
            try { System.Diagnostics.Trace.WriteLine($"App.OnProcessExit: logger unavailable: {ex.GetType().Name}: {ex.Message}"); }
            catch (Exception) { /* Trace itself failed during process exit. */ }
            try { Console.Error.WriteLine($"Process exiting (logger unavailable): {ex.GetType().Name}: {ex.Message}"); }
            catch (Exception) { /* Console.Error itself failed during process exit — nothing left to call. */ }
        }

        try
        {
            Interlocked.Exchange(ref _openTelemetryConnection, null)?.Dispose();
        }
        catch (Exception ex)
        {
            try { System.Diagnostics.Trace.WriteLine($"App.OnProcessExit: OpenTelemetry dispose failed: {ex.GetType().Name}: {ex.Message}"); }
            catch (Exception) { }
        }
    }

    private void OnUiThread(Microsoft.UI.Dispatching.DispatcherQueueHandler action) => _dispatcherQueue?.TryEnqueue(action);

    /// <summary>
    /// Check if the app was launched via protocol activation (MSIX deep link).
    /// In WinUI 3, protocol activation is retrieved via AppInstance, not OnActivated.
    /// </summary>
    private static string? GetProtocolActivationUri()
    {
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.Protocol
                && activatedArgs.Data is global::Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                return protocolArgs.Uri?.ToString();
            }
        }
        catch (Exception ex)
        {
            // Not activated via protocol, or not packaged. Surface at Debug for diagnostics.
            Logger.Debug($"GetProtocolActivationUri: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Builds the root DI composition root exactly once. Registers the WinUI-free
    /// core (dispatcher/app-commands/settings as pre-built instances, the navigation
    /// scope manager, and transient page view models) plus the WinUI-bound adapters
    /// (navigation service + page activator). Built with scope and build-time
    /// validation so wiring errors surface at startup rather than first use. No
    /// registered service starts work in its constructor.
    /// </summary>
    private void InitializeServiceProvider()
    {
        if (_services is not null)
        {
            return;
        }

        if (_dispatcherQueue is null || _settings is null)
        {
            Logger.Warn("Skipping service provider init: dispatcher or settings not ready.");
            return;
        }

        var dispatcher = new WinUIDispatcher(_dispatcherQueue);
        var context = new AppServiceContext(
            dispatcher,
            this,
            _settings,
            ExecApprovalsStore,
            this,
            _localAiRuntime);

        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(context);

        // WinUI-bound registrations are added here (not in the pure core) so the core
        // stays testable in a pure net10 project.
        services.AddSingleton<INavigationService>(new AppNavigationService(
            dispatcher,
            navigate: tag => ((IAppCommands)this).Navigate(tag),
            canGoBack: () => _windowManager?.CanNavigateHubBack() == true,
            goBack: () => _windowManager?.NavigateHubBack()));
        services.AddSingleton<IPageActivator>(sp => new FramePageActivator(
            sp.GetRequiredService<NavigationScopeManager>(),
            PageViewModelMap));

        try
        {
            _services = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
            Logger.Info("Service provider initialized.");
        }
        catch (Exception ex)
        {
            // Additive plumbing must never take the tray down. A build/validation
            // failure is logged and leaves _services null, so the navigation
            // activation hook stays a no-op. Wiring regressions are caught by tests
            // (AppServiceRegistrationTests builds with ValidateOnBuild).
            _services = null;
            Logger.Error($"Service provider initialization failed: {ex}");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) =>
        AsyncEventHandlerGuard.Run(
            () => OnLaunchedAsync(args),
            new AppLogger(),
            nameof(OnLaunched));

    private async Task OnLaunchedAsync(LaunchActivatedEventArgs args)
    {
        _startupArgs = Environment.GetCommandLineArgs();
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _isPostSetupRestart = HasArg(_startupArgs, "--post-setup-restart");
        _postSetupLaunch = GetArgValue(_startupArgs, "--post-setup-launch");

        // -----------------------------------------------------------------------
        // CLI uninstall path — headless; never shows tray or any windows.
        // Approach: detect in OnLaunched before any UI is created (WinUI3 Main
        // is auto-generated; earliest interception point is OnLaunched).
        // Bypasses the single-instance mutex so the Inno uninstaller can invoke
        // this even while the tray is running.
        // -----------------------------------------------------------------------
        if (_startupArgs.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            await CliUninstallHandler.RunAsync(_startupArgs);
            return; // Environment.Exit called inside; defensive return
        }

        // Check for protocol activation (MSIX packaged apps receive deep links this way)
        string? protocolUri = GetProtocolActivationUri();

        // Single instance check - keep mutex alive for app lifetime.
        // When running with an isolated data dir (tests), suffix the mutex name so
        // the test instance does not collide with the user's regular tray app.
        // String.GetHashCode() is randomized per process since .NET Core 2.1, so
        // two test runs against the same data dir would otherwise pick different
        // mutex names — and `Math.Abs(int.MinValue)` overflows. Use a stable
        // SHA-256 prefix instead.
        // NOTE: The build variant's bare mutex name is also referenced by
        // installer.iss `AppMutex=` for install/uninstall race coordination.
        // The suffixed test-isolation variant is
        // intentionally not covered by AppMutex — production installs only
        // ever use the unsuffixed name.
        var mutexName = AppIdentity.MutexBaseName;
        if (DataDirOverride is not null)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(DataDirOverride));
            mutexName = $"{AppIdentity.MutexBaseName}-{Convert.ToHexString(hash, 0, 4)}";
        }
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        var ownsMutex = createdNew;
        if (!ownsMutex && _isPostSetupRestart)
        {
            try
            {
                Logger.Warn("Post-setup restart found an existing tray mutex after waiting for the old process; waiting briefly for mutex release.");
                ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                Logger.Warn("Post-setup restart acquired abandoned tray mutex.");
                ownsMutex = true;
            }
        }

        _activationRouter = new ActivationRouter(AppIdentity.ProtocolScheme, DeepLinkPipeName);

        if (!ownsMutex)
        {
            await _activationRouter.ForwardLaunchToPrimaryAsync(new LaunchActivationInput(
                protocolUri,
                _startupArgs,
                _postSetupLaunch,
                SetupShownDuringStartup: false), CancellationToken.None);
            Exit();
            return;
        }

        // Store protocol URI for processing after setup
        _pendingProtocolUri = protocolUri;

        var appUserModelIdRegistration = AppUserModelIdRegistrar.RegisterCurrentProcess(AppIdentity.AppUserModelId);
        if (appUserModelIdRegistration.Attempted && appUserModelIdRegistration.HResult < 0)
        {
            Logger.Warn($"Failed to set AppUserModelID '{AppIdentity.AppUserModelId}' (HRESULT=0x{appUserModelIdRegistration.HResult:X8}); toast sender name may fall back to the executable name.");
        }

        // Initialize settings before update check so skip selections can be remembered.
        _settings = new SettingsManager();
        // Seed chat tool-call visibility from persisted settings so the timeline
        // honors the Settings > Chat "Show tool calls and usage" toggle on launch.
        OpenClawTray.Chat.OpenClawReactorChatRoot.SetToolCallsVisible(_settings.ShowChatToolCalls);
        _settingsChangeCoordinator = CreateSettingsChangeCoordinator(_settings.ToSettingsData());
        _openTelemetryConnection = new OpenTelemetryEndpointConnection();
        await _openTelemetryConnection.ApplyAsync(
            OpenTelemetryEndpointOptions.FromSettings(_settings));
        _chatCoordinator = new OpenClawTray.Chat.OpenClawChatCoordinator(
            _settings,
            () => _nodeService,
            new AppLogger(),
            _dispatcherQueue is null
                ? null
                : OpenClawTray.Chat.ReactorChatHostExtensions.AsPost(_dispatcherQueue));
        DiagnosticsJsonlService.Configure(DataPath);

        // Central observable model + gateway event handler.
        _appState = new AppState(_dispatcherQueue);
        _windowManager = new OpenClawTray.Services.WindowManager(
            _dispatcherQueue!,
            new WindowManagerCallbacks(
                GetAppState: () => _appState,
                GetAppNotificationService: () => _appNotificationService,
                GetConnectionManager: () => _connectionManager,
                GetGatewayRegistry: () => _gatewayRegistry,
                GetSettings: () => _settings,
                GetNodeService: () => _nodeService,
                GetVoiceService: () => _nodeService?.VoiceService ?? _standaloneVoiceService,
                GetPageActivator: () => PageActivator,
                GetPendingChatSessionKey: () => PendingChatSessionKey,
                GetStartupArgs: () => _startupArgs,
                IsDeepLinkArg: IsDeepLinkArg,
                Connect: ReconnectWithSyncedBrowserProxyForward,
                Disconnect: () =>
                {
                    _ = _connectionManager?.DisconnectByUserAsync();
                    UpdateTrayIcon();
                },
                SettingsSaved: OnSettingsSaved,
                AdvancedSetupRequested: OnSetupAdvancedSetupRequested,
                SetupCompleted: OnSetupCompleted,
                ApplyTheme: ApplyThemePreference));
        _updateCoordinator = new UpdateCoordinator(
            AppUpdater,
            _appState,
            _settings,
            () => _windowManager?.DialogXamlRoot,
            refreshStatus: UpdateStatusDetailWindow,
            exit: Exit);
        _appState.UpdateInfo = UpdateCoordinator.BuildInitialInfo();
        _gatewayService = new GatewayService(_appState, _dispatcherQueue!);
        _gatewayService.ConnectionStatusChanged += OnGatewayConnectionStatusChanged;
        _gatewayService.AuthenticationFailed += OnGatewayAuthenticationFailed;
        _gatewayService.SessionCommandCompleted += OnGatewaySessionCommandCompleted;
        _gatewayService.NotificationReceived += OnGatewayNotificationReceived;
        _appState.PropertyChanged += OnAppStateChanged;

        _diagnosticsClipboard = new DiagnosticsClipboardService(BuildCommandCenterState);
        _toastService = new ToastService(() => _settings);
        _appNotificationService = new AppNotificationService();
        PublishSandboxRiskNotificationIfNeeded();

        // Inbound pairing approvals: surface a focused dialog + awareness toast when another
        // device/node requests pairing (Mac-parity). Getters are lazy so this can be created
        // before the connection manager / node service exist.
        _pairingApprovalCoordinator = new PairingApprovalCoordinator(
            getClient: () => _connectionManager?.OperatorClient,
            getOwnNodeIds: BuildOwnNodeIds,
            isPromptEnabled: () => _settings?.ShowPairingApprovalDialog ?? true,
            logger: new AppLogger());
        _pairingApprovalCoordinator.ApprovalRequested += OnPairingApprovalRequested;
        _pairingApprovalCoordinator.DecisionCompleted += OnPairingDecisionCompleted;
        _gatewayService.PairListsChanged += OnPairListsChanged;

        // Safety-net poll: the gateway broadcasts pair requests with dropIfSlow=true, so a busy
        // socket can silently drop a "device wants to connect" event and the operator would never
        // be prompted. A periodic reconcile recovers any missed request. RefreshFromGatewayAsync
        // no-ops unless connected with approval scope, so this is idle-cheap.
        _pairingApprovalPollTimer = _dispatcherQueue!.CreateTimer();
        _pairingApprovalPollTimer.Interval = TimeSpan.FromSeconds(20);
        _pairingApprovalPollTimer.IsRepeating = true;
        _pairingApprovalPollTimer.Tick += (_, _) => _ = _pairingApprovalCoordinator?.RefreshFromGatewayAsync();
        _pairingApprovalPollTimer.Start();

        DiagnosticsJsonlService.Write("app.start", new
        {
            nodeMode = _settings.EnableNodeMode,
            useSshTunnel = _settings.UseSshTunnel
        });

        // Isolated test instances must not replace the user's installed protocol handler.
        if (DataDirOverride is null)
            DeepLinkHandler.RegisterUriScheme();

        // Anchor the WinUI runtime so transient windows (UpdateDialog,
        // setup wizard, etc.) don't terminate the process when closed.
        // WinUI 3 Desktop's default DispatcherShutdownMode is
        // OnLastWindowClose — without this override, closing the
        // UpdateDialog on the startup path (when it is the only window)
        // would shut down the WinUI runtime mid-flight and kill the
        // in-progress download/extraction. We still control shutdown
        // explicitly via Application.Exit().
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // Check for updates before launching. Skip in test instances — no UI dialogs,
        // no network calls, no startup delay.
        if (DataDirOverride is null &&
            Environment.GetEnvironmentVariable("OPENCLAW_SKIP_UPDATE_CHECK") != "1")
        {
            var shouldLaunch = await _updateCoordinator.CheckForUpdatesAsync();
            if (!shouldLaunch)
            {
                Exit();
                return;
            }
        }

        // Register toast activation handler
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        _sshTunnelService = new SshTunnelService(new AppLogger());
        _sshTunnelService.TunnelExited += OnSshTunnelExited;

        // Initialize tray icon FIRST (window-less pattern from WinUIEx).
        // The tray is application chrome and must always survive any failure
        // in the onboarding wizard. OnLaunched delegates through a guarded
        // async boundary so onboarding failures are logged instead of escaping
        // before the tray ever initializes.
        InitializeTrayIcon();
        ShowSurfaceImprovementsTipIfNeeded();

        // The singleton Local AI installation belongs to exactly one explicit
        // setup-managed local WSL gateway. Load the registry before composing its
        // lifecycle so no hardcoded distro can receive provider commands.
        var appLogger = new AppLogger();
        _gatewayRegistry = new GatewayRegistry(SettingsManager.SettingsDirectoryPath, logger: appLogger);
        _gatewayRegistry.Load();
        var localAiLogger = new AppLogger();
        var localAiPaths = new LocalAiPaths(AppIdentity.ResolveSetupLocalDataDirectory());
        var localAiEndpointLifecycle = new LocalAiGatewayProviderCoordinator(
            new WslExeCommandRunner(localAiLogger),
            new LocalAiGatewayDistroResolver(_gatewayRegistry),
            localAiLogger);
        _localAiRuntime = new LlamaServerRuntimeService(
            new LlamaServerRuntimeOptions
            {
                Paths = localAiPaths,
                EndpointLifecycle = localAiEndpointLifecycle,
            },
            localAiLogger);

        // Build the DI composition root AFTER the tray is up, so additive plumbing
        // can never delay or preempt tray initialization. It only needs the
        // dispatcher + settings (created above) and failures are non-fatal.
        InitializeServiceProvider();
        StartLocalAiRouterInBackground();

        // Initialize connection manager before setup flow.
        var credentialResolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var clientFactory = new GatewayClientFactory();
        var diagnostics = new ConnectionDiagnostics();
        var nodeConnector = new NodeConnector(appLogger, diagnostics);
        // Bridge: whenever NodeConnector creates a fresh WindowsNodeClient (initial
        // connect or reconnect), register the node's capabilities on it BEFORE the
        // outbound "connect" handshake runs. Without this hookup the gateway sees
        // the node as having no advertised commands and the agent cannot invoke
        // anything on it. _nodeService may be null at app startup (constructed
        // lazily); when null we no-op and the gateway will see an empty caps list
        // until the next reconnect after _nodeService becomes available.
        nodeConnector.ClientCreated += (_, args) =>
        {
            try
            {
                // A node client was just created (manager auto-start OR setup engine
                // EnsureNodeConnectedAsync). We MUST have a NodeService to register
                // capabilities on this client before the outbound "connect" goes out —
                // see NodeConnector.cs:66. Build it lazily here so we never depend on
                // OnLaunched ordering. EnsureNodeService is
                // idempotent — it returns the existing instance if already built.
                // Without this, _nodeService?.AttachClient below is a silent no-op and
                // the gateway sees the node with caps=0/cmds=0 (regression introduced
                // 2026-05-12 in 62533e2 when capability registration moved to this
                // lazy bridge pattern).
                if (_settings == null)
                {
                    Logger.Warn("[App] NodeConnector.ClientCreated fired before settings were initialized; node may connect without capabilities");
                    diagnostics.Record("node", "WARNING: settings unavailable; cannot initialize NodeService for capability binding");
                    throw new InvalidOperationException("Settings unavailable during node capability binding.");
                }

                EnsureNodeService(_settings);

                diagnostics.Record("node", $"ClientCreated fired, _nodeService null={_nodeService is null}");
                if (_nodeService == null)
                {
                    Logger.Warn("[App] NodeService unavailable during ClientCreated; node may connect with caps=0/cmds=0");
                    diagnostics.Record("node", "WARNING: NodeService unavailable; cannot bind node capabilities");
                    throw new InvalidOperationException("NodeService unavailable during node capability binding.");
                }

                _nodeService.AttachClient(args.Client, args.BearerToken);
                WireAppCapabilityHandlers();
                var client = args.Client;
                diagnostics.Record("node", $"After AttachClient: caps={client.Capabilities.Count}, cmds={client.RegisteredCommandCount}");
                if (client.RegisteredCommandCount > 0)
                    diagnostics.Record("node", $"Commands sample: {string.Join(", ", client.RegisteredCommandsSample)}...");
                else
                {
                    Logger.Warn("[App] Node capability binding produced 0 commands before connect");
                    diagnostics.Record("node", "WARNING: 0 commands registered on node client before connect");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[App] NodeConnector.ClientCreated handler failed: {ex.Message}");
                diagnostics.Record("node", $"ClientCreated handler THREW: {ex.Message}");
                throw;
            }
        };
        // SshTunnelService implements ISshTunnelManager directly — no shim needed
        var managedLocalPortProvenance = _managedLocalPortProvenance =
            new ManagedLocalGatewayPortProvenanceService(appLogger);
        _connectionManager = new GatewayConnectionManager(
            credentialResolver, clientFactory, _gatewayRegistry, appLogger,
            identityStore: new DeviceIdentityFileStore(appLogger),
            nodeConnector: nodeConnector,
            isNodeEnabled: IsGatewayNodeEnabled,
            diagnostics: diagnostics,
            tunnelManager: _sshTunnelService,
            endpointProvenanceProbe: managedLocalPortProvenance.InspectAsync,
            validationTunnelFactory: () => new SshTunnelService(appLogger));
        _connectionManager.OperatorClientChanged += OnOperatorClientChanged;
        _connectionManager.StateChanged += OnManagerStateChanged;
        _gatewayDirectConnectService = new GatewayDirectConnectService(
            _connectionManager,
            _gatewayRegistry,
            _settings,
            EnsureSshTunnelStarted,
            appLogger);

        // First-run check (also supports forced onboarding for testing).
        // Wrapped in try/catch so a wizard construction failure cannot tear
        // down the tray; user can retry via the Setup Guide menu item.
        var setupShownDuringStartup = false;
        try
        {
            if ((!_isPostSetupRestart && RequiresSetup(_settings)) ||
                Environment.GetEnvironmentVariable("OPENCLAW_FORCE_ONBOARDING") == "1")
            {
                await ShowOnboardingAsync();
                setupShownDuringStartup = true;
            }
        }
        catch (DeviceIdentityLoadException ex)
        {
            Logger.Error($"Stored device identity load failed during launch setup detection: {ex.InnerException?.Message}");
            ShowTransientConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"Onboarding failed during launch (tray remains available): {ex}");
        }

        // Packaged builds must reconcile auto-start with Windows after settings load.
        // Nothing else does: SettingsChangeCoordinator.Apply only runs on a settings
        // *change*, so a preserved AutoStart=true would be shown as enabled while the
        // manifest's StartupTask sat disabled. Backgrounded so a slow StartupTask query
        // cannot delay tray availability.
        ObserveBackgroundFault(
            ReconcileAutoStartOnStartupAsync(),
            "[App] Failed to reconcile auto-start with Windows");

        // Ensure NodeService is constructed BEFORE InitializeGatewayClient triggers a
        // NodeConnector connect. The NodeConnector.ClientCreated event subscription
        // above relies on _nodeService being non-null to register capabilities on the
        // new WindowsNodeClient. If we don't pre-construct here, the first connect
        // happens with empty caps and the gateway records this node as having no
        // advertised commands (which leaves the agent unable to invoke anything on it).
        // The method is idempotent — safe to call here AND later if first-run setup runs.
        if (ShouldInitializeNodeService() && _settings != null)
        {
            EnsureNodeService(_settings);
        }

        // Initialize connections — always create operator client for UI data,
        // additionally create node service for gateway node mode or local MCP.
        // Re-arm the WSL keepalive so the local gateway VM stays up across tray
        // restarts and across the 20s WSL vmIdleTimeout window observed on some
        // hosts. Fire-and-forget on a background task so a slow LxssManager at
        // cold logon never delays InitializeGatewayClient. The keepalive itself
        // runs detached from the tray — see WslDistroKeepAlive in LocalGatewaySetup.cs.
        var wslKeepAlive = new WslGatewayKeepAliveService(() => _settings, () => _gatewayRegistry);
        _ = Task.Run(wslKeepAlive.TryEnsureAsync);

        // Automatic self-repair for app-owned setup-managed local WSL gateways: if the local
        // gateway process goes down, probe it and (only if actually unreachable) restart the WSL
        // distro, re-arm the keepalive, and reconnect — without user action. Strictly gated to
        // setup-managed local WSL gateways; the reconnect is gateway-pinned + cancellable so a
        // gateway switch or shutdown mid-repair cannot disrupt another gateway. Kill switch:
        // Settings.EnableManagedLocalGatewayAutoRepair.
        var managedLocalRestarter = new OpenClawTray.Services.WslManagedLocalGatewayRestarter(
            new WslGatewayController(new WslExeCommandRunner(new AppLogger(), defaultTimeout: TimeSpan.FromSeconds(30)), appLogger));
        var managedLocalRepairCoordinator = new OpenClawTray.Services.ManagedLocalGatewayRepairCoordinator(
            _gatewayRegistry,
            managedLocalRestarter,
            (url, ct) => OpenClawTray.Services.GatewayReachabilityProbe.IsReachableAsync(url, ct),
            (gatewayId, ct) => _connectionManager?.ReconnectIfCurrentAsync(gatewayId, ct) ?? Task.FromResult(false),
            () => _connectionManager?.CurrentSnapshot.OperatorState == RoleConnectionState.Connected,
            _ => wslKeepAlive.TryEnsureAsync(),
            diagnostics,
            appLogger,
            tryAcquireLifecycleLease: () => _connectionManager?.TryAcquireGatewayLifecycleLease(),
            isRestartStillWarranted: () => OpenClawTray.Services.ManagedLocalGatewayAutoRepairMonitor.IsRepairCandidate(
                _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle),
            isAutomaticRepairAllowed: gatewayId => _connectionManager?.IsAutomaticReconnectAllowed(gatewayId) ?? false,
            repairPortConflictAsync: (record, ct) =>
                OpenClawTray.Services.ManagedLocalGatewayAutoRepairMonitor.IsRepairCandidate(
                    _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle) &&
                _connectionManager?.CurrentSnapshot.OperatorErrorKind == GatewayErrorKind.LocalPortConflict
                    ? managedLocalPortProvenance.RepairConflictAsync(
                        record,
                        ct,
                        canContinue: () =>
                            OpenClawTray.Services.WslKeepAlivePolicy.IsSameSetupManagedGateway(
                                record,
                                _gatewayRegistry?.GetActive()) &&
                            (_connectionManager?.IsAutomaticReconnectAllowed(record.Id) ?? false))
                    : Task.FromResult(new ManagedLocalPortConflictRepairResult(
                        ManagedLocalPortConflictRepairOutcome.NotNeeded)),
            isPortConflictCandidate: () =>
                _connectionManager?.CurrentSnapshot.OperatorErrorKind == GatewayErrorKind.LocalPortConflict);
        _managedLocalAutoRepairMonitor = new OpenClawTray.Services.ManagedLocalGatewayAutoRepairMonitor(
            () => _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle,
            _gatewayRegistry,
            ct => managedLocalRepairCoordinator.TryRepairActiveGatewayAsync(ct),
            id => managedLocalRepairCoordinator.ResetAttemptBudget(id),
            () => (_settings?.EnableManagedLocalGatewayAutoRepair ?? true)
                  && !(_connectionManager?.IsManualGatewayLifecycleInProgress ?? false),
            diagnostics,
            appLogger,
            isAutomaticRepairAllowed: gatewayId => _connectionManager?.IsAutomaticReconnectAllowed(gatewayId) ?? false);
        _managedLocalAutoRepairMonitor.Start();

        InitializeGatewayClient();

        // Pre-warm chat window (WebView2 init takes 1-3s, do it now so left-click is instant)
        if (_settings != null &&
            TryResolveChatCredentials(out var prewarmUrl, out var prewarmToken, out _, out var prewarmIsBootstrapToken) &&
            !prewarmIsBootstrapToken)
        {
            _windowManager.PrewarmChat(new ChatWindowRequest(prewarmUrl, prewarmToken));
            // Window is created but hidden — WebView2 initializes in the background
        }

        // Start forwarded-activation listener (current-user IPC)
        await _activationRouter.StartForwardedActivationListenerAsync(this, CancellationToken.None);

        // Register global hotkey if enabled
        if (_settings?.GlobalHotkeyEnabled == true)
        {
            _globalHotkey = new GlobalHotkeyService();
            _globalHotkey.VoiceHotkeyPressed += OnVoiceHotkeyPressed;
            _globalHotkey.SettingsHotkeyPressed += OnSettingsHotkeyPressed;
            _globalHotkey.Register();
        }

        // Process startup deep link (command-line, MSIX protocol activation, or post-setup chat)
        var launchPlan = _activationRouter.PlanLaunch(new LaunchActivationInput(
            _pendingProtocolUri,
            _startupArgs,
            _postSetupLaunch,
            setupShownDuringStartup));
        await _activationRouter.DispatchPlanAsync(launchPlan, this, CancellationToken.None);

        Logger.Info("Application started (WinUI 3)");
    }

    /// <summary>
    /// Starts only the lightweight llama-server router. The verified model preset is
    /// explicitly load-on-startup=false, so the first inference request owns model load.
    /// This observed background task must never delay tray or gateway startup.
    /// </summary>
    private void StartLocalAiRouterInBackground()
    {
        ILocalAiRuntime? runtime = _localAiRuntime;
        if (runtime is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();
                Logger.Info($"Local AI router startup state: {snapshot.State}");
            }
            catch (ObjectDisposedException)
            {
                // Shutdown won the race with background router startup.
            }
            catch (Exception ex)
            {
                Logger.Warn($"Local AI router startup failed: {ex.Message}");
            }
        });
    }

    private void InitializeTrayIcon()
    {
        // Initialize keep-alive window first to anchor WinUI runtime
        _windowManager?.InitializeRuntimeAnchor();

        _trayController = new TrayController(new TrayControllerCallbacks(
            CaptureMenuSnapshot: CaptureTrayMenuSnapshot,
            CaptureIconSnapshot: CaptureTraySnapshot,
            IsOperatorConnected: () =>
                _connectionManager?.CurrentSnapshot.OperatorState == RoleConnectionState.Connected,
            ShowChat: ShowChatWindow,
            ShowConnection: () => ShowHub("connection"),
            DispatchMenuAction: action => OnTrayMenuItemClicked(null, action),
            ApplyTheme: ApplyThemePreference,
            IsDispatcherAvailable: () => _dispatcherQueue != null,
            HasThreadAccess: () => _dispatcherQueue == null || _dispatcherQueue.HasThreadAccess,
            Marshal: OnUiThread,
            LogCrash: _crashLogger.Log));
        _trayController.Initialize();
    }

    internal void ApplyThemePreferenceToOpenWindows()
    {
        _windowManager?.ApplyThemeToOpenWindows();
        _trayController?.ApplyTheme();
    }

    private void ApplyThemePreference(Window? window)
    {
        if (_settings is null)
            return;

        ThemeHelper.ApplyTheme(window, _settings.AppTheme);
    }

    internal void ShowChatWindow()
    {
        if (_settings == null) return;
        if (!TryResolveChatCredentials(out var url, out var token, out var credentialSource, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway URL or credential is not configured");
            return;
        }

        if (isBootstrapToken)
        {
            ShowConnectionSettingsForPairingIssue(
                "ChatWindow",
                "Gateway pairing is not complete");
            return;
        }

        Logger.Info($"[ChatWindow] Quick-chat credentials resolved from {credentialSource}");
        _windowManager?.ShowChat(new ChatWindowRequest(url, token));
    }

    private void ShowCanvasWindow()
    {
        if (_settings?.NodeCanvasEnabled == false)
        {
            Logger.Warn("[Canvas] Canvas capability is disabled; opening capability settings");
            _windowManager?.ShowCanvas(new CanvasWindowRequest(
                CanvasSurfaceDestination.Capabilities,
                ShowCanvas: null));
            return;
        }

        if (_nodeService == null)
        {
            Logger.Warn("[Canvas] Windows node is not initialized; opening connection settings");
            _windowManager?.ShowCanvas(new CanvasWindowRequest(
                CanvasSurfaceDestination.Connection,
                ShowCanvas: null));
            return;
        }

        if (_nodeService.IsPendingApproval || !_nodeService.IsPaired)
        {
            Logger.Warn("[Canvas] Windows node pairing is not complete; opening connection settings");
            _windowManager?.ShowCanvas(new CanvasWindowRequest(
                CanvasSurfaceDestination.Connection,
                ShowCanvas: null));
            return;
        }

        _windowManager?.ShowCanvas(new CanvasWindowRequest(
            CanvasSurfaceDestination.Canvas,
            _nodeService.ShowCanvasWindow));
    }

    private void ShowConnectionSettingsForPairingIssue(string source, string reason)
    {
        Logger.Warn($"[{source}] {reason}; opening connection settings");
        ShowHub("connection");
    }

    // Voice overlay disabled — inline chat voice mode is used instead.
    // private VoiceOverlayWindow? _voiceOverlayWindow;
    private VoiceService? _standaloneVoiceService;

    /// <summary>
    /// Gets the current VoiceService instance (from the node service or standalone).
    /// Returns null if STT is not enabled.
    /// </summary>
    public VoiceService? VoiceServiceInstance =>
        _nodeService?.VoiceService ?? EnsureStandaloneVoiceService();

    // Voice overlay disabled — inline chat voice mode is used instead.
    // Kept for potential future re-enablement.
    /*
    private void ShowVoiceOverlay()
    {
        var voiceService = _nodeService?.VoiceService ?? EnsureStandaloneVoiceService();
        if (voiceService == null)
        {
            // STT not enabled — show settings
            ShowHub("voice");
            return;
        }

        if (_voiceOverlayWindow == null || _voiceOverlayWindow.AppWindow == null)
        {
            _voiceOverlayWindow = new VoiceOverlayWindow(voiceService, new AppLogger());
            _voiceOverlayWindow.Closed += (_, _) => _voiceOverlayWindow = null;
            // Wire transcription to gateway chat when connected
            _voiceOverlayWindow.TextSubmitted += text =>
            {
                var client = _connectionManager?.OperatorClient;
                if (client != null && _appState!.Status == ConnectionStatus.Connected)
                {
                    _ = client.SendChatMessageAsync(text);
                }
            };
            // Wire Settings button → open the Hub on the Voice & Audio page.
            _voiceOverlayWindow.SettingsRequested += () =>
            {
                OnUiThread(() => ShowHub("voice"));
            };
        }

        _voiceOverlayWindow.Activate();
    }
    */

    private VoiceService? EnsureStandaloneVoiceService()
    {
        if (_settings?.NodeSttEnabled != true)
            return null;

        return _standaloneVoiceService ??= new VoiceService(new AppLogger(), _settings);
    }

    private void OnTrayMenuItemClicked(object? sender, string action)
    {
        switch (action)
        {
            case "status": ShowStatusDetail(); break;
            case "reconnect": ReconnectWithSyncedBrowserProxyForward(); break;
            case "disconnect":
                _ = _connectionManager?.DisconnectByUserAsync();
                LocalDisconnectCleanup();
                break;
            case "connection": ShowHub("connection"); break;
            case "permissions": ShowHub("permissions"); break;
            case "dashboard": OpenDashboard(); break;
            case "diagnostics": ShowHub("debug"); break;
            case "canvas": ShowCanvasWindow(); break;
            case "openchat": ShowHub("chat"); break;
            case "voice": ShowHub("voice"); break; // was: ShowVoiceOverlay()
            case "webchat": ShowWebChat(); break;
            case "hub": ShowHub(); break;
            case "companion":
                // If disconnected, open Connection page (status, gateways, add flow)
                // If connected, open Hub at default page
                if (_appState!.Status != ConnectionStatus.Connected)
                    ShowHub("connection");
                else
                    ShowHub();
                break;
            case "quicksend": break; // Quick Send removed
            case "history": ShowHub("channels"); break;
            case "activity": ShowHub("channels"); break;
            case "healthcheck": _ = RunHealthCheckAsync(userInitiated: true); break;
            case "checkupdates": _ = _updateCoordinator!.CheckForUpdatesUserInitiatedAsync(); break;
            case "settings": ShowSettings(); break;
            case "setup": _ = ShowOnboardingAsync(); break;
            case "autostart": ToggleAutoStart(); break;
            case "log": OpenLogFile(); break;
            case "logfolder": OpenLogFolder(); break;
            case "configfolder": OpenConfigFolder(); break;
            case "diagnosticsfolder": OpenDiagnosticsFolder(); break;
            case "connectionstatus": ShowConnectionStatusWindow(); break;
            case "supportcontext": _diagnosticsClipboard!.CopySupportContext(); break;
            case "debugbundle": _diagnosticsClipboard!.CopyDebugBundle(); break;
            case "browsersetup": _diagnosticsClipboard!.CopyBrowserSetupGuidance(); break;
            case "portdiagnostics": _diagnosticsClipboard!.CopyPortDiagnostics(); break;
            case "capabilitydiagnostics": _diagnosticsClipboard!.CopyCapabilityDiagnostics(); break;
            case "nodeinventory": _diagnosticsClipboard!.CopyNodeInventory(); break;
            case "channelsummary": _diagnosticsClipboard!.CopyChannelSummary(); break;
            case "activitysummary": _diagnosticsClipboard!.CopyActivitySummary(); break;
            case "extensibilitysummary": _diagnosticsClipboard!.CopyExtensibilitySummary(); break;
            case "restartsshtunnel": RestartSshTunnel(); break;
            case "copydeviceid": CopyDeviceIdToClipboard(); break;
            case "copynodesummary": CopyNodeSummaryToClipboard(); break;
            case "exit": ExitApplication(); break;
            case "about": ShowHub("about"); break;
            default:
                if (action.StartsWith("perm-toggle|", StringComparison.Ordinal))
                {
                    ToggleTrayPermission(action);
                }
                else if (action.StartsWith("session-reset|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("reset", action["session-reset|".Length..]);
                else if (action.StartsWith("session-compact|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("compact", action["session-compact|".Length..]);
                else if (action.StartsWith("session-delete|", StringComparison.Ordinal))
                    _ = ExecuteSessionActionAsync("delete", action["session-delete|".Length..]);
                else if (action.StartsWith("session-thinking|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("thinking", split[2], split[1]);
                }
                else if (action.StartsWith("session-verbose|", StringComparison.Ordinal))
                {
                    var split = action.Split('|', 3);
                    if (split.Length == 3)
                        _ = ExecuteSessionActionAsync("verbose", split[2], split[1]);
                }
                else if (action.StartsWith("session:", StringComparison.Ordinal))
                    OpenDashboard($"sessions/{action[8..]}");
                else if (action.StartsWith("dashboard:", StringComparison.Ordinal))
                    OpenDashboard(action["dashboard:".Length..]);
                else if (action.StartsWith("activity:", StringComparison.Ordinal))
                    ShowHub("channels");
                else if (action.StartsWith("channel:", StringComparison.Ordinal))
                    ToggleChannel(action[8..]);
                else
                    // Default: treat as a Hub navigation tag (e.g. "nodes", "agent:main:sessions")
                    ShowHub(action);
                break;
        }
    }
    
    private void CopyDeviceIdToClipboard()
    {
        if (_nodeService?.FullDeviceId == null) return;
        
        try
        {
            CopyTextToClipboard(_nodeService.FullDeviceId);
            
            // Show toast confirming copy
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_DeviceIdCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_DeviceIdCopiedDetail"), _nodeService.ShortDeviceId)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy device ID: {ex.Message}");
        }
    }

    private void CopyNodeSummaryToClipboard()
    {
        if (_appState!.Nodes.Length == 0) return;

        try
        {
            var summary = NodeSummaryText.Build(_appState!.Nodes);

            CopyTextToClipboard(summary);

            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_NodeSummaryCopied"))
                .AddText(string.Format(LocalizationHelper.GetString("Toast_NodeSummaryCopiedDetail"), _appState!.Nodes.Length)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy node summary: {ex.Message}");
        }
    }

    private async Task ExecuteSessionActionAsync(string action, string sessionKey, string? value = null)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null || string.IsNullOrWhiteSpace(sessionKey)) return;

        try
        {
            if (action is "reset" or "compact" or "delete")
            {
                var kind = action switch
                {
                    "reset" => SessionActionKind.Reset,
                    "compact" => SessionActionKind.Compact,
                    _ => SessionActionKind.Delete,
                };

                var session = _appState?.Sessions?.FirstOrDefault(s => s.Key == sessionKey);
                var mainState = SessionActionPlanner.ResolveMainState(
                    sessionKey,
                    rowIsMain: session?.IsMain,
                    mainSessionKey: client.MainSessionKey,
                    sessions: _appState?.Sessions);
                var isMain = mainState == SessionMainState.Main;
                var displayName = session?.DisplayName;

                if (!SessionActionPlanner.IsAllowed(kind, mainState, out var blockedReason))
                {
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                        .AddText(blockedReason ?? string.Empty));
                    return;
                }

                var prompt = SessionActionPlanner.BuildPrompt(kind, sessionKey, displayName, isMain);
                if (prompt is not null)
                {
                    var localizedPrompt = SessionActionPromptLocalizer.Localize(prompt);
                    var confirmed = await ConfirmSessionActionAsync(
                        localizedPrompt.Title,
                        localizedPrompt.Body,
                        localizedPrompt.ConfirmLabel);
                    if (!confirmed) return;
                }
            }

            if (action == "delete")
            {
                var session = _appState?.Sessions?.FirstOrDefault(s => s.Key == sessionKey);
                var mainState = SessionActionPlanner.ResolveMainState(
                    sessionKey,
                    rowIsMain: session?.IsMain,
                    mainSessionKey: client.MainSessionKey,
                    sessions: _appState?.Sessions);
                if (!SessionActionPlanner.IsAllowed(SessionActionKind.Delete, mainState, out var blockedReason))
                {
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                        .AddText(blockedReason ?? string.Empty));
                    return;
                }
            }

            var sent = action switch
            {
                "reset" => await client.ResetSessionAsync(sessionKey),
                "compact" => await client.CompactSessionAsync(sessionKey, 400),
                "delete" => await client.DeleteSessionAsync(sessionKey, deleteTranscript: true),
                "thinking" => await client.PatchSessionAsync(sessionKey, thinkingLevel: value),
                "verbose" => await client.PatchSessionAsync(sessionKey, verboseLevel: value),
                _ => false
            };

            if (!sent)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailedDetail")));
                return;
            }

            if (action is "thinking" or "verbose")
            {
                _ = client.RequestSessionsAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session action error ({action}): {ex.Message}");
            try
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_SessionActionFailed"))
                    .AddText(ex.Message));
            }
            catch (Exception toastEx)
            {
                // Toast surface failed while reporting an outer error — outer error already logged above.
                Logger.Debug($"App: Session action failure toast suppressed: {toastEx.Message}");
            }
        }
    }

    private async Task<bool> ConfirmSessionActionAsync(string title, string body, string actionLabel)
    {
        var xamlRoot = _windowManager?.RuntimeAnchorXamlRoot;
        if (xamlRoot == null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = actionLabel,
            CloseButtonText = LocalizationHelper.GetString("SessionActionPrompt_CancelLabel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private void AddRecentActivity(
        string line,
        string category = "general",
        string? icon = null,
        string? dashboardPath = null,
        string? details = null,
        string? sessionKey = null,
        string? nodeId = null)
    {
        ActivityStreamService.Add(
            category: category,
            title: line,
            icon: icon,
            details: details,
            dashboardPath: dashboardPath,
            sessionKey: sessionKey,
            nodeId: nodeId);
    }

    private List<string> GetRecentActivity(int maxItems)
    {
        return ActivityStreamService.GetItems(Math.Max(0, maxItems))
            .Select(item => $"{item.Timestamp:HH:mm:ss} {item.Title}")
            .ToList();
    }

    private void LocalDisconnectCleanup()
    {
        _appState?.ClearCachedData();
        UpdateTrayIcon();
        // Dismiss the tray menu on disconnect — it will capture fresh data on next open
        _trayController?.HideMenu();
    }

    private SettingsWriteOrigin GetOrCreateSettingsWriteOrigin(
        ref SettingsWriteOrigin? originField,
        ISettingsStore store)
        => originField ??= store.CreateOrigin();

    private bool TryPersistPermissionSetting(
        ref SettingsWriteOrigin? originField,
        string writerName,
        Action<ISettingsEditor> edit,
        Action<SettingsManager> fallbackEdit,
        out string? error)
    {
        try
        {
            if (SettingsStore is { } store)
            {
                store.Update(GetOrCreateSettingsWriteOrigin(ref originField, store), edit);
                error = null;
                return true;
            }

            if (_settings == null)
            {
                error = "Settings are not initialized";
                Logger.Warn($"[App] {writerName} could not persist a permission setting because {error.ToLowerInvariant()}.");
                return false;
            }

            Logger.Warn($"[App] {writerName} could not reach ISettingsStore. Falling back to SettingsManager.Save.");
            fallbackEdit(_settings);
            _settings.Save();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Warn($"[App] {writerName} failed to persist a permission setting: {ex.Message}");
            return false;
        }
    }

    private void ToggleTrayPermission(string action)
    {
        if (_settings is null)
            return;

        switch (action)
        {
            case "perm-toggle|Windows node":
                PersistTrayPermission(
                    nameof(SettingsManager.EnableNodeMode),
                    !_settings.EnableNodeMode,
                    (edit, value) => edit.EnableNodeMode = value,
                    (settings, value) => settings.EnableNodeMode = value);
                break;
            case "perm-toggle|System tools":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeSystemRunEnabled),
                    !_settings.NodeSystemRunEnabled,
                    (edit, value) => edit.NodeSystemRunEnabled = value,
                    (settings, value) => settings.NodeSystemRunEnabled = value);
                break;
            case "perm-toggle|Browser control":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeBrowserProxyEnabled),
                    !_settings.NodeBrowserProxyEnabled,
                    (edit, value) => edit.NodeBrowserProxyEnabled = value,
                    (settings, value) => settings.NodeBrowserProxyEnabled = value);
                break;
            case "perm-toggle|Camera":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeCameraEnabled),
                    !_settings.NodeCameraEnabled,
                    (edit, value) => edit.NodeCameraEnabled = value,
                    (settings, value) => settings.NodeCameraEnabled = value);
                break;
            case "perm-toggle|Canvas":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeCanvasEnabled),
                    !_settings.NodeCanvasEnabled,
                    (edit, value) => edit.NodeCanvasEnabled = value,
                    (settings, value) => settings.NodeCanvasEnabled = value);
                break;
            case "perm-toggle|Screen capture":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeScreenEnabled),
                    !_settings.NodeScreenEnabled,
                    (edit, value) => edit.NodeScreenEnabled = value,
                    (settings, value) => settings.NodeScreenEnabled = value);
                break;
            case "perm-toggle|Location":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeLocationEnabled),
                    !_settings.NodeLocationEnabled,
                    (edit, value) => edit.NodeLocationEnabled = value,
                    (settings, value) => settings.NodeLocationEnabled = value);
                break;
            case "perm-toggle|Voice (TTS)":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeTtsEnabled),
                    !_settings.NodeTtsEnabled,
                    (edit, value) => edit.NodeTtsEnabled = value,
                    (settings, value) => settings.NodeTtsEnabled = value);
                break;
            case "perm-toggle|Speech-to-text (STT)":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeSttEnabled),
                    !_settings.NodeSttEnabled,
                    (edit, value) => edit.NodeSttEnabled = value,
                    (settings, value) => settings.NodeSttEnabled = value);
                break;
            case "perm-toggle|Ollama":
                PersistTrayPermission(
                    nameof(SettingsManager.NodeOllamaInferenceEnabled),
                    !_settings.NodeOllamaInferenceEnabled,
                    (edit, value) => edit.NodeOllamaInferenceEnabled = value,
                    (settings, value) => settings.NodeOllamaInferenceEnabled = value);
                break;
        }
    }

    private void PersistTrayPermission(
        string settingName,
        bool value,
        Action<ISettingsEditor, bool> edit,
        Action<SettingsManager, bool> fallbackEdit)
    {
        if (TryPersistPermissionSetting(
            ref _trayPermissionWriteOrigin,
            $"tray permissions flyout ({settingName})",
            settings => edit(settings, value),
            settings => fallbackEdit(settings, value),
            out _))
        {
            _nodeService?.ApplyOllamaPermission(
                _settings?.NodeOllamaInferenceEnabled == true);
            ReconnectWithSyncedBrowserProxyForward();
        }
    }

    private TrayMenuSnapshot CaptureTrayMenuSnapshot()
    {
        ApplyTrayMenuPreviewDataIfRequested();

        // Show "Reconfigure" if there's an existing setup, "Setup Guide" if fresh
        var hasExistingConfig = false;
        if (_settings != null)
        {
            try
            {
                hasExistingConfig = !StartupSetupState.RequiresSetup(
                    _settings,
                    IdentityDataPath,
                    _gatewayRegistry);
            }
            catch (DeviceIdentityLoadException ex)
            {
                Logger.Error($"Stored device identity load failed while opening the tray menu: {ex.InnerException?.Message}");
                ShowTransientConnectionError(ex.Message);
                hasExistingConfig = true;
            }
        }

        var hasSetupManagedLocalWslGateway = WslKeepAlivePolicy.HasSetupManagedLocalGateway(_gatewayRegistry?.GetAll());
        var setupMenuLabel = hasExistingConfig
            ? LocalizationHelper.GetString("Menu_Reconfigure")
            : LocalizationHelper.GetString("Menu_SetupGuide");

        return new TrayMenuSnapshot
        {
            CurrentStatus = _appState!.Status,
            OverallState = _connectionManager?.CurrentSnapshot.OverallState,
            AuthFailureMessage = _appState?.AuthFailureMessage,
            GatewayUrl = _gatewayRegistry?.GetActive()?.Url ?? _settings?.GetEffectiveGatewayUrl(),
            GatewaySelf = TrayGatewaySelfSnapshot.From(_appState?.GatewaySelf),
            Presence =
            [
                .. (_appState?.Presence ?? Array.Empty<PresenceEntry>())
                    .Select(TrayPresenceSnapshot.From),
            ],
            EnableNodeMode = _settings?.EnableNodeMode == true && _nodeService != null,
            NodeIsPaired = _nodeService?.IsPaired ?? false,
            NodeIsPendingApproval = _nodeService?.IsPendingApproval ?? false,
            NodeIsConnected = _nodeService?.IsConnected ?? false,
            NodePendingPairCount = _appState?.NodePairList?.Pending.Count ?? 0,
            DevicePendingPairCount = _appState?.DevicePairList?.Pending.Count ?? 0,
            Nodes =
            [
                .. (_appState?.Nodes ?? Array.Empty<GatewayNodeInfo>())
                    .Select(TrayNodeSnapshot.From),
            ],
            Sessions =
            [
                .. (_appState?.Sessions ?? Array.Empty<SessionInfo>())
                    .Select(TraySessionSnapshot.From),
            ],
            Usage = TrayUsageSnapshot.From(_appState?.Usage),
            UsageStatus = TrayUsageStatusSnapshot.From(_appState?.UsageStatus),
            UsageCost = TrayUsageCostSnapshot.From(_appState?.UsageCost),
            Settings = _settings is null
                ? null
                : new TrayMenuSettingsSnapshot(
                    _settings.EnableNodeMode,
                    _settings.EnableMcpServer,
                    _settings.NodeSystemRunEnabled,
                    _settings.NodeBrowserProxyEnabled,
                    _settings.NodeCameraEnabled,
                    _settings.NodeCanvasEnabled,
                    _settings.NodeScreenEnabled,
                    _settings.NodeLocationEnabled,
                    _settings.NodeTtsEnabled,
                    _settings.NodeSttEnabled,
                    _settings.NodeOllamaInferenceEnabled),
            SetupMenuLabel = setupMenuLabel,
            ShowSetupMenuEntry = !hasSetupManagedLocalWslGateway,
            LastUpdated = _appState?.LastCheckTime,
            IsMcpRunning = _nodeService?.IsMcpRunning == true,
            McpStartupError = _nodeService?.McpStartupError,
        };
    }


    /// <summary>
    /// Opt-in design preview: when the <c>OPENCLAW_TRAY_PREVIEW_DATA</c>
    /// environment variable is set to <c>1</c>, populate the session/usage
    /// caches with synthetic values so the Sessions and Usage flyouts render
    /// meaningful progress bars and provider data without a live gateway.
    /// Real data takes precedence — preview values are only written when the
    /// corresponding cache is empty/null, so attaching to a real gateway
    /// after launch immediately replaces the preview.
    /// </summary>
    private void ApplyTrayMenuPreviewDataIfRequested()
    {
        var flag = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_PREVIEW_DATA");
        if (string.IsNullOrEmpty(flag) || flag == "0") return;

        {
            var now = DateTime.UtcNow;
            if (_appState != null)
            {
                _appState.Sessions = new[]
                {
                    new SessionInfo
                    {
                        Key = "preview:main", IsMain = true, Status = "active",
                        Model = "claude-opus-4.7", DisplayName = "Main · preview",
                        InputTokens = 124_000, OutputTokens = 36_000,
                        ContextTokens = 200_000,
                        UpdatedAt = now.AddMinutes(-2), LastSeen = now,
                    },
                    new SessionInfo
                    {
                        Key = "preview:dashboard", IsMain = false, Status = "idle",
                        Model = "gpt-5.4", DisplayName = "agent:main:dashboard",
                        InputTokens = 58_000, OutputTokens = 12_000,
                        ContextTokens = 128_000,
                        UpdatedAt = now.AddHours(-1), LastSeen = now,
                    },
                    new SessionInfo
                    {
                        Key = "preview:scratch", IsMain = false, Status = "idle",
                        Model = "claude-haiku-4.5", DisplayName = "agent:main:scratch",
                        InputTokens = 6_400, OutputTokens = 1_200,
                        ContextTokens = 64_000,
                        UpdatedAt = now.AddHours(-4), LastSeen = now,
                    },
                };

                _appState.Usage = new GatewayUsageInfo
                {
                    InputTokens = 188_400,
                    OutputTokens = 49_200,
                    TotalTokens = 237_600,
                    CostUsd = 4.82,
                    RequestCount = 142,
                    Model = "claude-opus-4.7",
                };

                _appState.UsageStatus = new GatewayUsageStatusInfo
                {
                    UpdatedAt = DateTime.UtcNow,
                    Providers = new()
                    {
                        new GatewayUsageProviderInfo
                        {
                            Provider = "anthropic", DisplayName = "Anthropic",
                            Plan = "Pro",
                            Windows = new()
                            {
                                new() { Label = "5h window", UsedPercent = 64 },
                                new() { Label = "Weekly",    UsedPercent = 28 },
                                new() { Label = "Monthly",   UsedPercent = 0 },
                            },
                        },
                        new GatewayUsageProviderInfo
                        {
                            Provider = "openai", DisplayName = "OpenAI",
                            Plan = "Tier 4",
                            Windows = new()
                            {
                                new() { Label = "RPM",    UsedPercent = 41 },
                                new() { Label = "TPM",    UsedPercent = 73 },
                                new() { Label = "Daily",  UsedPercent = 96 },
                            },
                        },
                    },
                };
            }
        }
    }


    #region Gateway Client

    private void InitializeGatewayClient(bool useBootstrapHandoffAuth = false)
    {
        if (_settings == null || _connectionManager == null || _gatewayRegistry == null) return;
        // SSH tunnel lifecycle is now handled by the connection manager.

        var gatewayUrl = _settings.GetEffectiveGatewayUrl();

        // Check registry first — it's the source of truth after initial setup
        var activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            if (!TryConnectGatewayIfCredentialAvailable(activeRecord, "startup"))
            {
                // Still start MCP-only node if enabled — the active record may be stale
                // and MCP-only mode must work without gateway credentials.
                TryStartLocalMcpOnlyNode();
            }
            return;
        }

        TryMigrateLegacyGatewaySettings(gatewayUrl, new AppLogger());
        activeRecord = _gatewayRegistry.GetActive();
        if (activeRecord != null)
        {
            if (!TryConnectGatewayIfCredentialAvailable(activeRecord, "legacy migration"))
                TryStartLocalMcpOnlyNode();
            return;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            if (TryStartLocalMcpOnlyNode())
                return;

            Logger.Info("Gateway URL not configured — skipping client initialization");
            return;
        }

        // Bridge: create/update a GatewayRecord from current settings URL.
        // Credentials come from GatewayRegistry and DeviceIdentity, not settings.
        var existing = _gatewayRegistry.FindByUrl(gatewayUrl);
        if (existing != null)
        {
            // Record already exists — just ensure it's active and connect
            _gatewayRegistry.SetActive(existing.Id);
        }
        else
        {
            // No record yet — create one from settings URL if we have a stored device token.
            bool hasStoredDeviceToken;
            try
            {
                hasStoredDeviceToken = DeviceIdentity.HasStoredDeviceToken(
                    Path.Combine(SettingsManager.SettingsDirectoryPath));
            }
            catch (DeviceIdentityLoadException ex)
            {
                Logger.Error($"Stored device identity load failed during startup: {ex.InnerException?.Message}");
                ShowTransientConnectionError(ex.Message);
                TryStartLocalMcpOnlyNode();
                return;
            }

            if (!hasStoredDeviceToken)
            {
                if (TryStartLocalMcpOnlyNode())
                    return;

                Logger.Info("No stored device token — skipping startup connect (use Setup Code)");
                return;
            }

            var recordId = Guid.NewGuid().ToString();
            var record = new GatewayRecord
            {
                Id = recordId,
                Url = gatewayUrl,
                IsLocal = LocalGatewayUrlClassifier.IsLocalGatewayUrl(gatewayUrl),
                SshTunnel = _settings.UseSshTunnel
                    ? BrowserProxySshTunnelForwardPolicy.Apply(
                        _settings,
                        new SshTunnelConfig(
                        _settings.SshTunnelUser ?? "",
                        _settings.SshTunnelHost ?? "",
                        _settings.SshTunnelRemotePort,
                        _settings.SshTunnelLocalPort,
                        SshPort: _settings.SshTunnelSshPort))
                    : null,
            };
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
        }

        var migratedRecord = SyncGatewayBrowserProxyForward(_gatewayRegistry.GetActive()!);

        // Ensure identity directory exists for credential resolution
        var identityDir = _gatewayRegistry.GetIdentityDirectory(migratedRecord.Id);
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);

        // Copy identity file from legacy location if needed.
        // device-key-ed25519.json holds BOTH the operator DeviceToken and the
        // node NodeDeviceToken on a single record (DeviceIdentity.DeviceKeyData),
        // so this single copy migrates both roles' identity for paired-pre-
        // unification installs (the easy-button setup engine used to write the
        // node-side tokens to this same legacy path via NodeService.ConnectAsync).
        // The legacy file is preserved (copy, not move) for at least one release
        // to allow safe rollback.
        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        var newIdentityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (File.Exists(legacyIdentityPath) && !File.Exists(newIdentityPath))
        {
            try { File.Copy(legacyIdentityPath, newIdentityPath, overwrite: false); }
            catch (Exception ex) { Logger.Warn($"Failed to copy identity file: {ex.Message}"); }
        }

        // Delegate to connection manager — it creates the client, fires OperatorClientChanged,
        // and our handler re-wires the 27 event subscriptions
        if (!TryConnectGatewayIfCredentialAvailable(migratedRecord, "startup bridge"))
            TryStartLocalMcpOnlyNode();
    }

    /// <summary>
    /// Connects only when the active gateway has a usable operator credential:
    /// device token, shared gateway token, or bootstrap token.
    /// </summary>
    private bool TryConnectGatewayIfCredentialAvailable(GatewayRecord record, string context)
    {
        if (_connectionManager == null || _gatewayRegistry == null)
            return false;

        record = SyncGatewayBrowserProxyForward(record);
        var resolver = new CredentialResolver(DeviceIdentityFileReader.Instance);
        var identityDir = _gatewayRegistry.GetIdentityDirectory(record.Id);
        OpenClaw.Connection.GatewayCredential? credential;
        try
        {
            credential = ResolveStartupOperatorCredential(record, resolver, identityDir);
        }
        catch (DeviceIdentityLoadException ex)
        {
            Logger.Error($"Stored device identity load failed during {context}: {ex.InnerException?.Message}");
            ShowTransientConnectionError(ex.Message);
            return false;
        }

        if (credential == null)
        {
            OpenClaw.Connection.GatewayCredential? nodeCredential;
            try
            {
                nodeCredential = ResolveStartupNodeCredential(record, resolver, identityDir);
            }
            catch (DeviceIdentityLoadException ex)
            {
                Logger.Error($"Stored node identity load failed during {context}: {ex.InnerException?.Message}");
                ShowTransientConnectionError(ex.Message);
                return false;
            }

            if (nodeCredential != null && IsGatewayNodeEnabled())
            {
                Logger.Info(
                    $"Connecting node-only gateway during {context}: {record.Url} ({nodeCredential.Source})");
                ObserveBackgroundFault(
                    _connectionManager.ConnectNodeOnlyAsync(record.Id),
                    $"[App] Startup node-only gateway connect failed during {context}");
                return true;
            }

            Logger.Info($"Active gateway has no usable credential — skipping {context} connect");
            return false;
        }

        var connectionKind = record.LastConnected.HasValue
            ? "last successful gateway"
            : "credentialed gateway";
        Logger.Info($"Connecting to {connectionKind} during {context}: {record.Url} ({credential.Source})");
        ObserveBackgroundFault(
            _connectionManager.ConnectAsync(record.Id),
            $"[App] Startup gateway connect failed during {context}");
        if (!IsGatewayNodeEnabled())
            TryStartLocalMcpOnlyNode();
        return true;
    }

    private void ReconnectWithSyncedBrowserProxyForward()
    {
        SyncActiveGatewayBrowserProxyForward();
        _nodeService?.RefreshMcpOnlyCapabilities();
        _ = _connectionManager?.ReconnectAsync();
    }

    private void SyncActiveGatewayBrowserProxyForward()
    {
        if (_gatewayRegistry?.GetActive() is { } active)
            SyncGatewayBrowserProxyForward(active);
    }

    private GatewayRecord SyncGatewayBrowserProxyForward(GatewayRecord record)
    {
        if (_settings == null || _gatewayRegistry == null || record.SshTunnel == null)
            return record;

        var effectiveTunnel = BrowserProxySshTunnelForwardPolicy.Apply(_settings, record.SshTunnel);
        if (Equals(effectiveTunnel, record.SshTunnel))
            return record;

        var updated = record with { SshTunnel = effectiveTunnel };
        _gatewayRegistry.AddOrUpdate(updated);
        _gatewayRegistry.Save();
        Logger.Info($"[SETTINGS] Updated active gateway SSH browser-proxy forward flag to {effectiveTunnel.IncludeBrowserProxyForward}");
        return updated;
    }

    private static void ObserveBackgroundFault(Task task, string message)
    {
        if (task.IsFaulted)
        {
            Logger.Error($"{message}: {task.Exception.GetBaseException().Message}");
            return;
        }

        if (task.IsCanceled)
        {
            Logger.Warn($"{message}: canceled");
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                t => Logger.Error($"{message}: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ApplyOpenTelemetryEndpointSettings()
    {
        var connection = _openTelemetryConnection;
        var settings = _settings;
        if (connection == null || settings == null)
            return;

        var options = OpenTelemetryEndpointOptions.FromSettings(settings);
        ObserveBackgroundFault(
            connection.ApplyAsync(options),
            "[App] Failed to apply OpenTelemetry endpoint settings");
    }

    private async Task<bool> ResendOpenTelemetryProbeAsync()
    {
        var connection = _openTelemetryConnection;
        var settings = _settings;
        if (connection == null || settings == null)
            return false;

        var options = OpenTelemetryEndpointOptions.FromSettings(settings);
        await connection.ProbeAsync(options);
        return connection.State == OpenTelemetryEndpointConnectionState.ProbeFlushed &&
            connection.CurrentOptions == options;
    }

    private OpenClaw.Connection.GatewayCredential? ResolveStartupOperatorCredential(
        GatewayRecord record,
        CredentialResolver resolver,
        string identityDir)
    {
        if (_gatewayRegistry == null)
            return null;

        var resolution = resolver.ResolveOperatorDetailed(record, identityDir);
        var credential = ResolveStartupCredentialOrThrow(resolution, identityDir);
        if (credential != null)
            return credential;

        // Backfill for legacy installs that still have the identity file at the
        // root settings path while the active registry record points at that URL.
        var effectiveUrl = _settings?.GetEffectiveGatewayUrl();
        if (!string.IsNullOrWhiteSpace(effectiveUrl) &&
            string.Equals(record.Url, effectiveUrl, StringComparison.OrdinalIgnoreCase))
        {
            resolution = resolver.ResolveOperatorDetailed(record, SettingsManager.SettingsDirectoryPath);
            return ResolveStartupCredentialOrThrow(resolution, SettingsManager.SettingsDirectoryPath);
        }

        return null;
    }

    private OpenClaw.Connection.GatewayCredential? ResolveStartupNodeCredential(
        GatewayRecord record,
        CredentialResolver resolver,
        string identityDir)
    {
        var resolution = resolver.ResolveNodeDetailed(record, identityDir);
        var credential = ResolveStartupCredentialOrThrow(resolution, identityDir);
        if (credential != null)
            return credential;

        var effectiveUrl = _settings?.GetEffectiveGatewayUrl();
        if (string.IsNullOrWhiteSpace(effectiveUrl) ||
            !string.Equals(record.Url, effectiveUrl, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        resolution = resolver.ResolveNodeDetailed(record, SettingsManager.SettingsDirectoryPath);
        credential = ResolveStartupCredentialOrThrow(resolution, SettingsManager.SettingsDirectoryPath);
        if (credential == null)
            return null;

        TryCopyLegacyIdentityToGateway(record.Id, identityDir);
        return credential;
    }

    private static OpenClaw.Connection.GatewayCredential? ResolveStartupCredentialOrThrow(
        GatewayCredentialResolution resolution,
        string identityDir)
    {
        var failureStatus = resolution.PrimaryStatus ?? resolution.Status;
        if (failureStatus is not (
            GatewayCredentialResolutionStatus.Unreadable
            or GatewayCredentialResolutionStatus.Corrupt))
        {
            return resolution.Credential;
        }

        Exception cause = failureStatus == GatewayCredentialResolutionStatus.Unreadable
            ? new IOException(resolution.Detail ?? "Identity file could not be read.")
            : new InvalidDataException(resolution.Detail ?? "Identity file is invalid.");
        throw new DeviceIdentityLoadException(
            Path.Combine(identityDir, "device-key-ed25519.json"),
            cause);
    }

    private static void TryCopyLegacyIdentityToGateway(string gatewayId, string identityDir)
    {
        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        var newIdentityPath = Path.Combine(identityDir, "device-key-ed25519.json");
        if (!File.Exists(legacyIdentityPath) || File.Exists(newIdentityPath))
            return;

        try
        {
            if (!Directory.Exists(identityDir))
                Directory.CreateDirectory(identityDir);
            File.Copy(legacyIdentityPath, newIdentityPath, overwrite: false);
            Logger.Info($"[GatewayRegistry] Copied legacy identity into active gateway {gatewayId}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to copy legacy identity file for gateway {gatewayId}: {ex.Message}");
        }
    }

    private void TryMigrateLegacyGatewaySettings(string gatewayUrl, IOpenClawLogger logger)
    {
        if (_settings == null || _gatewayRegistry == null || string.IsNullOrWhiteSpace(gatewayUrl))
        {
            return;
        }

        var legacyIdentityPath = Path.Combine(SettingsManager.SettingsDirectoryPath, "device-key-ed25519.json");
        if (!_settings.HasLegacyGatewayCredentials && !File.Exists(legacyIdentityPath))
        {
            return;
        }

        var migrated = _gatewayRegistry.MigrateFromSettings(
            gatewayUrl,
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken,
            _settings.UseSshTunnel,
            _settings.SshTunnelUser,
            _settings.SshTunnelHost,
            _settings.SshTunnelSshPort,
            _settings.SshTunnelRemotePort,
            _settings.SshTunnelLocalPort,
            includeBrowserProxyForward: BrowserProxySshTunnelForwardPolicy.ShouldInclude(
                _settings.NodeBrowserProxyEnabled,
                _settings.SshTunnelRemotePort,
                _settings.SshTunnelLocalPort),
            SettingsManager.SettingsDirectoryPath,
            logger);

        if (migrated)
        {
            Logger.Info("[GatewayRegistry] Migrated legacy gateway settings into registry");
        }
    }

    private bool TryStartLocalMcpOnlyNode()
    {
        if (_settings == null || !_settings.EnableMcpServer || _settings.EnableNodeMode)
        {
            return false;
        }

        var nodeService = EnsureNodeService(_settings);
        if (nodeService == null)
        {
            Logger.Warn("MCP-only mode requested but node service could not be initialized");
            return false;
        }

        try
        {
            nodeService.StartLocalOnlyAsync().GetAwaiter().GetResult();
            var notificationPlan = McpRuntimeStatePolicy.PlanStartupNotification(
                _settings.EnableMcpServer,
                nodeService.IsMcpRunning,
                nodeService.McpStartupError);
            if (notificationPlan.ShouldShow)
            {
                Logger.Error($"Failed to start MCP-only node service: {notificationPlan.Message}");
                ApplyMcpStartupNotificationPlan(notificationPlan);
                return false;
            }

            WireAppCapabilityHandlers();
            ApplyMcpStartupNotificationPlan(notificationPlan);
            Logger.Info("Started MCP-only node service without gateway connection");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start MCP-only node service: {ex}");
            nodeService.SetMcpStartupError($"MCP server startup failed: {ex.Message}");
            ApplyMcpStartupNotificationPlan(
                McpRuntimeStatePolicy.PlanStartupNotification(
                    _settings.EnableMcpServer,
                    nodeService.IsMcpRunning,
                    nodeService.McpStartupError));
            return false;
        }
    }

    /// <summary>
    /// Handles the connection manager's OperatorClientChanged event.
    /// Re-wires all 27 data event handlers from the old client to the new one.
    /// </summary>
    private void OnOperatorClientChanged(object? sender, OperatorClientChangedEventArgs e)
    {
        if (_dispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            if (!dispatcher.TryEnqueue(() => OnOperatorClientChanged(sender, e)))
            {
                Logger.Warn("[ConnMgr] Failed to dispatch operator client swap to UI thread");
            }
            return;
        }

        // Delegate all 27 event subscriptions to GatewayService
        _gatewayService?.AttachClient(e.NewClient, e.OldClient);

        // Configure new client
        if (e.NewClient is { } client)
        {
            client.SetUserRules(_settings?.UserRules?.Count > 0 ? _settings.UserRules : null);
            client.SetPreferStructuredCategories(_settings?.PreferStructuredCategories ?? true);

            var concreteClient = client as OpenClawGatewayClient;
            if (concreteClient == null)
                Logger.Warn("[ConnMgr] NewClient is not OpenClawGatewayClient — chat coordinator disabled");
            _chatCoordinator?.SetOperatorClient(concreteClient);
        }
        else
        {
            _chatCoordinator?.SetOperatorClient(null);
            _pairingApprovalCoordinator?.Reset();
        }

        RaiseChatProviderChanged();

        // Update UI references
        if (_appState != null)
            _appState.GatewaySelf = null;
    }

    private void RaiseChatProviderChanged()
    {
        ChatProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles the connection manager's StateChanged event.
    /// Maps the snapshot to the existing tray icon / UI status system.
    /// Authoritative writer of gateway lifecycle status.
    /// </summary>
    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snap)
    {
        _openTelemetryConnection?.SendConnectionState(snap);
        var mapped = ConnectionStatusPresenter.ToLegacyStatus(snap);
        var connectedSideEffectsKey = snap.OperatorState == RoleConnectionState.Connected
            ? $"{snap.GatewayId ?? snap.GatewayUrl ?? "unknown"}|{snap.OperatorDeviceId ?? "unknown"}"
            : null;
        OnUiThread(() =>
        {
            if (_appState != null) _appState.Status = mapped;
            _windowManager?.UpdateHubTitleBarStatus(snap, mapped);
            _trayController?.ApplyConnectionState(mapped, snap.OverallState);
            UpdateConnectionIssueNotification(snap);
            PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);
        });

        if (connectedSideEffectsKey != null)
        {
            if (!string.Equals(_lastManagerConnectedSideEffectsKey, connectedSideEffectsKey, StringComparison.Ordinal))
            {
                _lastManagerConnectedSideEffectsKey = connectedSideEffectsKey;
                _ = RunHealthCheckAsync();
                _ = TryConnectLocalNodeServiceAsync();
            }
        }
        else
        {
            _lastManagerConnectedSideEffectsKey = null;
        }
    }

    private NodeService? EnsureNodeService(SettingsManager settings)
    {
        if (_nodeService != null)
            return _nodeService;

        if (_dispatcherQueue == null)
            return null;

        if (_gatewayService == null)
        {
            Logger.Error("GatewayService must be initialized before NodeService event wiring");
            return null;
        }

        try
        {
            _nodeService = new NodeService(
                new AppLogger(),
                _dispatcherQueue,
                DataPath,
                settings: settings,
                enableMcpServer: settings.EnableMcpServer,
                identityDataPath: IdentityDataPath,
                sharedGatewayTokenResolver: () => _gatewayRegistry?.GetActive()?.SharedGatewayToken,
                browserControlPortResolver: () => _gatewayRegistry?.GetActive()?.BrowserControlPort,
                activeGatewayTunnelResolver: () => _gatewayRegistry?.GetActive()?.SshTunnel,
                activeGatewayUrlResolver: () => _gatewayRegistry?.GetActive()?.Url,
                browserControlAuthorization: async (uri, cancellationToken) =>
                {
                    var record = _gatewayRegistry?.GetActive();
                    if (record is null || !uri.IsLoopback)
                        return false;
                    if (record.SshTunnel is not null)
                    {
                        var browserForwardPort = record.SshTunnel.LocalPort + 2;
                        if (!record.SshTunnel.IncludeBrowserProxyForward ||
                            uri.Port != browserForwardPort)
                        {
                            return false;
                        }

                        return _sshTunnelService is not null &&
                            await _sshTunnelService
                                .IsOwnedListenerReadyAsync(
                                    record.SshTunnel,
                                    uri.Port,
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }
                    if (_managedLocalPortProvenance is null ||
                        GatewayRecordEditing.ResolveManagedDistroName(record) is null)
                    {
                        return false;
                    }

                    var controlRecord = record with
                    {
                        Url = $"ws://localhost:{uri.Port}",
                        IsLocal = true,
                    };
                    return (await _managedLocalPortProvenance.InspectAsync(
                        controlRecord,
                        cancellationToken)).Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway;
                },
                execApprovalsStore: ExecApprovalsStore);
            _nodeService.StatusChanged += OnNodeStatusChanged;
            _nodeService.NotificationRequested += OnNodeNotificationRequested;
            _nodeService.ToastRequested += OnNodeToastRequested;
            _nodeService.PairingStatusChanged += OnPairingStatusChanged;
            _nodeService.ChannelHealthUpdated += _gatewayService.OnChannelHealthUpdated;
            _nodeService.InvokeCompleted += OnNodeInvokeCompleted;
            _nodeService.ToolTelemetryCompleted += OnNodeToolTelemetryCompleted;
            _nodeService.GatewaySelfUpdated += _gatewayService.OnGatewaySelfUpdated;
            return _nodeService;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize node service for local gateway setup: {ex}");
            _nodeService = null;
            return null;
        }
    }

    private bool RequiresSetup(SettingsManager settings)
    {
        return StartupSetupState.RequiresSetup(settings, IdentityDataPath, _gatewayRegistry);
    }

    private bool ShouldInitializeNodeService()
    {
        return _settings?.EnableNodeMode == true || _settings?.EnableMcpServer == true;
    }

    /// <summary>True when this PC should connect as a gateway node.</summary>
    private bool IsGatewayNodeEnabled()
    {
        return _settings?.EnableNodeMode == true;
    }

    // The pre-unification ShouldInitializeNodeService(GatewayRecord, string) overload
    // and LocalNodeServiceOwnsIdentityFor have been removed: GatewayConnectionManager
    // is now the single owner of the WindowsNodeClient lifecycle for ALL gateways
    // (local + remote). NodeService remains as the capability registrar via the
    // NodeConnector.ClientCreated → AttachClient bridge wired in InitializeApp.

    private void OnNodeStatusChanged(object? sender, ConnectionStatus status)
    {
        Logger.Info($"Node status: {status}");
        AddRecentActivity($"Node mode {status}", category: "node", dashboardPath: "nodes");
        
        // In node-only mode, surface node connection in main status indicator
        if (_settings?.EnableNodeMode == true)
        {
            // Status field is maintained by OnManagerStateChanged — no write needed here.
            UpdateTrayIcon();
            OnUiThread(() =>
            {
                UpdateStatusDetailWindow();
                PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        
        // Don't show "connected" toast if waiting for pairing - we'll show pairing status instead
        var nodeService = _nodeService;
        if (status == ConnectionStatus.Connected && nodeService?.IsPaired == true)
        {
            RefreshGatewayNodes("node connected");
            var deviceId = nodeService.FullDeviceId;
            if (_toastService!.HasRecentToast("node-paired", deviceId))
            {
                Logger.Info($"[ToastDeduper] Suppressed node-connected toast after node-paired deviceId={deviceId}");
                return;
            }

            try
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActive"))
                    .AddText(LocalizationHelper.GetString("Toast_NodeModeActiveDetail")),
                    "node-connected",
                    deviceId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"App: Failed to show node-connected toast for device '{DeviceIdForLog(deviceId)}': {ex.Message}");
            }
        }
    }

    private void OnPairingStatusChanged(object? sender, OpenClaw.Shared.PairingStatusEventArgs args)
    {
        Logger.Info($"Pairing status: {args.Status}");

        // The local node's own device id may have just become known. Re-run the
        // approval reconcile so the own-node filter drops any self pairing request
        // rather than prompting the operator to approve their own machine.
        OnUiThread(() => _pairingApprovalCoordinator?.OnPairListsUpdated(
            _appState?.DevicePairList, _appState?.NodePairList));

        try
        {
            if (args.Status == OpenClaw.Shared.PairingStatus.Pending)
            {
                var approvalCommand = args.ApprovalKind switch
                {
                    OpenClaw.Shared.PairingApprovalKind.DevicePair => BuildPairingApprovalCommand(args.DeviceId),
                    OpenClaw.Shared.PairingApprovalKind.NodePair => CommandCenterDiagnostics.BuildNodeApprovalRepairCommand(args.RequestId),
                    _ => CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands()
                };
                ShowPairingPendingNotification(args.DeviceId, approvalCommand);
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Paired)
            {
                RefreshGatewayNodes("node paired");
                ClearPairingAppNotifications(args.DeviceId);
                // Bug 3: idempotency guard — only show "Node paired" toast/activity once
                // per device per session. WS reconnects re-fire Paired; suppress duplicates.
                var deviceKey = args.DeviceId ?? string.Empty;
                if (!_toastService!.HasShownPairedToast(deviceKey))
                {
                    _toastService!.MarkPairedToastShown(deviceKey);
                    AddRecentActivity("Node paired", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId);
                    AppNotificationPublisher.Show(
                        _appNotificationService,
                        LocalizationHelper.GetString("Toast_NodePaired"),
                        LocalizationHelper.GetString("Toast_NodePairedDetail"),
                        "node",
                        "pairing",
                        AppNotificationSeverity.Success,
                        "node-paired:" + HashNotificationKey(deviceKey),
                        "connection",
                        LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
                        id: BuildPairingPairedNotificationId(deviceKey));
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_NodePaired"))
                        .AddText(LocalizationHelper.GetString("Toast_NodePairedDetail")),
                        "node-paired",
                        args.DeviceId);
                }
                else
                {
                    Logger.Info($"App: Suppressing duplicate Paired toast for device {DeviceIdForLog(deviceKey)}");
                }
            }
            else if (args.Status == OpenClaw.Shared.PairingStatus.Rejected)
            {
                _appNotificationService?.Dismiss(BuildPairingPendingNotificationId(args.DeviceId));
                ShowPairingRejectedAppNotification(args.DeviceId, args.Message);
                AddRecentActivity("Node pairing rejected", category: "node", dashboardPath: "nodes", nodeId: args.DeviceId, details: args.Message ?? LocalizationHelper.GetString("Toast_PairingRejectedDetail"));
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejected"))
                    .AddText(LocalizationHelper.GetString("Toast_PairingRejectedDetail")),
                    "node-pairing-rejected",
                    args.DeviceId);
            }
        }
        catch (ObjectDisposedException ex)
        {
            // Shutdown race: the toast infrastructure is gone. Routine, not a bug.
            Logger.Debug($"App: OnPairingStatusChanged handler skipped during shutdown (status={args.Status}): {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"App: Failed to handle pairing status '{args.Status}' for device '{DeviceIdForLog(args.DeviceId)}': {ex.Message}");
        }
    }

    private void RefreshGatewayNodes(string reason)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null || !client.IsConnectedToGateway)
            return;

        ObserveBackgroundFault(
            client.RequestNodesAsync(),
            $"[App] Node list refresh failed after {reason}");
    }

    /// <summary>
    /// Pushes current node service state to hub window so ConnectionPage reflects live pairing/identity.
    /// Now a no-op — pages read App properties directly via CurrentApp.
    /// </summary>

    public static string BuildPairingApprovalCommand(string deviceId) =>
        $"openclaw devices approve {deviceId}";

    private static string BuildPairingPendingNotificationId(string deviceId) =>
        $"node-pairing-pending:{deviceId.Trim().ToLowerInvariant()}";

    private static string BuildPairingPairedNotificationId(string deviceId) =>
        $"node-paired:{deviceId.Trim().ToLowerInvariant()}";

    private static string BuildPairingRejectedNotificationId(string deviceId) =>
        $"node-pairing-rejected:{deviceId.Trim().ToLowerInvariant()}";

    private void ClearPairingAppNotifications(string deviceId)
    {
        _appNotificationService?.Dismiss(BuildPairingPendingNotificationId(deviceId));
        _appNotificationService?.Dismiss(BuildPairingRejectedNotificationId(deviceId));
    }

    private static string DeviceIdForLog(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return "<none>";

        var sanitized = TokenSanitizer.Sanitize(deviceId.Trim());
        if (sanitized.Contains("[REDACTED", StringComparison.Ordinal))
            return sanitized;

        return sanitized.Length <= 8 ? sanitized : $"{sanitized[..8]}...";
    }

    public void ShowPairingPendingNotification(string deviceId, string? approvalCommand = null)
    {
        var command = approvalCommand ?? BuildPairingApprovalCommand(deviceId);
        var shortDeviceId = deviceId.Length > 16 ? deviceId[..16] : deviceId;

        AddRecentActivity("Node pairing pending", category: "node", dashboardPath: "nodes", nodeId: deviceId);
        AppNotificationPublisher.Show(
            _appNotificationService,
            LocalizationHelper.GetString("Toast_PairingPending"),
            string.Format(LocalizationHelper.GetString("Toast_PairingPendingDetail"), shortDeviceId),
            "node",
            "pairing",
            AppNotificationSeverity.Warning,
            $"node-pairing-pending:{deviceId}",
            "connection",
            LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
            id: BuildPairingPendingNotificationId(deviceId));
        _toastService!.ShowToast(new ToastContentBuilder()
            .AddText(LocalizationHelper.GetString("Toast_PairingPending"))
            .AddText(string.Format(LocalizationHelper.GetString("Toast_PairingPendingDetail"), shortDeviceId))
            .AddButton(new ToastButton()
                .SetContent(LocalizationHelper.GetString("Toast_CopyPairingCommand"))
                .AddArgument("action", "copy_pairing_command")
                .AddArgument("command", command)),
            "node-pairing-pending",
            deviceId);
    }

    private void ShowPairingRejectedAppNotification(string deviceId, string? detail)
    {
        AppNotificationPublisher.Show(
            _appNotificationService,
            LocalizationHelper.GetString("Toast_PairingRejected"),
            detail ?? LocalizationHelper.GetString("Toast_PairingRejectedDetail"),
            "node",
            "pairing",
            AppNotificationSeverity.Error,
            $"node-pairing-rejected:{deviceId}",
            "connection",
            LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
            id: BuildPairingRejectedNotificationId(deviceId));
    }

    /// <summary>
    /// Publishes an immediate connection-error banner using the single
    /// connection-issue notification identity. Used for transient, page-driven
    /// failures (e.g. a manual gateway switch that throws) where the snapshot
    /// may be briefly silent. Because it reuses the connection-issue id/dedupe
    /// key it occupies the same banner slot — it cannot produce a second bar —
    /// and the snapshot-driven path will replace or dismiss it on the next tick.
    /// </summary>
    internal void ShowTransientConnectionError(string message)
    {
        var body = string.IsNullOrWhiteSpace(message)
            ? LocalizationHelper.GetString("AppNotification_GatewayConnectionFailed_DefaultMessage")
            : message;

        // Keep the snapshot-driven publisher from immediately re-emitting a
        // duplicate for the same underlying error.
        _lastConnectionIssueNotificationKey = $"operator-error:{message}";

        AppNotificationPublisher.Show(
            _appNotificationService,
            LocalizationHelper.GetString("AppNotification_GatewayConnectionFailed_Title"),
            body,
            "connection",
            "lifecycle",
            AppNotificationSeverity.Error,
            ConnectionIssueNotificationDedupeKey,
            "connection",
            LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
            id: ConnectionIssueNotificationId);
    }

    private void UpdateConnectionIssueNotification(GatewayConnectionSnapshot snapshot)
    {
        if (!TryBuildConnectionIssueNotification(snapshot, out var title, out var message, out var severity, out var category, out var key))
        {
            _lastConnectionIssueNotificationKey = null;
            _appNotificationService?.Dismiss(ConnectionIssueNotificationId);
            return;
        }

        if (string.Equals(_lastConnectionIssueNotificationKey, key, StringComparison.Ordinal))
            return;

        _lastConnectionIssueNotificationKey = key;
        AppNotificationPublisher.Show(
            _appNotificationService,
            title,
            message,
            "connection",
            category,
            severity,
            ConnectionIssueNotificationDedupeKey,
            "connection",
            LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
            id: ConnectionIssueNotificationId);
    }

    private void ShowMcpStartupFailureNotification(string message)
    {
        AppNotificationPublisher.Show(
            _appNotificationService,
            "Local MCP failed",
            message,
            "connection",
            "mcp",
            AppNotificationSeverity.Error,
            McpStartupNotificationDedupeKey,
            "connection",
            LocalizationHelper.GetString("AppNotification_ActionOpenConnection"),
            id: McpStartupNotificationId);
    }

    private void ApplyMcpStartupNotificationPlan(McpStartupNotificationPlan plan)
    {
        if (plan.ShouldShow && !string.IsNullOrWhiteSpace(plan.Message))
        {
            ShowMcpStartupFailureNotification(plan.Message);
        }
        else if (plan.ShouldDismiss)
        {
            _appNotificationService?.Dismiss(McpStartupNotificationId);
        }

        UpdateTrayIcon();
    }

    private static bool TryBuildConnectionIssueNotification(
        GatewayConnectionSnapshot snapshot,
        out string title,
        out string message,
        out AppNotificationSeverity severity,
        out string category,
        out string key)
    {
        title = "";
        message = "";
        severity = AppNotificationSeverity.Warning;
        category = "lifecycle";
        key = "";

        if (snapshot.OperatorPairingRequired)
        {
            title = LocalizationHelper.GetString("AppNotification_GatewayPairingRequired_Title");
            message = string.IsNullOrWhiteSpace(snapshot.OperatorDeviceId)
                ? LocalizationHelper.GetString("AppNotification_GatewayPairingRequired_GenericMessage")
                : LocalizationHelper.Format(
                    "AppNotification_GatewayPairingRequired_DeviceMessageFormat",
                    DeviceIdForLog(snapshot.OperatorDeviceId));
            category = "pairing";
            key = $"operator-pairing:{snapshot.OperatorDeviceId ?? "unknown"}";
            return true;
        }

        if (snapshot.OverallState == OverallConnectionState.PairingRequired &&
            snapshot.NodeState == RoleConnectionState.PairingRequired)
        {
            title = LocalizationHelper.GetString("AppNotification_GatewayPairingRequired_Title");
            message = "Approve the Windows node pairing request on the gateway host.";
            category = "pairing";
            key = $"node-pairing:{snapshot.NodeDeviceId ?? snapshot.NodePairingRequestId ?? "unknown"}";
            return true;
        }

        if (TryBuildNodeConnectionIssueNotification(snapshot, out title, out message, out severity, out category, out key))
            return true;

        if (snapshot.OverallState == OverallConnectionState.Error)
        {
            title = LocalizationHelper.GetString("AppNotification_GatewayConnectionFailed_Title");
            var rawError = snapshot.OperatorError;
            message = string.IsNullOrWhiteSpace(rawError)
                ? LocalizationHelper.GetString("AppNotification_GatewayConnectionFailed_DefaultMessage")
                : rawError;
            severity = AppNotificationSeverity.Error;
            key = $"operator-error:{rawError ?? "default"}";
            return true;
        }

        return false;
    }

    private static bool TryBuildNodeConnectionIssueNotification(
        GatewayConnectionSnapshot snapshot,
        out string title,
        out string message,
        out AppNotificationSeverity severity,
        out string category,
        out string key)
    {
        title = "";
        message = "";
        severity = AppNotificationSeverity.Warning;
        category = "node";
        key = "";

        if (snapshot.OperatorState == RoleConnectionState.Error)
            return false;

        if (snapshot.NodeState == RoleConnectionState.RateLimited)
        {
            title = LocalizationHelper.GetString("AppNotification_WindowsNodeRateLimited_Title");
            message = snapshot.NodeError ?? LocalizationHelper.GetString("AppNotification_WindowsNodeRateLimited_DefaultMessage");
            key = $"node-rate-limited:{message}";
            return true;
        }

        if (snapshot.NodeState is RoleConnectionState.Error or RoleConnectionState.PairingRejected ||
            !string.IsNullOrWhiteSpace(snapshot.NodeError))
        {
            title = LocalizationHelper.GetString("AppNotification_WindowsNodeConnectionFailed_Title");
            message = snapshot.NodeError ?? LocalizationHelper.GetString("AppNotification_WindowsNodeConnectionFailed_DefaultMessage");
            severity = AppNotificationSeverity.Error;
            key = $"node-error:{message}";
            return true;
        }

        return false;
    }

    private void OnNodeNotificationRequested(object? sender, OpenClaw.Shared.Capabilities.SystemNotifyArgs args)
    {
        AddRecentActivity(args.Title, category: "node", dashboardPath: "nodes", details: args.Body);

        // Agent requested a notification via node.invoke system.notify
        try
        {
            AppNotification notification = AppNotificationMapper.FromNodeSystemNotification(args);
            AppNotificationPublisher.Publish(
                _appNotificationService,
                _toastService,
                new AppNotificationPublishRequest(
                    notification,
                    new ToastContentBuilder()
                        .AddText(notification.Title)
                        .AddText(notification.Message)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show node notification: {ex.Message}");
        }
    }

    private void OnNodeToastRequested(object? sender, NodeToastRequestedEventArgs args)
        => OnUiThread(() =>
            NonFatalAction.Run(
                () => AppNotificationPublisher.Publish(
                    _appNotificationService,
                    _toastService,
                    new AppNotificationPublishRequest(
                        args.AppNotification,
                        args.ToastBuilder,
                        args.ToastTag,
                        args.ToastDeviceId)),
                msg => Logger.Warn($"Failed to show node toast: {msg}")));

    private static string HashNotificationKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void OnNodeInvokeCompleted(object? sender, NodeInvokeCompletedEventArgs args)
    {
        var status = args.Ok ? "completed" : "failed";
        var durationMs = Math.Max(0, (int)Math.Round(args.Duration.TotalMilliseconds));
        var details = args.Ok
            ? $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms"
            : $"{GetNodeInvokePrivacyClass(args.Command)} · {durationMs} ms · {args.Error ?? "unknown error"}";

        AddRecentActivity(
            $"node.invoke {status}: {args.Command}",
            category: "node.invoke",
            dashboardPath: "nodes",
            details: details,
            nodeId: args.NodeId);

        OnUiThread(UpdateStatusDetailWindow);
    }

    private void OnNodeToolTelemetryCompleted(
        object? sender,
        NodeToolTelemetryCompletion completion)
    {
        _openTelemetryConnection?.SendNodeToolCompletion(completion);
    }

    private static string GetNodeInvokePrivacyClass(string command)
    {
        if (string.Equals(command, "screen.record", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "screen.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.snap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.clip", StringComparison.OrdinalIgnoreCase))
        {
            return "privacy-sensitive";
        }

        if (command.StartsWith("system.run", StringComparison.OrdinalIgnoreCase))
        {
            return "exec";
        }

        return "metadata";
    }

    // ── Re-raised event handlers from GatewayService ──────────────────

    private void OnGatewayConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status == ConnectionStatus.Connected && _appState != null)
        {
            _appState.AuthFailureMessage = null;
        }

        OnUiThread(() =>
        {
            UpdateStatusDetailWindow();
        });
    }

    /// <summary>
    /// Connects the local NodeService to the active gateway when the operator connection
    /// is established. This handles the restart case where the NodeConnector is suppressed
    /// for local gateways (LocalNodeServiceOwnsIdentityFor returns true) but the NodeService
    /// was never told to connect.
    /// </summary>
    private async Task TryConnectLocalNodeServiceAsync()
    {
        if (_connectionManager == null || !IsGatewayNodeEnabled())
            return;

        Logger.Info("[App] Auto-connecting local NodeService via EnsureNodeConnectedAsync");
        try
        {
            await _connectionManager.EnsureNodeConnectedAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"[App] Local NodeService auto-connect failed: {ex.Message}");
        }
    }

    private void OnGatewayAuthenticationFailed(object? sender, string message)
    {
        UpdateTrayIcon();

        // Store auth failure in AppState — observed for tray tooltip / status.
        if (_appState != null)
        {
            _appState.AuthFailureMessage = message;
        }

        // The user-facing banner is published by the single connection-issue
        // notification (UpdateConnectionIssueNotification), driven off the
        // snapshot's Error state + OperatorError (same string surfaced here).
        // Publishing a second "authentication failed" banner here produced a
        // duplicate top bar and forced the action button to degrade to
        // "Show more", so it is intentionally not raised from this handler.
    }

    private void OnGatewaySessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        OnUiThread(() =>
        {
            try
            {
                var title = result.Ok ? "✅ Session updated" : "❌ Session action failed";
                var key = string.IsNullOrWhiteSpace(result.Key) ? "session" : result.Key!;
                var message = result.Ok
                    ? result.Method switch
                    {
                        "sessions.patch" => $"Updated settings for {key}",
                        "sessions.reset" => $"Reset {key}",
                        "sessions.compact" => result.Kept.HasValue
                            ? $"Compacted {key} ({result.Kept.Value} lines kept)"
                            : $"Compacted {key}",
                        "sessions.delete" => $"Deleted {key}",
                        _ => $"Completed action for {key}"
                    }
                    : result.Error ?? "Request failed";
                AddRecentActivity(
                    $"{title.Replace("✅ ", "").Replace("❌ ", "")}: {message}",
                    category: "session",
                    dashboardPath: !string.IsNullOrWhiteSpace(result.Key) ? $"sessions/{result.Key}" : "sessions",
                    sessionKey: result.Key);

                AppNotification? appNotification = result.Ok
                    ? null
                    : new AppNotification
                    {
                        Title = title,
                        Message = message,
                        Source = "session",
                        Category = "status",
                        Severity = AppNotificationSeverity.Error,
                        DedupeKey = "session-command:" + HashNotificationKey($"{result.Method}|{key}|{message}")
                    };

                AppNotificationPublisher.Publish(
                    _appNotificationService,
                    _toastService,
                    new AppNotificationPublishRequest(
                        appNotification,
                        new ToastContentBuilder()
                            .AddText(title)
                            .AddText(message)));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to show session action toast: {ex.Message}");
            }
        });

        if (result.Ok)
        {
            _ = _connectionManager?.OperatorClient?.RequestSessionsAsync();
        }
    }

    private void OnGatewayNotificationReceived(object? sender, OpenClawNotification notification)
    {
        // Voice overlay: show agent chat responses, and (independently) speak them
        // if the user enabled "Read responses aloud".
        if (notification.IsChat && !string.IsNullOrEmpty(notification.Message))
        {
            var speechText = ChatNotificationSpeechText.Resolve(notification);

            // Suppress TTS/voice overlay when the user has aborted the response.
            if (ChatProvider?.IsResponseSuppressed == true)
                return;

            // Voice overlay disabled — agent responses no longer routed to overlay window.
            // if (_voiceOverlayWindow != null)
            // {
            //     OnUiThread(() =>
            //     {
            //         try
            //         {
            //             _voiceOverlayWindow?.AddAgentResponse(notification.Message);
            //         }
            //         catch { }
            //     });
            // }

            // TTS: read response aloud whenever chat TTS is enabled and ready (any chat surface).
            if (SpeechSetupReadiness.IsAutomaticChatTtsEnabled(_settings))
            {
                _ = (_chatCoordinator?.SpeakResponseAsync(speechText) ?? Task.CompletedTask);
            }
        }

        if (_settings?.ShowNotifications != true) return;
        if (!ShouldShowNotification(notification)) return;

        AppNotification appNotification = AppNotificationMapper.FromGatewayNotification(
            notification,
            LocalizationHelper.GetString("AppNotification_ExecApprovalPending_OpenChatAction"));
        if (notification.IsChat && string.IsNullOrWhiteSpace(appNotification.Message)) return;

        // Store in history
        NotificationHistoryService.AddNotification(new Services.GatewayNotification
        {
            Title = appNotification.Title,
            Message = appNotification.Message,
            Category = notification.Type
        });

        // Show toast
        try
        {
            var builder = new ToastContentBuilder()
                .AddText(appNotification.Title)
                .AddText(appNotification.Message);

            var logoPath = GetNotificationIcon(notification.Type);
            if (!string.IsNullOrEmpty(logoPath) && System.IO.File.Exists(logoPath))
            {
                builder.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Circle);
            }

            if (notification.IsChat)
            {
                builder.AddArgument("action", "open_chat");
                if (!string.IsNullOrEmpty(notification.SessionKey))
                {
                    builder.AddArgument("sessionKey", notification.SessionKey);
                }
                builder.AddButton(new ToastButton()
                    .SetContent("Open Chat")
                    .AddArgument("action", "open_chat")
                    .AddArgument("sessionKey", notification.SessionKey ?? ""));
            }

            AppNotificationPublisher.Publish(
                _appNotificationService,
                _toastService,
                new AppNotificationPublishRequest(
                    appNotification,
                    builder));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show toast: {ex.Message}");
        }
    }

    // ── AppState → tray-level side effects (tray icon, status detail) ──
    // The tray menu is NOT refreshed live while open — data is frozen at
    // open time via TrayMenuSnapshot to avoid WinUI layout races that cause
    // blank subflyouts. The menu captures a fresh snapshot on every open.

    private void OnAppStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_appState == null) return;

        switch (e.PropertyName)
        {
            case nameof(AppState.GatewaySelf):
            case nameof(AppState.Sessions):
            case nameof(AppState.UsageCost):
            case nameof(AppState.Nodes):
                UpdateStatusDetailWindow();
                if (e.PropertyName == nameof(AppState.Nodes))
                    PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(AppState.Channels):
                UpdateChannelIssueNotifications(_appState.Channels);
                UpdateStatusDetailWindow();
                break;
            case nameof(AppState.Config):
                PermissionsRuntimeChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(AppState.CurrentActivity):
                UpdateTrayIcon();
                break;
        }
    }

    private void UpdateChannelIssueNotifications(ChannelHealth[] channels)
    {
        var currentIssueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            if (!TryBuildChannelIssueNotification(channel, out var title, out var message, out var signature))
                continue;

            currentIssueNames.Add(channel.Name);
            if (_reportedChannelIssueSignatures.TryGetValue(channel.Name, out var existingSignature) &&
                string.Equals(existingSignature, signature, StringComparison.Ordinal))
                continue;

            _reportedChannelIssueSignatures[channel.Name] = signature;
            AppNotificationPublisher.Show(
                _appNotificationService,
                title,
                message,
                "channels",
                "status",
                AppNotificationSeverity.Error,
                $"channels:{channel.Name}:status-error",
                "channels",
                LocalizationHelper.GetString("AppNotification_ActionOpenChannels"),
                id: BuildChannelIssueNotificationId(channel.Name));
        }

        foreach (var channelName in _reportedChannelIssueSignatures.Keys.Except(currentIssueNames).ToList())
        {
            _reportedChannelIssueSignatures.Remove(channelName);
            _appNotificationService?.Dismiss(BuildChannelIssueNotificationId(channelName));
        }
    }

    private static bool TryBuildChannelIssueNotification(
        ChannelHealth channel,
        out string title,
        out string message,
        out string signature)
    {
        title = "";
        message = "";
        signature = "";

        if (string.IsNullOrWhiteSpace(channel.Name))
            return false;

        var status = channel.Status?.Trim() ?? "";
        var hasExplicitError = !string.IsNullOrWhiteSpace(channel.Error);
        var hasErrorStatus = !string.IsNullOrWhiteSpace(status) &&
            !ChannelHealth.IsHealthyStatus(status) &&
            !ChannelHealth.IsIntermediateStatus(status) &&
            !status.Equals("not configured", StringComparison.OrdinalIgnoreCase) &&
            !status.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        if (!hasExplicitError && !hasErrorStatus)
            return false;

        var displayName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(channel.Name);
        title = LocalizationHelper.Format("AppNotification_ChannelNeedsAttention_TitleFormat", displayName);
        message = hasExplicitError
            ? channel.Error!.Trim()
            : LocalizationHelper.Format("AppNotification_ChannelNeedsAttention_StatusMessageFormat", channel.Name, status);
        signature = $"{status}|{message}";
        return true;
    }

    private static string BuildChannelIssueNotificationId(string channelName) =>
        $"channel-issue:{channelName.Trim().ToLowerInvariant()}";

    private void PublishSandboxRiskNotificationIfNeeded()
    {
        if (_settings is null || _appNotificationService is null)
            return;

        if (!_settings.SystemRunSandboxEnabled)
        {
            _sandboxRiskProbeGeneration++;
            _sandboxRiskProbeInFlight = false;
            PublishSandboxRiskNotification(
                "disabled",
                LocalizationHelper.GetString("AppNotification_SandboxDisabled_Title"),
                LocalizationHelper.GetString("AppNotification_SandboxDisabled_Message"));
            return;
        }

        if (_sandboxRiskAvailabilityCache is { } cachedAvailability)
            PublishSandboxRiskNotification(cachedAvailability);
        else
            ClearSandboxRiskNotification();

        StartSandboxRiskProbeIfNeeded();
    }

    private void StartSandboxRiskProbeIfNeeded()
    {
        if (_settings is not { SystemRunSandboxEnabled: true })
            return;

        var now = DateTimeOffset.UtcNow;
        if (_sandboxRiskProbeInFlight)
            return;

        if (_sandboxRiskAvailabilityCache is { ProbeErrored: false } &&
            now - _lastSandboxRiskProbeStartedAt < SandboxRiskProbeRefreshInterval)
        {
            return;
        }

        _sandboxRiskProbeInFlight = true;
        _lastSandboxRiskProbeStartedAt = now;
        var generation = ++_sandboxRiskProbeGeneration;

        _ = Task.Run(() => MxcAvailability.Probe(new AppLogger()))
            .ContinueWith(
                task => OnUiThread(() => CompleteSandboxRiskProbe(generation, task)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
    }

    private void CompleteSandboxRiskProbe(int generation, Task<MxcAvailability> task)
    {
        if (generation != _sandboxRiskProbeGeneration)
            return;

        _sandboxRiskProbeInFlight = false;

        MxcAvailability availability;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            availability = task.Result;
        }
        else
        {
            var message = task.Exception?.GetBaseException().Message ?? LocalizationHelper.GetString("SandboxPage_ProbeErrorReason");
            Logger.Warn($"Sandbox availability probe failed: {message}");
            availability = new MxcAvailability(
                false,
                false,
                false,
                null,
                new[] { message },
                probeErrored: true);
        }

        _sandboxRiskAvailabilityCache = availability;
        PublishSandboxRiskNotification(availability);
    }

    private void PublishSandboxRiskNotification(MxcAvailability availability)
    {
        if (availability.CanRunSystemRunSandbox)
        {
            ClearSandboxRiskNotification();
            return;
        }

        var reasonText = availability.SystemRunSandboxUnsupportedReasons.Count > 0
            ? string.Join("  ·  ", availability.SystemRunSandboxUnsupportedReasons)
            : LocalizationHelper.GetString("AppNotification_SandboxUnavailable_DefaultReason");
        var blockHostFallback = _settings?.SystemRunBlockHostFallbackWhenMxcUnavailable == true;
        var mode = blockHostFallback ? "blocked" : "host-fallback";
        var title = blockHostFallback
            ? LocalizationHelper.GetString("AppNotification_SandboxUnavailableBlocked_Title")
            : LocalizationHelper.GetString("AppNotification_SandboxUnavailable_Title");
        var message = blockHostFallback
            ? LocalizationHelper.Format("AppNotification_SandboxUnavailableBlocked_MessageFormat", reasonText)
            : LocalizationHelper.Format("AppNotification_SandboxUnavailable_MessageFormat", reasonText);

        PublishSandboxRiskNotification(
            $"unavailable:{mode}:{reasonText}",
            title,
            message);
    }

    private void PublishSandboxRiskNotification(string riskKey, string title, string message)
    {
        if (string.Equals(_lastSandboxRiskNotificationKey, riskKey, StringComparison.Ordinal))
            return;

        _lastSandboxRiskNotificationKey = riskKey;
        AppNotificationPublisher.Show(
            _appNotificationService,
            title,
            message,
            "sandbox",
            "system.run",
            AppNotificationSeverity.Warning,
            SandboxRiskNotificationDedupeKey,
            "sandbox",
            LocalizationHelper.GetString("AppNotification_ActionOpenSandbox"),
            id: SandboxRiskNotificationId);
    }

    private void ClearSandboxRiskNotification()
    {
        _lastSandboxRiskNotificationKey = null;
        _appNotificationService?.ClearSource("sandbox");
    }


    private static string? GetNotificationIcon(string? type)
    {
        // For now, use the app icon for all notifications
        // In the future, we could create category-specific icons
        var appDir = AppContext.BaseDirectory;
        var iconPath = System.IO.Path.Combine(appDir, "Assets", "openclaw.ico");
        return System.IO.File.Exists(iconPath) ? iconPath : null;
    }

    private bool ShouldShowNotification(OpenClawNotification notification)
    {
        if (_settings == null) return true;

        // Chat toggle: suppress all chat responses if disabled
        if (notification.IsChat && !_settings.NotifyChatResponses)
            return false;

        // Suppress chat notifications when a chat window is already showing them
        if (notification.IsChat)
        {
            if (_windowManager?.IsHubOpen == true)
                return false;
            if (_windowManager?.IsChatVisible == true)
                return false;
        }

        var type = notification.Type;
        if (type == null) return true;
        return s_notifTypeMap.TryGetValue(type, out var selector) ? selector(_settings) : true;
    }

    #endregion

    #region Health Check

    /// <summary>User-initiated health check (from UI button). No background timers.</summary>
    private async Task RunHealthCheckAsync(bool userInitiated = false)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null)
        {
            if (_settings?.EnableNodeMode == true && _nodeService?.IsConnected == true)
            {
                RecordHealthCheckTime();
                OnUiThread(UpdateStatusDetailWindow);
                if (userInitiated)
                {
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                        .AddText("Node Mode is connected; gateway health is streaming."));
                }
                return;
            }

            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckNotConnected")));
            }
            return;
        }

        try
        {
            RecordHealthCheckTime();
            await client.CheckHealthAsync();
            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheck"))
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckSent")));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Health check failed: {ex.Message}");
            if (userInitiated)
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText(LocalizationHelper.GetString("Toast_HealthCheckFailed"))
                    .AddText(ex.Message));
            }
        }
    }

    private void RecordHealthCheckTime()
    {
        OnUiThread(() =>
        {
            if (_appState is not null)
                _appState.LastCheckTime = DateTime.Now;
        });
    }

    #endregion

    #region Tray Icon

    private void UpdateTrayIcon() => _trayController?.RefreshIcon();

    private TrayStateSnapshot CaptureTraySnapshot()
    {
        return new TrayStateSnapshot
        {
            Status = _appState!.Status,
            OverallState = _connectionManager?.CurrentSnapshot.OverallState,
            CurrentActivity = _appState!.CurrentActivity,
            Channels = _appState!.Channels,
            Nodes = _appState!.Nodes,
            LocalNodeFallback = _nodeService?.GetLocalNodeInfo(),
            AuthFailureMessage = _appState!.AuthFailureMessage,
            LastCheckTime = _appState!.LastCheckTime,
            Settings = _settings,
            IsMcpRunning = _nodeService?.IsMcpRunning == true,
            McpStartupError = _nodeService?.McpStartupError
        };
    }

    #endregion

    #region Window Management

    internal void ShowHub(string? navigateTo = null, bool activate = true)
    {
        _windowManager?.ShowHub(navigateTo, activate);
    }

    private void ShowSettings()
    {
        ShowHub("settings");
    }

    private void OnSettingsCommandCenterRequested(object? sender, EventArgs e)
    {
        ShowStatusDetail();
    }

    private void ShowWebChat(string? sessionKey = null)
    {
        if (_settings == null) return;
        if (!TryResolveChatCredentials(out _, out _, out _, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway URL or credential is not configured");
            return;
        }

        if (isBootstrapToken)
        {
            ShowConnectionSettingsForPairingIssue(
                "Chat",
                "Gateway pairing is not complete");
            return;
        }

        // Stash the session key on both App (fallback when HubWindow doesn't exist)
        // and HubWindow (existing path) so ChatPage can pick it up after navigation.
        if (!string.IsNullOrEmpty(sessionKey))
        {
            PendingChatSessionKey = sessionKey;
            _windowManager?.SetPendingChatSessionKey(sessionKey);
        }
        else
        {
            PendingChatSessionKey = null;
            _windowManager?.SetPendingChatSessionKey(null);
        }

        ShowHub("chat");
    }

    private void ShowStatusDetail()
    {
        ShowHub("connection");
    }

    private void ShowConnectionStatusWindow()
    {
        _windowManager?.ShowConnectionStatus();
    }

    // ─── Inbound pairing approvals ───────────────────────────────────────

    /// <summary>Feeds fresh device/node pending pair-lists into the approval coordinator.</summary>
    private void OnPairListsChanged(object? sender, EventArgs e)
    {
        _pairingApprovalCoordinator?.OnPairListsUpdated(_appState?.DevicePairList, _appState?.NodePairList);
    }

    /// <summary>
    /// All identifiers the local Windows node may be known by in the gateway's pending node list,
    /// so the approval coordinator never prompts the operator to approve their own machine. The node
    /// advertises itself as <c>NodeId ?? FullDeviceId</c>; we offer both so the own-node filter is
    /// robust to either identifier space.
    /// </summary>
    private IReadOnlyCollection<string> BuildOwnNodeIds()
    {
        var ids = new List<string>(2);
        var fullDeviceId = _nodeService?.FullDeviceId;
        if (!string.IsNullOrWhiteSpace(fullDeviceId)) ids.Add(fullDeviceId);
        var nodeId = _nodeService?.NodeId;
        if (!string.IsNullOrWhiteSpace(nodeId) && !ids.Contains(nodeId)) ids.Add(nodeId);
        return ids;
    }

    /// <summary>A new inbound pairing request arrived — present the focused dialog and an awareness toast.</summary>
    private void OnPairingApprovalRequested(object? sender, PendingApproval approval)
    {
        OnUiThread(() =>
        {
            // Only steal foreground when the dialog isn't already open. On a reconnect burst the
            // gateway re-pushes every pending request at once; the first opens + foregrounds the
            // dialog, the rest just enqueue into it without repeated focus-stealing.
            var alreadyOpen = _pairingApprovalDialog is { IsClosed: false };
            ShowPairingApprovalDialog(bringToFront: !alreadyOpen);

            var name = string.IsNullOrWhiteSpace(approval.DisplayName)
                ? approval.DeviceId
                : approval.DisplayName!;
            AddRecentActivity(
                LocalizationHelper.GetString("Toast_PairingRequestTitle"),
                category: "pairing",
                dashboardPath: "connection",
                details: name);

            var bodyKey = approval.Kind == PairingApprovalKind.NodePair
                ? "Toast_PairingRequestBodyNode"
                : "Toast_PairingRequestBody";
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_PairingRequestTitle"))
                .AddText(string.Format(LocalizationHelper.GetString(bodyKey), name))
                .AddButton(new ToastButton()
                    .SetContent(LocalizationHelper.GetString("Toast_PairingReview"))
                    .AddArgument("action", "review_pairing")),
                "pairing-request",
                approval.DecisionId);
        });
    }

    /// <summary>After a decision is confirmed by the gateway, surface a confirmation toast + activity entry.</summary>
    private void OnPairingDecisionCompleted(object? sender, PairingDecisionResult result)
    {
        if (!result.Success) return; // defensive — the coordinator only raises this for confirmed decisions
        OnUiThread(() =>
        {
            var name = string.IsNullOrWhiteSpace(result.Approval.DisplayName)
                ? result.Approval.DeviceId
                : result.Approval.DisplayName!;
            var titleKey = result.Approved ? "Toast_PairingApprovedTitle" : "Toast_PairingRejectedTitle";
            var bodyKey = result.Approved ? "Toast_PairingApprovedBody" : "Toast_PairingRejectedBody";
            AddRecentActivity(
                LocalizationHelper.GetString(titleKey),
                category: "pairing",
                dashboardPath: "connection",
                details: name);
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString(titleKey))
                .AddText(string.Format(LocalizationHelper.GetString(bodyKey), name)),
                "pairing-decided",
                result.Approval.DecisionId);
        });
    }

    /// <summary>Opens (or re-focuses) the inbound pairing approval dialog when there is something to decide.</summary>
    private void ShowPairingApprovalDialog() => ShowPairingApprovalDialog(bringToFront: true);

    private void ShowPairingApprovalDialog(bool bringToFront)
    {
        if (_pairingApprovalCoordinator == null) return;
        if (_pairingApprovalCoordinator.Current.Count == 0)
        {
            ShowStatusDetail();
            return;
        }

        if (_pairingApprovalDialog is { IsClosed: false } existing)
        {
            if (bringToFront) existing.ShowForeground();
            return;
        }

        _pairingApprovalDialog = new OpenClawTray.Dialogs.PairingApprovalDialog(_pairingApprovalCoordinator);
        _pairingApprovalDialog.ShowForeground();
    }

    public async Task<bool> RestartSshTunnelAsync()
    {
        return _connectionManager is not null &&
            await _connectionManager.RestartSshTunnelAsync();
    }

    private void RestartSshTunnel() =>
        AsyncEventHandlerGuard.Run(
            RestartSshTunnelCoreAsync,
            new AppLogger(),
            nameof(RestartSshTunnel));

    private async Task RestartSshTunnelCoreAsync()
    {
        if (_settings?.UseSshTunnel != true)
        {
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText("Managed SSH tunnel mode is not enabled."));
            return;
        }

        try
        {
            Logger.Info("Restarting managed SSH tunnel from Command Center");
            DiagnosticsJsonlService.Write("tunnel.restart_requested", new
            {
                localEndpoint = _settings.SshTunnelLocalPort > 0 ? $"127.0.0.1:{_settings.SshTunnelLocalPort}" : null,
                remotePort = _settings.SshTunnelRemotePort
            });

            var restarted = await RestartSshTunnelAsync();
            UpdateStatusDetailWindow();
            if (restarted)
            {
                _sshTunnelRecoveryBudget.Reset();
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel")
                    .AddText("Restarted and authenticated."));
            }
            else
            {
                _toastService!.ShowToast(new ToastContentBuilder()
                    .AddText("SSH tunnel restart failed")
                    .AddText("The owned tunnel or authenticated gateway connection could not be verified."));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"SSH tunnel restart request failed: {ex.Message}");
            DiagnosticsJsonlService.Write("tunnel.restart_request_failed", new { ex.Message });
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel restart failed")
                .AddText(ex.Message));
        }
    }

    private async Task RefreshCommandCenterAsync()
    {
        await RunHealthCheckAsync(userInitiated: true);
        var client = _connectionManager?.OperatorClient;
        if (client != null)
        {
            await client.RequestSessionsAsync();
            await client.RequestUsageAsync();
            await client.RequestNodesAsync();
        }
        UpdateStatusDetailWindow();
    }

    private void UpdateStatusDetailWindow()
    {
        // No-op — hub window observes AppState.PropertyChanged directly.
        // Tray status detail window reads from AppState too.
    }

    internal GatewayCommandCenterState BuildCommandCenterState() =>
        new CommandCenterStateBuilder(CaptureSnapshot()).Build();

    internal IReadOnlyList<ConnectionDiagnosticEvent> GetConnectionDiagnosticEvents() =>
        _connectionManager?.Diagnostics.GetRecent(200) ?? [];

    private AppStateSnapshot CaptureSnapshot()
    {
        var activeGateway = _gatewayRegistry?.GetActive();
        return new AppStateSnapshot
        {
            Status = _appState!.Status,
            OverallState = _connectionManager?.CurrentSnapshot.OverallState,
            LastCheckTime = _appState!.LastCheckTime,
            Channels = _appState!.Channels,
            Sessions = _appState!.Sessions,
            Nodes = _appState!.Nodes,
            Usage = _appState!.Usage,
            UsageStatus = _appState!.UsageStatus,
            UsageCost = _appState!.UsageCost,
            GatewaySelf = _appState!.GatewaySelf,
            AuthFailureMessage = _appState!.AuthFailureMessage,
            LastUpdateInfo = _appState!.UpdateInfo,
            Settings = _settings,
            NodeService = _nodeService,
            IsMcpRunning = _nodeService?.IsMcpRunning == true,
            McpStartupError = _nodeService?.McpStartupError,
            NodePairingApprovalKind = _connectionManager?.CurrentSnapshot.NodePairingApprovalKind
                ?? PairingApprovalKind.Unknown,
            NodePairingRequestId = _connectionManager?.CurrentSnapshot.NodePairingRequestId,
            SshTunnelSnapshot = _sshTunnelService?.CreateSnapshot(),
            HasGatewayClient = _connectionManager?.OperatorClient != null,
            EffectiveGatewayUrl = activeGateway?.Url ?? _settings?.GatewayUrl,
            EffectiveBrowserControlPort = activeGateway?.BrowserControlPort,
            HasActiveGatewayRecord = activeGateway != null,
            ActiveGatewayHasSharedToken = !string.IsNullOrWhiteSpace(activeGateway?.SharedGatewayToken),
            NodeConnectionState = _connectionManager?.CurrentSnapshot.NodeState
                ?? OpenClaw.Connection.RoleConnectionState.Idle,
            ActiveGatewaySshTunnel = activeGateway?.SshTunnel
        };
    }

    private void ShowNotificationHistory()
    {
        // ActivityPage removed; legacy callers now land on the Channels page.
        ShowHub("channels");
    }

    private void ShowActivityStream(string? filter = null)
    {
        // ActivityPage removed; legacy callers now land on the Channels page.
        _ = filter;
        ShowHub("channels");
    }

    private async Task ShowOnboardingAsync()
    {
        if (_windowManager is not null)
        {
            await _windowManager.ShowOnboardingAsync();
        }
    }

    private async Task ShowGatewayWizardAsync()
    {
        if (_windowManager is not null)
        {
            await _windowManager.ShowGatewayWizardAsync();
        }
    }

    private void OnSetupAdvancedSetupRequested(object? sender, EventArgs e)
    {
        ShowHub("connection");
        _windowManager?.CloseSetup();
    }

    private void OnSetupCompleted(object? sender, SetupCompletedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RestartAfterSetupAsync(e.EnableAutoStart),
            new AppLogger(),
            nameof(OnSetupCompleted));

    private async Task RestartAfterSetupAsync(bool enableAutoStart)
    {
        var exePath = ResolveCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            await ShowSetupRestartErrorAsync("OpenClaw setup finished, but the tray executable could not be found for restart.");
            return;
        }

        try
        {
            if (enableAutoStart)
            {
                try
                {
                    await AutoStartManager.SetAutoStartAsync(true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to enable autostart after setup: {ex}");
                }
            }

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--post-setup-restart");
            psi.ArgumentList.Add("--wait-for-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--post-setup-launch");
            psi.ArgumentList.Add("chat");

            var restarted = Process.Start(psi);
            if (restarted == null)
                throw new InvalidOperationException("Process.Start returned null.");
            restarted.Dispose();

            Logger.Info("Started post-setup tray restart process");
            _windowManager?.CloseSetup();
            await ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to restart tray after setup: {ex}");
            await ShowSetupRestartErrorAsync("OpenClaw setup finished, but restarting the tray failed. The current tray will keep running; please exit and reopen OpenClaw.");
        }
    }

    private async Task ShowSetupRestartErrorAsync(string message)
    {
        var xamlRoot = _windowManager?.SetupXamlRoot;
        if (xamlRoot is null)
        {
            Logger.Error(message);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart OpenClaw",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        };

        await dialog.ShowAsync();
    }

    private static string? ResolveCurrentExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            return Environment.ProcessPath;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private void ShowSurfaceImprovementsTipIfNeeded()
    {
        if (_settings == null || _settings.HasSeenActivityStreamTip) return;

        _settings.HasSeenActivityStreamTip = true;
        _settings.Save();

        try
        {
            _toastService!.ShowToast(new ToastContentBuilder()
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTip"))
                .AddText(LocalizationHelper.GetString("Toast_ActivityStreamTipDetail"))
                .AddButton(new ToastButton()
                    .SetContent(LocalizationHelper.GetString("Toast_ActivityStreamTipButton"))
                    .AddArgument("action", "open_activity")));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show activity stream tip: {ex.Message}");
        }
    }

    #endregion

    private bool TryResolveChatCredentials(
        out string gatewayUrl,
        out string token,
        out string credentialSource,
        out bool isBootstrapToken)
    {
        gatewayUrl = string.Empty;
        token = string.Empty;
        credentialSource = "none";
        isBootstrapToken = false;

        if (_settings == null)
            return false;

        if (!InteractiveGatewayCredentialResolver.TryResolve(
            _gatewayRegistry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
            _settings.GetEffectiveGatewayUrl(),
            _settings.LegacyToken,
            _settings.LegacyBootstrapToken,
            (record, candidate) =>
                _managedLocalPortProvenance?.IsStrongCredentialAllowed(record, candidate) == true,
            out var credential) ||
            credential == null)
        {
            return false;
        }

        gatewayUrl = credential.GatewayUrl;
        token = credential.Token;
        credentialSource = credential.Source;
        isBootstrapToken = credential.IsBootstrapToken;
        return true;
    }

    #region Actions

    private void OpenDashboard(string? path = null)
    {
        if (_settings == null) return;
        if (!EnsureSshTunnelConfigured())
        {
            _toastService?.ShowToast(new ToastContentBuilder()
                .AddText("SSH tunnel")
                .AddText(_sshTunnelService?.LastError ?? "Check SSH tunnel settings and logs."));
            return;
        }

        if (!TryResolveChatCredentials(out var gatewayUrl, out var token, out var credentialSource, out var isBootstrapToken))
        {
            ShowConnectionSettingsForPairingIssue(
                "Dashboard",
                "Gateway URL or credential is not configured");
            return;
        }

        var url = GatewayDashboardUrlBuilder.Build(
            gatewayUrl,
            path,
            token,
            !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken);

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open dashboard: {ex.Message}");
        }
    }

    // ── IAppCommands implementation ─────────────────────────────────────

    void IAppCommands.OpenDashboard(string? path) => OpenDashboard(path);
    void IAppCommands.Navigate(string pageTag) => ShowHub(pageTag);
    void IAppCommands.Reconnect() => ReconnectWithSyncedBrowserProxyForward();
    void IAppCommands.Disconnect()
    {
        _ = _connectionManager?.DisconnectByUserAsync();
        UpdateTrayIcon();
    }
    void IAppCommands.ShowVoiceOverlay() => ShowHub("voice");
    void IAppCommands.ShowChat() => ShowChatWindow();
    void IAppCommands.CheckForUpdates() => _ = _updateCoordinator!.CheckForUpdatesUserInitiatedAsync();
    void IAppCommands.ShowOnboarding() => _ = ShowOnboardingAsync();
    void IAppCommands.OpenLocalAiLogs() =>
        OpenFolder(new LocalAiPaths(AppIdentity.ResolveSetupLocalDataDirectory()).LogsDirectory, "Local AI logs");
    void IAppCommands.ShowGatewayWizard() => _ = ShowGatewayWizardAsync();
    void IAppCommands.ShowConnectionStatus() => ShowConnectionStatusWindow();
    void IAppCommands.NotifySettingsSaved() => OnSettingsSaved(this, EventArgs.Empty);
    Task<bool> IAppCommands.ResendOpenTelemetryProbeAsync() => ResendOpenTelemetryProbeAsync();
    event EventHandler? IPermissionsPageRuntimeHost.Changed
    {
        add => PermissionsRuntimeChanged += value;
        remove => PermissionsRuntimeChanged -= value;
    }
    GatewayConnectionSnapshot IPermissionsPageRuntimeHost.ConnectionSnapshot => _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle;
    GatewayNodeInfo[] IPermissionsPageRuntimeHost.Nodes => _appState?.Nodes ?? Array.Empty<GatewayNodeInfo>();
    string? IPermissionsPageRuntimeHost.LocalNodeDeviceId => _nodeService?.FullDeviceId;
    JsonElement? IPermissionsPageRuntimeHost.GatewayConfig => _appState?.Config;
    string? IPermissionsPageRuntimeHost.McpStartupError => _nodeService?.McpStartupError;
    string IPermissionsPageRuntimeHost.McpEndpoint => NodeService.McpServerUrl;
    bool IPermissionsPageRuntimeHost.IsMcpTokenReady => File.Exists(NodeService.McpTokenPath);
    int IPermissionsPageRuntimeHost.McpServedCapabilityCount => NodeCapabilityGating.CountMcpServedCapabilities(_settings);
    PermissionsVoiceSetupRequirement IPermissionsPageRuntimeHost.VoiceSetupRequirement => GetPermissionsVoiceSetupRequirement();

    private PermissionsVoiceSetupRequirement GetPermissionsVoiceSetupRequirement()
    {
        var needsSpeechModel = _settings?.NodeSttEnabled == true
            && SpeechSetupReadiness.IsConfiguredSttModelSetupRequired(_settings);
        var needsVoiceSetup = _settings?.NodeTtsEnabled == true
            && _settings is not null
            && SpeechSetupReadiness.IsConfiguredTtsProviderSetupRequired(_settings);

        return (needsSpeechModel, needsVoiceSetup) switch
        {
            (true, true) => PermissionsVoiceSetupRequirement.SpeechModelAndVoiceSetup,
            (true, false) => PermissionsVoiceSetupRequirement.SpeechModel,
            (false, true) => PermissionsVoiceSetupRequirement.VoiceSetup,
            _ => PermissionsVoiceSetupRequirement.None,
        };
    }

    private void ToggleChannel(string channelName) =>
        AsyncEventHandlerGuard.Run(
            () => ToggleChannelAsync(channelName),
            new AppLogger(),
            nameof(ToggleChannel));

    private async Task ToggleChannelAsync(string channelName)
    {
        var client = _connectionManager?.OperatorClient;
        if (client == null) return;

        var channel = _appState!.Channels.FirstOrDefault(c => c.Name == channelName);
        if (channel == null) return;

        try
        {
            var isRunning = ChannelHealth.IsHealthyStatus(channel.Status);
            if (isRunning)
            {
                await client.StopChannelAsync(channelName);
                AddRecentActivity($"Stopped channel: {channelName}", category: "channel", dashboardPath: "settings");
            }
            else
            {
                await client.StartChannelAsync(channelName);
                AddRecentActivity($"Started channel: {channelName}", category: "channel", dashboardPath: "settings");
            }
             
            // Refresh health
            await RunHealthCheckAsync();
        }
        catch (Exception ex)
        {
            AddRecentActivity($"Channel toggle failed: {channelName}", category: "channel", details: ex.Message);
            Logger.Error($"Failed to toggle channel: {ex.Message}");
        }
    }

    private void ToggleAutoStart() =>
        AsyncEventHandlerGuard.Run(
            ToggleAutoStartAsync,
            new AppLogger(),
            nameof(ToggleAutoStart));

    private async Task ToggleAutoStartAsync()
    {
        if (_settings == null) return;

        var origin = SettingsStore is { } store
            ? GetOrCreateSettingsWriteOrigin(ref _trayAutoStartWriteOrigin, store)
            : null;
        await ApplyAutoStartCore(origin, !_settings.AutoStart);
    }

    /// <summary>
    /// Persists the auto-start setting and applies the Windows OS registration in the original
    /// order (save, then await the OS write, then notify). Returns true only when the OS write
    /// and notify complete, so the caller shows its saved confirmation only on success. The save
    /// is tagged with the originating writer so other active settings consumers refresh while the
    /// triggering view model ignores its own change event.
    /// </summary>
    public async Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart)
        => await ApplyAutoStartCore(origin, autoStart);

    private async Task<bool> ApplyAutoStartCore(SettingsWriteOrigin? origin, bool autoStart)
    {
        if (_settings == null) return false;
        try
        {
            if (SettingsStore is { } store)
            {
                store.Update(origin, edit => edit.AutoStart = autoStart);
            }
            else
            {
                _settings.AutoStart = autoStart;
                _settings.Save();
            }

            await AutoStartManager.SetAutoStartAsync(autoStart);
            OnSettingsSaved(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"ApplyAutoStart failed: {ex.Message}");
            var effectiveAutoStart = await AutoStartManager.IsAutoStartEnabledAsync();
            if (SettingsStore is { } store)
            {
                store.Update(origin, edit => edit.AutoStart = effectiveAutoStart);
            }
            else if (_settings != null)
            {
                _settings.AutoStart = effectiveAutoStart;
                _settings.Save();
            }
            return false;
        }
    }

    /// <summary>
    /// Aligns the stored auto-start preference with the state Windows actually reports,
    /// so the Settings toggle never claims auto-start is on while nothing launches at logon.
    /// </summary>
    private async Task ReconcileAutoStartOnStartupAsync()
    {
        if (_settings == null) return;

        var configured = _settings.AutoStart;
        var effective = await AutoStartManager.ReconcileAutoStartAsync(configured);
        if (effective == configured) return;

        Logger.Info($"Auto-start setting corrected from {configured} to {effective} to match Windows.");
        if (SettingsStore is { } store)
        {
            store.Update(null, edit => edit.AutoStart = effective);
        }
        else
        {
            _settings.AutoStart = effective;
            _settings.Save();
        }

        OnSettingsSaved(this, EventArgs.Empty);
    }

    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Logger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open log file: {ex.Message}");
        }
    }

    private void OpenLogFolder()
    {
        OpenFolder(Path.GetDirectoryName(Logger.LogFilePath), "logs");
    }

    private void OpenConfigFolder()
    {
        OpenFolder(SettingsManager.SettingsDirectoryPath, "config");
    }

    private void OpenDiagnosticsFolder()
    {
        OpenFolder(Path.GetDirectoryName(DiagnosticsJsonlService.FilePath), "diagnostics");
    }

    private static void OpenFolder(string? folderPath, string label)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Logger.Warn($"Failed to open {label} folder: path is not configured");
            return;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            Logger.Info($"Opened {label} folder: {folderPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Failed to open {label} folder {folderPath}: {ex.Message}");
        }
    }

    private void OnVoiceHotkeyPressed(object? sender, EventArgs e)
    {
        if (_dispatcherQueue == null) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            _windowManager?.ShowHubChatAndStartVoice();
        });
    }

    private void OnSettingsHotkeyPressed(object? sender, EventArgs e)
    {
        OnUiThread(ShowSettings);
    }

    #endregion

    #region Deep Links

    private async Task StopVoiceAsync()
    {
        var voiceService = _nodeService?.VoiceService;
        if (voiceService != null)
            await voiceService.StopAsync();
    }

    public Task SpeakChatTextAsync(string text) =>
        _chatCoordinator?.SpeakChatTextAsync(text) ?? Task.CompletedTask;

    public void StopChatSpeaking() => _chatCoordinator?.StopSpeaking();

    /// <summary>Raised when speaker mute state changes from any source (composer, settings, etc.).</summary>
    public event Action<bool>? SpeakerMuteChanged;

    /// <summary>
    /// Sets speaker mute from any surface (chat window, chat page, voice settings) and persists it.
    /// The public path publishes a null-origin settings change, so an open Settings page still
    /// reflects a mute toggled elsewhere.
    /// </summary>
    public void SetChatSpeakerMuted(bool muted)
        => SetChatSpeakerMuted(muted, origin: null);

    private void SetChatSpeakerMuted(bool muted, SettingsWriteOrigin? origin)
    {
        if (_chatCoordinator is { } c) c.IsMuted = muted;

        if (_settings != null && SettingsStore is { } store)
        {
            store.Update(origin, edit => edit.VoiceTtsEnabled = !muted);
        }
        else if (_settings != null)
        {
            _settings.VoiceTtsEnabled = !muted;
            _settings.Save();
        }

        SpeakerMuteChanged?.Invoke(muted);
    }

    #endregion

    private bool EnsureSshTunnelConfigured()
    {
        if (_settings == null)
        {
            return false;
        }

        if (_settings.UseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(_settings.SshTunnelUser) ||
                string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ||
                _settings.SshTunnelRemotePort is < 1 or > 65535 ||
                _settings.SshTunnelLocalPort is < 1 or > 65535)
            {
                Logger.Warn("SSH tunnel is enabled but settings are incomplete");
                UpdateTrayIcon();
                return false;
            }

            try
            {
                _sshTunnelService ??= new SshTunnelService(new AppLogger());
                var includeBrowserProxy = BrowserProxySshTunnelForwardPolicy.ShouldInclude(
                    _settings.NodeBrowserProxyEnabled,
                    _settings.SshTunnelRemotePort,
                    _settings.SshTunnelLocalPort);
                _sshTunnelService.EnsureStarted(
                    _settings.SshTunnelUser,
                    _settings.SshTunnelHost,
                    _settings.SshTunnelRemotePort,
                    _settings.SshTunnelLocalPort,
                    includeBrowserProxy,
                    _settings.SshTunnelSshPort);
                DiagnosticsJsonlService.Write("tunnel.ensure_started", new
                {
                    status = _sshTunnelService.Status.ToString(),
                    localEndpoint = $"127.0.0.1:{_settings.SshTunnelLocalPort}",
                    remoteHost = string.IsNullOrWhiteSpace(_settings.SshTunnelHost) ? null : _settings.SshTunnelHost,
                    remotePort = _settings.SshTunnelRemotePort
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start SSH tunnel: {ex.Message}");
                UpdateTrayIcon();
                return false;
            }
        }
        else
        {
            _sshTunnelService?.Stop();
        }

        return true;
    }

    private void OnSshTunnelExited(object? sender, SshTunnelExit tunnelExit) =>
        AsyncEventHandlerGuard.Run(
            () => OnSshTunnelExitedAsync(tunnelExit),
            new AppLogger(),
            nameof(OnSshTunnelExited));

    private async Task OnSshTunnelExitedAsync(SshTunnelExit tunnelExit)
    {
        var connectionManager = _connectionManager;
        var tunnelService = _sshTunnelService;
        if (tunnelService?.TryMarkRestarting(tunnelExit) != true)
            return;

        if (!_sshTunnelRecoveryBudget.TryReserve(
                tunnelExit,
                DateTimeOffset.UtcNow,
                out var retryDelay))
        {
            const string reason = "SSH tunnel recovery stopped after repeated failures. Restart it manually after correcting the tunnel configuration.";
            tunnelService.TryMarkRecoveryFailed(tunnelExit, reason);
            Logger.Warn(reason);
            DiagnosticsJsonlService.Write("tunnel.restart_exhausted", new
            {
                owner = tunnelExit.Owner.ToString(),
                tunnelExit.ExitCode
            });
            return;
        }

        Logger.Warn(
            $"SSH tunnel exited unexpectedly (code {tunnelExit.ExitCode}); " +
            $"restarting in {retryDelay.TotalSeconds:0}s...");
        DiagnosticsJsonlService.Write("tunnel.restart_scheduled", new
        {
            exitCode = tunnelExit.ExitCode,
            retryDelaySeconds = retryDelay.TotalSeconds,
            localEndpoint = tunnelService.CurrentLocalPort > 0
                ? $"127.0.0.1:{tunnelService.CurrentLocalPort}"
                : null
        });
        await Task.Delay(retryDelay);

        try
        {
            bool recovered;
            if (tunnelExit.Owner == SshTunnelOwner.GatewayConnectionManager)
            {
                // The connection manager owns the registry-backed tunnel and both
                // gateway clients. Reconnect through it so recovery cannot drift
                // back to the legacy global SSH settings.
                recovered = connectionManager != null &&
                    await connectionManager.RecoverSshTunnelAsync(tunnelExit);
            }
            else
            {
                // Settings-owned tunnels are tunnel-only. Restart the exact
                // generation/configuration without promoting them into a gateway reconnect.
                recovered = tunnelService.TryRestart(tunnelExit);
            }

            if (!recovered)
            {
                const string reason = "SSH tunnel recovery was declined because its owner or connection intent changed.";
                tunnelService.TryMarkRecoveryFailed(tunnelExit, reason);
                Logger.Warn(reason);
                DiagnosticsJsonlService.Write("tunnel.restart_declined", new
                {
                    owner = tunnelExit.Owner.ToString(),
                    tunnelExit.ExitCode
                });
                return;
            }

            _sshTunnelRecoveryBudget.ReportRecovered(tunnelExit);
            Logger.Info("SSH tunnel restarted successfully");
            DiagnosticsJsonlService.Write("tunnel.restart_succeeded", new
            {
                localEndpoint = tunnelService.CurrentLocalPort > 0
                    ? $"127.0.0.1:{tunnelService.CurrentLocalPort}"
                    : null
            });
        }
        catch (Exception ex)
        {
            tunnelService.TryMarkRecoveryFailed(tunnelExit, $"SSH tunnel restart failed: {ex.Message}");
            Logger.Error($"SSH tunnel restart failed: {ex.Message}");
            DiagnosticsJsonlService.Write("tunnel.restart_failed", new { ex.Message });
        }
    }
}
