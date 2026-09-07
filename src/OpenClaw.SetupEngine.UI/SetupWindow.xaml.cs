using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using OpenClaw.Shared.Inference;
using OpenClaw.SetupEngine.UI.Pages;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.UI;

public sealed partial class SetupWindow : Window
{
    private SetupConfig _config = null!;
    private bool _isWelcomeInstallSelected = true;
    private SetupRunLock? _setupLock;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task<StepResult>? _contextApplyTask;
    private readonly TaskCompletionSource<bool> _initialContentReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _cleanupCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isClosed;
    private bool _persistStartupPreferenceOnComplete = true;
    private bool _showStartupPreferenceOnComplete = true;
    private readonly string _dataDir;
    private readonly string _localDataDir;
    private readonly object _localAiHardwareProbeLock = new();
    private Task<HostHardwareInfo>? _localAiHardwareProbeTask;
    private readonly WslViabilityProbe _wslViabilityProbe = new(InspectWslViabilityAsync);

    public static SetupWindow? Active { get; private set; }

    public event EventHandler? AdvancedSetupRequested;
    public event EventHandler<SetupCompletedEventArgs>? SetupCompleted;
    public bool IsClosed => _isClosed;
    public Task CleanupCompleted => _cleanupCompleted.Task;
    internal string DataDir => _dataDir;
    internal string LocalDataDir => _localDataDir;
    public bool CanNavigateToWizard =>
        !_isClosed &&
        _setupLock is not null &&
        RootFrame.Content is not WizardPage;
    public bool CanNavigateToGatewayInstalledMilestone =>
        !_isClosed &&
        _setupLock is not null &&
        RootFrame.Content is not ProgressPage { IsPipelineRunning: true } &&
        RootFrame.Content is not WizardPage;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public SetupWindow(
        string? configPath = null,
        bool startAtGatewayInstalledMilestone = false,
        string? dataDir = null,
        string? localDataDir = null,
        string? distroNameOverride = null,
        int? gatewayPortOverride = null,
        string[]? commandLineArgs = null)
    {
        _dataDir = dataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = localDataDir ?? SetupContext.ResolveLocalDataDir();
        InitializeComponent();
        Active = this;

        Closed += async (_, _) =>
        {
            _isClosed = true;
            _initialContentReady.TrySetResult(true);
            try
            {
                _lifetimeCts.Cancel();
                if (_contextApplyTask is { } contextApplyTask)
                    await contextApplyTask;
            }
            catch (OperationCanceledException)
            {
                // Window teardown owns this cancellation; cleanup still must finish.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Setup cleanup failed: {ex}");
            }
            finally
            {
                _setupLock?.Dispose();
                _setupLock = null;
                if (ReferenceEquals(Active, this))
                    Active = null;
                _cleanupCompleted.TrySetResult(true);
            }
        };

        // Size window accounting for DPI
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(720 * scale), (int)(820 * scale)));

        // Extend into title bar for modern look
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);

        // Mica backdrop — the signature Windows 11 material (native).
        SystemBackdrop = new MicaBackdrop();

        // Load config: explicit --config arg, or bundled default-config.json (required)
        commandLineArgs ??= Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (!SetupWindowCommandLine.TryParse(
                commandLineArgs,
                out var setupArguments,
                out var argumentError))
        {
            ShowConfigurationError($"Invalid setup arguments: {argumentError}");
            return;
        }

        var explicitConfigPath = configPath ?? setupArguments.ConfigPath;
        configPath = explicitConfigPath;
        if (configPath == null)
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(defaultPath))
                configPath = defaultPath;
            else
            {
                var libraryDefaultPath = Path.Combine(AppContext.BaseDirectory, "OpenClaw.SetupEngine.UI", "default-config.json");
                if (File.Exists(libraryDefaultPath))
                    configPath = libraryDefaultPath;
            }
        }

        if (configPath == null)
        {
            var missingPath = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            ShowConfigurationError(
                $"No setup configuration file was found at '{missingPath}'. " +
                "Place default-config.json next to the executable or pass --config <path>.");
            return;
        }

        if (!SetupConfig.TryLoadFromFile(configPath, out var loadedConfig, out var configError))
        {
            ShowConfigurationError(
                $"The setup configuration file '{configPath}' could not be loaded. {configError}");
            return;
        }

        _config = loadedConfig;
        _config.UsesBundledDefaultConfig = explicitConfigPath == null;
        _config = SetupConfig.FromEnvironment(_config);
        if (!string.IsNullOrWhiteSpace(distroNameOverride))
            _config.DistroName = distroNameOverride;
        if (gatewayPortOverride is > 0 and <= 65535)
        {
            _config.GatewayPort = gatewayPortOverride.Value;
            _config.GatewayUrl = null;
        }
        try
        {
            GatewayReleasePolicy.ResolveAndApply(_config);
        }
        catch (GatewayCompatibilityException ex)
        {
            ShowConfigurationError(ex.Message);
            return;
        }
        _config.ApplyUiDefaults(rollbackOnFailure: setupArguments.RollbackOnFailure);
        if (startAtGatewayInstalledMilestone)
        {
            _persistStartupPreferenceOnComplete = false;
            _showStartupPreferenceOnComplete = false;
        }

        var previewPage = SetupPreview.RequestedPage;
        if (previewPage != null)
        {
            NavigatePreview(previewPage);
            return;
        }

        if (!SetupRunLock.TryAcquire(_dataDir, out _setupLock, out var lockMessage))
        {
            NavigateTo(typeof(CompletePage), new CompletePageArgs(false, TimeSpan.Zero, null, lockMessage ?? "Another setup run is active."));
            return;
        }

        if (startAtGatewayInstalledMilestone)
            NavigateToGatewayInstalledMilestone();
        else
            NavigateTo(typeof(SecurityNoticePage), _config);
    }

    public void NavigateToSecurityNotice(bool back = false) => NavigateTo(typeof(SecurityNoticePage), _config, back);
    public void NavigateToWelcome(bool back = false) => NavigateTo(typeof(WelcomePage), _config, back);
    public bool IsWelcomeInstallSelected => _isWelcomeInstallSelected;
    public void SetWelcomeInstallSelected(bool installSelected) => _isWelcomeInstallSelected = installSelected;

    internal Task<HostHardwareInfo> GetLocalAiHardwareAsync(bool forceRefresh = false)
    {
        lock (_localAiHardwareProbeLock)
        {
            if (forceRefresh ||
                _localAiHardwareProbeTask is null ||
                _localAiHardwareProbeTask.IsFaulted ||
                _localAiHardwareProbeTask.IsCanceled)
            {
                _localAiHardwareProbeTask = Task.Run(() => new CudaHostHardwareProbe().Probe());
            }

            return _localAiHardwareProbeTask;
        }
    }

    internal Task<WslViabilityResult> GetWslViabilityAsync(bool refresh = false) =>
        _wslViabilityProbe.GetAsync(refresh);

    private static async Task<WslViabilityResult> InspectWslViabilityAsync()
    {
        using var logger = new SetupLogger(filePath: null);
        return await WslViabilityInspector.InspectAsync(
            new CommandRunner(logger),
            logger,
            CancellationToken.None);
    }

    public void NavigateToAdvancedSetup() => NavigateTo(typeof(AdvancedSetupPage), _config);
    public void NavigateToCapabilities() => NavigateTo(typeof(CapabilitiesPage), _config);
    public void NavigateToProgress() => NavigateTo(typeof(ProgressPage), CreateProgressPageArgs(showMilestoneOnly: false));
    public void NavigateToGatewayInstalledMilestone() =>
        NavigateTo(typeof(ProgressPage), CreateProgressPageArgs(showMilestoneOnly: true));

    private ProgressPageArgs CreateProgressPageArgs(bool showMilestoneOnly) =>
        new(_config, showMilestoneOnly, _dataDir, _localDataDir);

    public bool TryNavigateToGatewayInstalledMilestone()
    {
        if (!CanNavigateToGatewayInstalledMilestone)
            return false;

        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateToGatewayInstalledMilestone();
        return true;
    }

    public bool TryNavigateToWizard(bool back = false)
    {
        if (!CanNavigateToWizard)
            return false;

        NavigateTo(typeof(WizardPage), _config, back);
        return true;
    }

    internal async Task<StepResult> ApplyWindowsNodeContextAsync()
    {
        if (_contextApplyTask is { } existingTask)
            return await existingTask;

        _contextApplyTask = ApplyWindowsNodeContextCoreAsync();
        try
        {
            return await _contextApplyTask;
        }
        finally
        {
            _contextApplyTask = null;
        }
    }

    private async Task<StepResult> ApplyWindowsNodeContextCoreAsync()
    {
        if (!_config.WindowsNodeContext.Enabled)
            return StepResult.Skip("Windows node context injection disabled");

        var ct = _lifetimeCts.Token;
        using var logger = new SetupLogger(filePath: null);
        using var journal = new TransactionJournal(filePath: null, logger);
        var context = new SetupContext(
            _config,
            logger,
            journal,
            new CommandRunner(logger),
            ct,
            _dataDir,
            _localDataDir);
        // This is an idempotent refresh after onboarding, not a transactional
        // install. A failed refresh must not remove a valid block from an earlier run.
        var pipeline = new SetupPipeline(
            [new WindowsNodeBootstrapContextStep()],
            rollbackOnFailureOverride: false);
        var result = await pipeline.RunAsync(context);
        return result.Outcome switch
        {
            PipelineOutcome.Success => StepResult.Ok("Windows node context injected"),
            PipelineOutcome.Cancelled => StepResult.Fail("Windows node context injection was cancelled"),
            _ => StepResult.Fail(result.Message ?? "Windows node context injection failed")
        };
    }

    public void NavigateToComplete(
        bool success,
        TimeSpan elapsed,
        string? logPath,
        string? errorMessage = null,
        GatewayCompatibilityFailureKind? compatibilityFailure = null,
        LocalAiFailureDetail? detail = null,
        bool restartRequired = false)
    {
        var canRetryFallback =
            compatibilityFailure is { } failureKind &&
            GatewayReleasePolicy.CanRetryWithFallback(_config, failureKind);
        NavigateTo(
            typeof(CompletePage),
            new CompletePageArgs(
                success,
                elapsed,
                logPath,
                errorMessage,
                DefaultAutoStart: true,
                ShowStartupPreference: _showStartupPreferenceOnComplete,
                ReviewSummary: SetupReviewSummaryBuilder.Build(_config, _dataDir, _localDataDir),
                CanRetryGatewayFallback: canRetryFallback,
                GatewayFallbackVersion: canRetryFallback
                    ? GatewayReleasePolicy.FallbackVersion
                    : null,
                Detail: detail)
            {
                RequiresRestart = restartRequired,
            });
    }

    public bool TryRetryWithGatewayFallback(out string? error)
    {
        if (!GatewayReleasePolicy.TryApplyFallback(_config, out error))
            return false;

        NavigateToProgress();
        return true;
    }

    private void ShowConfigurationError(string errorMessage)
    {
        _persistStartupPreferenceOnComplete = false;
        _showStartupPreferenceOnComplete = false;
        NavigateTo(
            typeof(CompletePage),
            new CompletePageArgs(
                Success: false,
                Elapsed: TimeSpan.Zero,
                LogPath: null,
                ErrorMessage: errorMessage,
                ShowStartupPreference: false));
    }

    // Directional page transition: forward steps slide in from the right, Back from the left.
    private void NavigateTo(Type page, object? parameter, bool back = false) =>
        RootFrame.Navigate(page, parameter, new SlideNavigationTransitionInfo
        {
            Effect = back ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight,
        });

    private void NavigatePreview(string page) => RootFrame.Navigate(
        page switch
        {
            "welcome" => typeof(WelcomePage),
            "advanced" => typeof(AdvancedSetupPage),
            "capabilities" => typeof(CapabilitiesPage),
            "capabilities-review" => typeof(CapabilitiesPage),
            "capabilities-review-consent" => typeof(CapabilitiesPage),
            "progress" => typeof(ProgressPage),
            "progress-local-ai" => typeof(ProgressPage),
            "milestone" => typeof(ProgressPage),
            "wizard" => typeof(WizardPage),
            "wizard-error" => typeof(WizardPage),
            "complete" => typeof(CompletePage),
            "complete-error" => typeof(CompletePage),
            _ => typeof(SecurityNoticePage),
        },
        page switch
        {
            "complete" => new CompletePageArgs(
                true,
                TimeSpan.FromMinutes(3),
                null,
                ReviewSummary: SetupReviewSummaryBuilder.Build(_config, _dataDir, _localDataDir)),
            "complete-error" => new CompletePageArgs(false, TimeSpan.FromMinutes(3), null, "Setup could not finish. Review the details, then retry setup when you are ready."),
            "progress" => CreateProgressPageArgs(showMilestoneOnly: false),
            "progress-local-ai" => CreateProgressPageArgs(showMilestoneOnly: false),
            "milestone" => CreateProgressPageArgs(showMilestoneOnly: true),
            _ => _config,
        });

    public void RequestAdvancedSetup()
    {
        AdvancedSetupRequested?.Invoke(this, EventArgs.Empty);
    }

    public bool RequestSetupCompleted(bool enableAutoStart)
    {
        var handler = SetupCompleted;
        if (handler == null)
            return false;

        try
        {
            if (_persistStartupPreferenceOnComplete)
            {
                _config.Settings.AutoStart = enableAutoStart;
                TraySettingsConfig.UpdateAutoStartInSettingsFile(
                    Path.Combine(_dataDir, "settings.json"),
                    enableAutoStart);
            }
        }
        catch (Exception ex)
        {
            NavigateToComplete(false, TimeSpan.Zero, null, $"Setup completed, but saving your startup preference failed: {ex.Message}");
            return true;
        }

        handler.Invoke(this, new SetupCompletedEventArgs(enableAutoStart));
        return true;
    }

    public async Task WaitForInitialContentReadyAsync()
    {
        var completed = await Task.WhenAny(_initialContentReady.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        if (completed == _initialContentReady.Task)
            await _initialContentReady.Task;
        else
            _initialContentReady.TrySetResult(true);
    }

    public void BringToFrontForSetupLaunch()
    {
        Activate();

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        if (presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();

        var wasAlwaysOnTop = presenter.IsAlwaysOnTop;
        presenter.IsAlwaysOnTop = true;
        Activate();

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(750);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!wasAlwaysOnTop && AppWindow.Presenter is OverlappedPresenter p)
                p.IsAlwaysOnTop = false;
        };
        timer.Start();
    }

    private void RootFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement element)
        {
            if (element.IsLoaded)
            {
                CompleteInitialContentReady();
                return;
            }

            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                element.Loaded -= loaded;
                CompleteInitialContentReady();
            };
            element.Loaded += loaded;
            return;
        }

        CompleteInitialContentReady();
    }

    private void RootFrame_NavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        _initialContentReady.TrySetResult(true);
    }

    private void CompleteInitialContentReady()
    {
        RootFrame.Navigated -= RootFrame_Navigated;
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => _initialContentReady.TrySetResult(true));
    }

}

public sealed record CompletePageArgs(
    bool Success,
    TimeSpan Elapsed,
    string? LogPath,
    string? ErrorMessage = null,
    bool DefaultAutoStart = true,
    bool ShowStartupPreference = true,
    SetupReviewSummary? ReviewSummary = null,
    bool CanRetryGatewayFallback = false,
    string? GatewayFallbackVersion = null,
    LocalAiFailureDetail? Detail = null)
{
    public bool RequiresRestart { get; init; }
}
public sealed record SetupCompletedEventArgs(bool EnableAutoStart);
