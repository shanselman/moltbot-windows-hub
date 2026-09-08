using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.SetupEngine.UI;
using System.ComponentModel;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupConfig? _config;
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();
    private readonly Dictionary<string, FrameworkElement> _permRows = new();
    private readonly Dictionary<string, bool> _permGranted = new();
    private SetupWindow? _setupWindow;
    private Task? _permissionsTask;
    private bool _suppressProfile;
    private bool _suppressLocalAiToggle;
    private bool _suppressLocalAiSelection;
    private bool _suppressLocalAiConsent;
    private bool _skipPermissions;
    private bool _skipWizardWithoutLocalAi;
    private bool _localAiSelectionEligible;
    private bool _localAiNetworkingConsentRequired;
    private HostHardwareInfo? _localAiHardware;
    private string? _localAiRecommendedModelId;
    private WslGlobalConfigStatus? _localAiNetworkingStatus;
    private string _localAiUnavailableReason = string.Empty;
    private readonly LocalAiSetupAvailabilityCoordinator _localAiAvailability = new();
    private bool _treatBundledAllOnAsPlaceholder;
    private bool _forceLocalAiNetworkingConsent;
    private CancellationTokenSource? _tailscaleStatusCancellation;
    private int _tailscaleStatusGeneration;
    private int _step = 1;

    // Capability profiles preset only runtime-gated settings. Device info/status
    // stays available whenever Node Mode is enabled, so it is disclosed but not selectable.
    private static readonly string[] ProfileReadOnly = ["Canvas", "Screen"];
    private static readonly string[] ProfileStandard = ["System", "Canvas", "Screen", "Tts", "Stt"];

    // (config property, display name, description, fluent icon glyph)
    private static readonly (string Key, string Name, string Desc, string Glyph)[] Capabilities =
    [
        ("System", "System", "Shell commands, files, clipboard", "\uE756"),
        ("Canvas", "Canvas", "Whiteboard and annotations", "\uE790"),
        ("Screen", "Screen capture", "Screenshots and recording", "\uE7F4"),
        ("Camera", "Camera", "Webcam photos and video", "\uE722"),
        ("Location", "Location", "Share device location", "\uE81D"),
        ("Browser", "Browser", "Web navigation and automation", "\uE774"),
        ("Tts", "Text-to-speech", "Speak text aloud", "\uE767"),
        ("Stt", "Speech-to-text", "Transcribe spoken audio", "\uE720"),
    ];

    // Which capability requires which Windows permission (for the inline step-2 rows).
    private static readonly (string CapKey, string PermId)[] CapPermMap =
    [
        ("Camera", "Camera"),
        ("Stt", "Microphone"),
        ("Location", "Location"),
        ("Screen", "Screen"),
    ];

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        // The tray always registers device.info/status with Node Mode. Keep the
        // setup declaration and gateway allowlist aligned with that runtime contract.
        _config.Capabilities.Device = true;
        _skipPermissions = _config.SkipPermissions;
        _skipWizardWithoutLocalAi = _config.SkipWizard;
        _treatBundledAllOnAsPlaceholder = _config.UsesBundledDefaultConfig;
        BuildToggles();
        _suppressProfile = true;
        var profileIndex = DetectProfileIndex();
        ProfileRadio.SelectedIndex = profileIndex;
        UpdateCapabilityProfilePresentation(profileIndex);
        // BuildToggles() seeded the toggles from the config. The bundled
        // default-config.json still ships with every capability on as a
        // placeholder, so default that implicit case to Standard. Explicit
        // custom configs are preserved even when they do not match a preset.
        if (_config.UsesBundledDefaultConfig && profileIndex == 1 && !MatchesProfile(ProfileStandard))
            ApplyProfile(1);
        _suppressProfile = false;
        _treatBundledAllOnAsPlaceholder = false;
        // Only probe OS permissions when the permissions step will actually be shown.
        if (!_skipPermissions)
            _permissionsTask = BuildPermissionRows();
        _setupWindow = SetupWindow.Active;
        if (_setupWindow is not null)
            _setupWindow.Activated += SetupWindow_Activated;
        TailscaleToggle.IsOn = _config.Tailscale.Enabled;
        TailscaleTrustAuthToggle.IsOn = _config.Tailscale.TrustTailscaleAuth;
        TailscaleAuthModeSelector.SelectedIndex = _config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey ? 1 : 0;
        UpdateTailscaleOptions();
        var previewPage = SetupPreview.RequestedPage;
        var localAiReviewPreview = previewPage is "capabilities-review" or "capabilities-review-consent";
        _forceLocalAiNetworkingConsent = previewPage == "capabilities-review-consent";
        if (localAiReviewPreview)
            _config.LocalAi.Enabled = true;
        AsyncEventHandlerGuard.Run(
            () => InitializeLocalAiReviewAsync(
                forceNetworkingConsent: _forceLocalAiNetworkingConsent),
            NullLogger.Instance,
            nameof(InitializeLocalAiReviewAsync));
        ApplySetupReviewSummary(_config);
        GoToStep(localAiReviewPreview ? 3 : 1);
        if (localAiReviewPreview)
            DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, 0, null, disableAnimation: true));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _localAiAvailability.CancelCurrent();
        CancelTailscaleStatusProbe();
        if (_setupWindow is not null)
        {
            _setupWindow.Activated -= SetupWindow_Activated;
            _setupWindow = null;
        }
        base.OnNavigatedFrom(e);
    }

    private void SetupWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_skipPermissions || e.WindowActivationState == WindowActivationState.Deactivated)
            return;

        // Settings opens outside the setup window. Refresh when focus returns so the
        // status rows and completion summary immediately reflect the user's changes.
        _permissionsTask = RefreshPermissionRowsAsync(_permissionsTask);
    }

    private async Task RefreshPermissionRowsAsync(Task? previousRefresh)
    {
        if (previousRefresh is not null)
            await previousRefresh;
        await BuildPermissionRows();
    }

    // ── Stepped flow (mirrors the gateway onboard transcript) ──

    // The permissions step (internal step 2) is hidden when SetupConfig.SkipPermissions
    // is set, so the flow is 2 visible steps instead of 3. Internal step ids stay 1/2/3;
    // navigation routes around step 2 when it is hidden.

    private void GoToStep(int step)
    {
        _step = step;
        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = step switch
        {
            1 => "What should your agent be able to do?",
            2 => "Windows permissions",
            _ => "What setup will install on this PC",
        };
        PrimaryButton.Content = step == 3 ? "Install & set up" : "Next";
        // Back is always available — from step 1 it returns to the Welcome screen.
        BackButton.Visibility = Visibility.Visible;
        UpdatePrimaryButtonState();

        ScrollActiveIntoView();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            PrimaryClickAsync,
            NullLogger.Instance,
            nameof(Primary_Click));

    private async Task PrimaryClickAsync()
    {
        // The Windows-permission checks run on entry as a background task. They are fast
        // local reads (registry / device enumeration), but make sure they have finished
        // before any step that reads their results — step 2's rows and step 3's summary —
        // so a fast click-through can't render empty rows or an undercounted summary.
        if (_permissionsTask is { } permissionsTask && !permissionsTask.IsCompletedSuccessfully)
        {
            PrimaryButton.IsEnabled = false;
            try { await permissionsTask; }
            finally { PrimaryButton.IsEnabled = true; }
        }

        switch (_step)
        {
            case 1:
                AppendTranscript("What your agent can do", ProfileSummary());
                GoToStep(_skipPermissions ? 3 : 2);
                break;
            case 2:
                AppendTranscript("Windows permissions", PermissionSummary());
                GoToStep(3);
                break;
            default:
                WriteCapabilities();
                SetupWindow.Active?.NavigateToProgress();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1)
        {
            // First capability step — step back to the Welcome screen.
            SetupWindow.Active?.NavigateToWelcome(back: true);
            return;
        }
        if (Transcript.Children.Count > 0)
            Transcript.Children.RemoveAt(Transcript.Children.Count - 1);
        // Skip back over the hidden permissions step when permissions are skipped.
        var previous = _step == 3 && _skipPermissions ? 1 : _step - 1;
        GoToStep(previous);
    }

    private void WriteCapabilities()
    {
        var config = _config!;
        var caps = config.Capabilities;
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (_toggles.TryGetValue(key, out var toggle))
            {
                var prop = typeof(CapabilitiesConfig).GetProperty(key);
                prop?.SetValue(caps, toggle.IsOn);
            }
        }
        config.Settings.ApplyCapabilities(caps);
        config.Tailscale.Enabled = TailscaleToggle.IsOn == true;
        config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true;
        config.Tailscale.AuthMode = TailscaleAuthModeSelector.SelectedIndex == 1
            ? TailscaleAuthMode.AuthKey
            : TailscaleAuthMode.Browser;
        config.Tailscale.AuthKey = config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey
            ? TailscaleAuthKeyBox.Password
            : null;
        config.LocalAi.Enabled = LocalAiToggle.IsOn == true;
        config.SkipWizard = config.LocalAi.Enabled || _skipWizardWithoutLocalAi;
        config.LocalAi.WslMirroredNetworkingConsent =
            config.LocalAi.Enabled &&
            _localAiNetworkingConsentRequired &&
            LocalAiNetworkingConsentCheckBox.IsChecked == true;
    }

    private void ApplySetupReviewSummary(SetupConfig config)
    {
        var summary = SetupReviewSummaryBuilder.Build(
            config,
            SetupWindow.Active?.DataDir,
            SetupWindow.Active?.LocalDataDir);
        InstallDistroTitleText.Text = summary.DistroTitle;
        InstallDistroDetailText.Text = summary.DistroDescription;
        InstallCliDetailText.Text = summary.InstallerDescription;
        InstallCliBadgeText.Text = summary.InstallerBadge;
        GatewayServiceDetailText.Text = summary.GatewayDescription;
        GatewayEndpointText.Text = summary.GatewayEndpoint;
        ExactCommandsText.Text = summary.ExactCommands;
    }

    private async Task InitializeLocalAiReviewAsync(
        bool forceNetworkingConsent,
        bool refreshHardwareProbe = false,
        LocalAiSetupAvailabilitySnapshot? startedAvailability = null)
    {
        LocalAiSetupAvailabilitySnapshot checking =
            startedAvailability ?? _localAiAvailability.StartProbe();
        ShowLocalAiAvailabilityChecking(checking);
        SetupWindow? setupWindow = _setupWindow;
        Task<HostHardwareInfo> hardwareTask = setupWindow is not null
            ? setupWindow.GetLocalAiHardwareAsync(forceRefresh: refreshHardwareProbe)
            : Task.Run(() => new CudaHostHardwareProbe().Probe());

        string? hardwareReason = null;
        LocalInferenceEligibilityResult? eligibility = null;
        try
        {
            HostHardwareInfo hardware = await hardwareTask;
            if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
                return;
            _localAiHardware = hardware;

            // Gate on device-level eligibility (the best catalog model this hardware can run),
            // not the currently configured model. A stale/removed SelectedModelId must not make
            // an otherwise-capable device look unavailable and hide the badge/option; it only
            // means the configured model needs to fall back to a valid one below.
            LocalInferenceEligibilityResult deviceEligibility = LocalInferenceEligibility.Evaluate(_localAiHardware);
            if (deviceEligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            {
                // Incomplete facts (a partial/transient CUDA read) are inconclusive, not a
                // definitive "this device cannot run Local AI". Report it the same way as a
                // thrown probe failure so recheck stays available instead of the option being
                // permanently disabled.
                if (_localAiAvailability.TryApplyProbeFailure(
                        checking.Generation,
                        LocalAiProbeFailureReason,
                        out var incompleteSnapshot))
                {
                    ShowLocalAiProbeUnknown(incompleteSnapshot);
                }
                return;
            }
            _localAiRecommendedModelId = deviceEligibility.CanInstall
                ? deviceEligibility.Plan?.Model.Id
                : null;

            if (!deviceEligibility.CanInstall || deviceEligibility.Plan is null || deviceEligibility.SelectedGpu is null)
            {
                hardwareReason = DescribeLocalAiUnavailable(deviceEligibility);
            }
            else
            {
                // The device can run Local AI. Reconcile the configured model selection: a
                // model that no longer exists in the catalog, or exists but this specific
                // hardware cannot run at all (e.g. the config was moved to a machine with a
                // smaller GPU), falls back to the recommended (or the device-eligible default)
                // model instead of leaving setup stuck on a known-incompatible selection. A
                // merely busy GPU (EligibleButBusy) is not reconciled away: the same model would
                // still work once the GPU frees up, and CanInstall already covers that case.
                if (_config!.LocalAi.SelectedModelId is { } selectedModelId &&
                    !LocalInferenceEligibility.Evaluate(_localAiHardware, selectedModelId).CanInstall)
                {
                    _config.LocalAi.SelectedModelId = null;
                }
                _config.LocalAi.SelectedModelId ??= _localAiRecommendedModelId ?? deviceEligibility.Plan.Model.Id;

                eligibility = LocalInferenceEligibility.Evaluate(
                    _localAiHardware,
                    _config.LocalAi.SelectedModelId);
            }
        }
        catch (Exception ex)
        {
            // Trace.TraceWarning is not compiled out in Release (unlike Debug.WriteLine) and its
            // default listener forwards to OutputDebugString, so this stays visible via
            // DebugView/ETW instead of silently disappearing in a packaged build.
            Trace.TraceWarning($"Local AI hardware probe failed: {ex}");
            if (_localAiAvailability.TryApplyProbeFailure(
                    checking.Generation,
                    LocalAiProbeFailureReason,
                    out var unavailableSnapshot))
            {
                ShowLocalAiProbeUnknown(unavailableSnapshot);
            }
            return;
        }

        string? wslNetworkingReason = null;
        try
        {
            WslGlobalConfigStatus networkingStatus = forceNetworkingConsent
                ? new(false, false)
                : CreateWslGlobalConfigManager().Inspect();
            if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
                return;
            _localAiNetworkingStatus = networkingStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Trace.TraceWarning($"WSL networking inspection failed: {ex}");
            wslNetworkingReason = SetupLocalization.GetString("Onboarding_LocalAi_WslConfigReadFailureReason");
        }

        if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
            return;

        string? unavailableReason = LocalAiAvailabilityReasons.Build(
            hardwareReason,
            wslNetworkingReason);
        if (unavailableReason is not null)
        {
            if (_localAiAvailability.TryApplyUnsupported(
                    checking.Generation,
                    unavailableReason,
                    out var unavailableSnapshot))
            {
                ShowLocalAiUnavailable(unavailableSnapshot);
            }
            return;
        }

        if (!_localAiAvailability.TryApplyAvailable(checking.Generation, out var availableSnapshot))
            return;
        Debug.Assert(eligibility is not null);
        ApplyLocalAiAvailabilityChrome(availableSnapshot);
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        LocalAiToggle.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(isAvailable: true);
        _localAiSelectionEligible = eligibility.Status == LocalInferenceEligibilityStatus.Eligible;
        _config!.LocalAi.SelectedModelId ??= eligibility.Plan!.Model.Id;
        _config.LocalAi.SelectedProfileId = eligibility.Plan!.Profile.Id;
        PopulateLocalAiModels();
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        UpdateLocalAiOptions(forceNetworkingConsent);
        ApplySetupReviewSummary(_config);
    }

    private static string LocalAiProbeFailureReason =>
        SetupLocalization.GetString("Onboarding_LocalAi_ProbeFailureReason");

    private bool CanApplyLocalAiAvailability(int generation, SetupWindow? setupWindow) =>
        _localAiAvailability.IsCurrent(generation) &&
        (_setupWindow is not null || setupWindow is null);

    private static WslGlobalConfigManager CreateWslGlobalConfigManager()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(profile, ".wslconfig");
        var localDataDir = SetupWindow.Active?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        return new WslGlobalConfigManager(
            configPath,
            Path.Combine(localDataDir, "LocalAI", "network-backup"));
    }

    private void ShowLocalAiAvailabilityChecking(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        ApplyLocalAiAvailabilityChrome(snapshot);
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(
            isAvailable: false,
            SetupLocalization.GetString("Onboarding_LocalAi_CheckingHelpText"));
        RestoreLocalAiToggleAsPendingStateEscapeHatch();
        ApplySetupReviewSummary(_config);
        UpdatePrimaryButtonState();
    }

    private void ShowLocalAiProbeUnknown(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        ApplyLocalAiAvailabilityChrome(snapshot);
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(
            isAvailable: false,
            SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownHelpText"));
        RestoreLocalAiToggleAsPendingStateEscapeHatch();
        ApplySetupReviewSummary(_config);
        UpdatePrimaryButtonState();
    }

    /// <summary>
    /// SetLocalAiOptionAvailability(isAvailable: false) sets LocalAiOptionContent.IsHitTestVisible
    /// to false, which suppresses pointer input for its entire subtree regardless of any
    /// descendant's own IsEnabled value; setting LocalAiToggle.IsEnabled back to true alone would
    /// not make it clickable. Availability being merely pending (Checking/ProbeUnknown), not yet a
    /// definitive result, must not remove the user's only way out, so this restores hit-testing on
    /// the shared container and re-enables just the toggle: turning Local AI off unblocks Continue
    /// via the existing LocalAiToggle.IsOn != true branch, instead of ever letting Continue itself
    /// bypass an as-yet-undetermined WSL networking-consent requirement. The other Local AI
    /// controls (model selector, consent checkbox) stay genuinely non-interactive because their
    /// own IsEnabled is still false, independent of the container's hit-testability.
    /// </summary>
    private void RestoreLocalAiToggleAsPendingStateEscapeHatch()
    {
        LocalAiOptionContent.IsHitTestVisible = true;
        LocalAiToggle.IsEnabled = true;
    }

    private void ShowLocalAiUnavailable(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = false;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        ApplyLocalAiAvailabilityChrome(snapshot);
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(isAvailable: false);
        _config!.LocalAi.Enabled = false;
        _config.SkipWizard = _skipWizardWithoutLocalAi;
        ApplySetupReviewSummary(_config);
        UpdatePrimaryButtonState();
    }

    private static string DescribeLocalAiUnavailable(LocalInferenceEligibilityResult eligibility)
    {
        LocalInferenceUnavailableReason reason = LocalInferenceEligibilityDiagnostics.GetUnavailableReason(eligibility);
        return reason.Kind switch
        {
            LocalInferenceUnavailableReasonKind.RuntimeUnavailable =>
                SetupLocalization.GetString("LocalAi_Reason_RuntimeUnavailable"),
            LocalInferenceUnavailableReasonKind.NoNvidiaGpu =>
                SetupLocalization.GetString("LocalAi_Reason_NoNvidiaGpu"),
            LocalInferenceUnavailableReasonKind.UnknownModel =>
                SetupLocalization.GetString("LocalAi_Reason_UnknownModel"),
            LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete =>
                SetupLocalization.GetString("LocalAi_Reason_HardwareFactsIncomplete"),
            LocalInferenceUnavailableReasonKind.InsufficientGpuMemory =>
                SetupLocalization.Format(
                    "LocalAi_Reason_InsufficientGpuMemory",
                    reason.ModelDisplayName ?? SetupLocalization.GetString("LocalAi_Reason_UnknownModelName"),
                    FormatGigabytes(reason.RequiredGigabytes),
                    reason.DetectedGigabytes is { } detected
                        ? FormatGigabytes(detected)
                        : SetupLocalization.GetString("LocalAi_Reason_UnknownMemoryAmount")),
            LocalInferenceUnavailableReasonKind.DriverTooOld =>
                SetupLocalization.Format(
                    "LocalAi_Reason_DriverTooOld",
                    reason.DetectedDriverVersion ?? SetupLocalization.GetString("LocalAi_Reason_UnknownDriverVersion"),
                    reason.MinimumDriverVersion),
            LocalInferenceUnavailableReasonKind.CudaCapabilityTooLow =>
                SetupLocalization.GetString("LocalAi_Reason_CudaCapabilityTooLow"),
            _ => SetupLocalization.GetString("LocalAi_Reason_Generic"),
        };
    }

    private static string FormatGigabytes(double gigabytes) =>
        SetupLocalization.Format("LocalAi_Reason_GigabytesFormat", gigabytes);

    private void ApplyLocalAiAvailabilityChrome(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        _localAiUnavailableReason = snapshot.Reason ?? string.Empty;
        LocalAiUnavailablePanel.Visibility =
            snapshot.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        LocalAiUnavailablePanel.Title = snapshot.Status switch
        {
            LocalAiSetupAvailabilityStatus.Checking =>
                SetupLocalization.GetString("Onboarding_LocalAi_CheckingTitle"),
            LocalAiSetupAvailabilityStatus.Unknown =>
                SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownTitle"),
            _ => SetupLocalization.GetString("Onboarding_LocalAi_UnavailableTitle"),
        };
        LocalAiUnavailablePanel.Message = snapshot.Status switch
        {
            LocalAiSetupAvailabilityStatus.Checking =>
                SetupLocalization.GetString("Onboarding_LocalAi_CheckingMessage"),
            LocalAiSetupAvailabilityStatus.Unknown =>
                SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownMessage"),
            _ => SetupLocalization.GetString("Onboarding_LocalAi_UnavailableMessage"),
        };
        LocalAiUnavailableDetailsButton.Visibility =
            string.IsNullOrWhiteSpace(_localAiUnavailableReason) ? Visibility.Collapsed : Visibility.Visible;
        LocalAiAvailabilityRecoveryPanel.Visibility =
            snapshot.IsChecking || snapshot.IsUnknown ? Visibility.Visible : Visibility.Collapsed;
        LocalAiAvailabilityProgressRing.IsActive = snapshot.IsChecking;
        LocalAiAvailabilityProgressRing.Visibility =
            snapshot.IsChecking ? Visibility.Visible : Visibility.Collapsed;
        LocalAiRecheckAvailabilityButton.Visibility =
            snapshot.IsUnknown ? Visibility.Visible : Visibility.Collapsed;
        LocalAiRecheckAvailabilityButton.IsEnabled = snapshot.CanRecheck;
    }

    private void SetLocalAiOptionAvailability(bool isAvailable, string? helpText = null)
    {
        LocalAiOptionContent.IsHitTestVisible = isAvailable;
        LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55;
        LocalAiToggle.IsEnabled = isAvailable;
        LocalAiModelSelector.IsEnabled = isAvailable;
        LocalAiNetworkingConsentCheckBox.IsEnabled = isAvailable;
        AutomationProperties.SetHelpText(
            LocalAiOptionContent,
            isAvailable
                ? string.Empty
                : helpText ?? SetupLocalization.GetString("Onboarding_LocalAi_UnavailableHelpText"));
    }

    private void LocalAiRecheckAvailability_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RecheckLocalAiAvailabilityAsync,
            NullLogger.Instance,
            nameof(LocalAiRecheckAvailability_Click));

    private Task RecheckLocalAiAvailabilityAsync()
    {
        if (!_localAiAvailability.TryStartRecheck(out var snapshot))
        {
            ApplyLocalAiAvailabilityChrome(snapshot);
            return Task.CompletedTask;
        }

        return InitializeLocalAiReviewAsync(
            forceNetworkingConsent: _forceLocalAiNetworkingConsent,
            refreshHardwareProbe: true,
            startedAvailability: snapshot);
    }

    private void LocalAiUnavailableDetails_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            ShowLocalAiUnavailableDetailsAsync,
            NullLogger.Instance,
            nameof(LocalAiUnavailableDetails_Click));

    private async Task ShowLocalAiUnavailableDetailsAsync()
    {
        var xamlRoot = LocalAiInstallReviewCard.XamlRoot;
        if (xamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = SetupLocalization.GetString("Onboarding_LocalAi_UnavailableDetailsDialogTitle"),
            Content = new TextBlock
            {
                Text = _localAiUnavailableReason,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = SetupLocalization.GetString("Onboarding_LocalAi_UnavailableDetailsDialogClose"),
        };
        await dialog.ShowAsync();
    }

    private void PopulateLocalAiModels()
    {
        _suppressLocalAiSelection = true;
        LocalAiModelSelector.Items.Clear();
        int selectedIndex = 0;
        (LocalModelInfo Model, LocalInferencePlan Plan)[] fittingModels = LocalModelCatalog.Models
            .Select(model => (Model: model, Eligibility: LocalInferenceEligibility.Evaluate(_localAiHardware!, model.Id)))
            .Where(candidate => candidate.Eligibility.CanInstall && candidate.Eligibility.Plan is not null)
            .Select(candidate => (candidate.Model, candidate.Eligibility.Plan!))
            .ToArray();
        for (int index = 0; index < fittingModels.Length; index++)
        {
            (LocalModelInfo model, LocalInferencePlan plan) = fittingModels[index];
            bool isRecommended = string.Equals(
                _localAiRecommendedModelId,
                model.Id,
                StringComparison.OrdinalIgnoreCase);
            LocalAiModelSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{SetupReviewSummaryBuilder.DisplayModelName(model)} " +
                    $"({FormatSize(model.Weights.SizeBytes)}, " +
                    $"{FormatContext(plan.Profile.ContextTokens)}, " +
                    $"{LocalModelCatalog.ToDisplayCacheType(plan.Profile.KeyCachePrecision)} KV)" +
                    (isRecommended ? " (Recommended)" : string.Empty),
                Tag = model.Id,
            });
            string? selectedModelId = _config!.LocalAi.SelectedModelId ?? _localAiRecommendedModelId;
            if (string.Equals(selectedModelId, model.Id, StringComparison.OrdinalIgnoreCase))
                selectedIndex = index;
        }
        LocalAiModelSelector.SelectedIndex = selectedIndex;
        _suppressLocalAiSelection = false;
    }

    private void LocalAiToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLocalAiToggle || _config is null)
            return;
        UpdateLocalAiOptions();
        ApplySetupReviewSummary(_config);
    }

    private void LocalAiModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLocalAiSelection || _config is null ||
            LocalAiModelSelector.SelectedItem is not ComboBoxItem { Tag: string modelId })
        {
            return;
        }
        _config.LocalAi.SelectedModelId = modelId;
        UpdateLocalAiModelDetails();
        ApplySetupReviewSummary(_config);
    }

    private void LocalAiNetworkingConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressLocalAiConsent || _config is null)
            return;
        _config.LocalAi.WslMirroredNetworkingConsent =
            LocalAiToggle.IsOn == true &&
            _localAiNetworkingConsentRequired &&
            LocalAiNetworkingConsentCheckBox.IsChecked == true;
        UpdatePrimaryButtonState();
    }

    private void UpdateLocalAiOptions(bool forceNetworkingConsent = false)
    {
        var config = _config!;
        bool enabled = LocalAiToggle.IsOn == true;
        config.LocalAi.Enabled = enabled;
        config.SkipWizard = enabled || _skipWizardWithoutLocalAi;
        LocalAiDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LocalAiNetworkingInspectionError.Visibility = Visibility.Collapsed;
        _localAiNetworkingConsentRequired = false;

        if (!enabled)
        {
            LocalAiNetworkingConsentPanel.Visibility = Visibility.Collapsed;
            SetLocalAiNetworkingConsent(false);
            config.LocalAi.WslMirroredNetworkingConsent = false;
            UpdatePrimaryButtonState();
            return;
        }

        UpdateLocalAiModelDetails();
        WslGlobalConfigStatus status = forceNetworkingConsent
            ? new(false, false)
            : _localAiNetworkingStatus ?? new(false, false);
        _localAiNetworkingConsentRequired = !status.IsMirrored;
        LocalAiNetworkingConsentPanel.Visibility = _localAiNetworkingConsentRequired
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetLocalAiNetworkingConsent(false);
        config.LocalAi.WslMirroredNetworkingConsent = false;
        UpdatePrimaryButtonState();
    }

    private void UpdateLocalAiModelDetails()
    {
        if (_localAiHardware is null ||
            LocalAiModelSelector.SelectedItem is not ComboBoxItem { Tag: string modelId })
        {
            return;
        }

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(_localAiHardware, modelId);
        if (eligibility.Plan is not { } plan || eligibility.SelectedGpu is not { } gpu)
        {
            _localAiSelectionEligible = false;
            _config!.LocalAi.SelectedProfileId = null;
            LocalAiHardwareStatusText.Text = "This model is not qualified for the detected hardware.";
            UpdatePrimaryButtonState();
            return;
        }

        _localAiSelectionEligible = eligibility.Status == LocalInferenceEligibilityStatus.Eligible;
        _config!.LocalAi.SelectedProfileId = plan.Profile.Id;
        LocalAiHardwareStatusText.Text = eligibility.Status switch
        {
            LocalInferenceEligibilityStatus.Eligible =>
                $"{FormatMemorySize(eligibility.RequiredTotalMemoryBytes)} required · " +
                $"{FormatOptionalMemorySize(eligibility.DetectedTotalMemoryBytes)} CUDA-visible on {gpu.Name}",
            LocalInferenceEligibilityStatus.EligibleButBusy =>
                $"Detected {gpu.Name}, but only {FormatOptionalMemorySize(eligibility.AvailableFreeMemoryBytes)} of " +
                $"{FormatMemorySize(eligibility.RequiredFreeMemoryBytes)} required GPU memory is currently free. " +
                "Close GPU applications and retry setup.",
            _ => DescribeLocalAiUnavailable(eligibility),
        };
        LocalAiEngineDetailText.Text =
            "llama-server for Windows; " +
            $"{FormatSize(plan.Runtime.Artifacts.Sum(artifact => artifact.SizeBytes))} verified download; " +
            "loads on first request";
        LocalAiModelDetailText.Text =
            $"{SetupReviewSummaryBuilder.DisplayModelName(plan.Model)}, " +
            $"{FormatSize(plan.Model.Weights.SizeBytes)} from Hugging Face";
        UpdatePrimaryButtonState();
    }

    private void SetLocalAiNetworkingConsent(bool value)
    {
        _suppressLocalAiConsent = true;
        LocalAiNetworkingConsentCheckBox.IsChecked = value;
        _suppressLocalAiConsent = false;
    }

    private void UpdatePrimaryButtonState()
    {
        // Local AI availability being merely pending (Checking/ProbeUnknown) must never let
        // Continue bypass eligibility or an as-yet-undetermined WSL networking-consent
        // requirement. ShowLocalAiAvailabilityChecking/ShowLocalAiProbeUnknown keep
        // LocalAiToggle itself interactive during those states specifically so the user always
        // has a way out: turning Local AI off satisfies the LocalAiToggle.IsOn != true branch
        // below immediately, without needing Continue to advance on incomplete information.
        PrimaryButton.IsEnabled =
            _step != 3 ||
            LocalAiToggle.IsOn != true ||
            (_localAiSelectionEligible &&
             (!_localAiNetworkingConsentRequired || LocalAiNetworkingConsentCheckBox.IsChecked == true));
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / 1_000_000_000d:0.#} GB";

    private static string FormatMemorySize(long bytes) =>
        $"{bytes / (1024d * 1024d * 1024d):0.#} GiB";

    private static string FormatOptionalMemorySize(long? bytes) =>
        bytes is { } value ? FormatMemorySize(value) : "an unknown amount";

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K"
            : tokens % 1000 == 0
                ? $"{tokens / 1000}K"
                : $"{tokens:N0} tokens";

    private void TailscaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateTailscaleOptions();
        if (_config is not null)
        {
            _config.Tailscale.Enabled = TailscaleToggle.IsOn == true;
            ApplySetupReviewSummary(_config);
        }
    }

    private void TailscaleAuthMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TailscaleAuthKeyBox.Visibility = TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TailscaleTrustAuthToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_config is null)
            return;

        _config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true;
        ApplySetupReviewSummary(_config);
    }

    private void UpdateTailscaleOptions()
    {
        CancelTailscaleStatusProbe();
        var enabled = TailscaleToggle.IsOn == true;
        TailscaleOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TailscaleAuthKeyBox.Visibility = enabled && TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!enabled)
            return;

        var cancellation = new CancellationTokenSource();
        _tailscaleStatusCancellation = cancellation;
        _ = RefreshWindowsTailscaleStatusAsync(
            _tailscaleStatusGeneration,
            cancellation);
    }

    private async Task RefreshWindowsTailscaleStatusAsync(
        int generation,
        CancellationTokenSource cancellation)
    {
        TailscaleStatusText.Text = "Checking Windows Tailscale…";
        try
        {
            var path = PreflightWindowsTailscaleStep.ResolveWindowsTailscaleCliPath();
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");
            psi.ArgumentList.Add("--peers=false");
            var result = await Task.Run(
                () => BoundedProcessOutput.ReadAsync(
                    psi,
                    BoundedProcessOutput.DefaultTimeoutMs,
                    cancellation.Token),
                cancellation.Token);
            if (!IsCurrentTailscaleStatusProbe(generation, cancellation))
                return;

            string? dnsName = null;
            string? tailnetDnsSuffix = null;
            if (result.ExitCode == 0 &&
                TailscaleSetupPolicy.TryParseStatus(result.Output, out var status) &&
                status.IsRunning)
            {
                dnsName = status.DnsName;
                tailnetDnsSuffix = TailscaleSetupPolicy.GetTailnetDnsSuffix(dnsName);
            }
            TailscaleStatusText.Text = tailnetDnsSuffix is not null
                ? $"Windows Tailscale connected as {dnsName}."
                : "Windows Tailscale must be installed and signed in before setup can continue.";
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _config.Tailscale.TailnetDnsSuffix = tailnetDnsSuffix;
                ApplySetupReviewSummary(_config);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (
            ex is Win32Exception or
                IOException or
                InvalidOperationException or
                NotSupportedException or
                AggregateException or
                UnauthorizedAccessException)
        {
            if (!IsCurrentTailscaleStatusProbe(generation, cancellation))
                return;

            Trace.WriteLine(
                $"CapabilitiesPage: Windows Tailscale status probe failed ({ex.GetType().Name}).");
            TailscaleStatusText.Text = "Windows Tailscale must be installed and signed in before setup can continue.";
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _config.Tailscale.TailnetDnsSuffix = null;
                ApplySetupReviewSummary(_config);
            }
        }
        finally
        {
            if (ReferenceEquals(_tailscaleStatusCancellation, cancellation))
                _tailscaleStatusCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelTailscaleStatusProbe()
    {
        _tailscaleStatusGeneration++;
        var cancellation = _tailscaleStatusCancellation;
        _tailscaleStatusCancellation = null;
        cancellation?.Cancel();
    }

    private bool IsCurrentTailscaleStatusProbe(
        int generation,
        CancellationTokenSource cancellation) =>
        generation == _tailscaleStatusGeneration &&
        ReferenceEquals(_tailscaleStatusCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

    private string ProfileSummary()
    {
        if (MatchesProfile(ProfileReadOnly)) return "Read-only";
        if (MatchesProfile(ProfileStandard)) return "Standard";
        if (MatchesProfile(Capabilities.Select(c => c.Key).ToArray())) return "Full access";
        var n = _toggles.Values.Count(t => t.IsOn);
        return $"{n} of {Capabilities.Length} capabilities";
    }

    private string PermissionSummary()
    {
        var visible = 1; // Notifications always shown
        var granted = _permGranted.TryGetValue("Notifications", out var ng) && ng ? 1 : 0;
        foreach (var (capKey, permId) in CapPermMap)
        {
            if (!IsCapOn(capKey))
                continue;
            visible++;
            if (_permGranted.TryGetValue(permId, out var g) && g)
                granted++;
        }
        return granted == visible ? $"All {visible} granted" : $"{granted} of {visible} granted";
    }

    private void AppendTranscript(string question, string? answer)
    {
        var grid = new Grid { Padding = new Thickness(2, 6, 2, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = SetupPermissionHelper.Res("SystemFillColorSuccessBrush"),
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
            },
        };

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontSize = 14,
            Foreground = SetupPermissionHelper.Res("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(answer))
        {
            stack.Children.Add(new TextBlock
            {
                Text = answer,
                FontSize = 13,
                Foreground = SetupPermissionHelper.Res("TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(dot);
        grid.Children.Add(stack);
        Transcript.Children.Add(grid);
    }

    private void ScrollActiveIntoView()
    {
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null);
    }

    // ── Capability toggles ──

    private void BuildToggles()
    {
        var caps = _config!.Capabilities;
        var totalRows = (Capabilities.Length + 1) / 2; // ceiling division for 2 columns

        for (int i = 0; i < totalRows; i++)
            CapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < Capabilities.Length; i++)
        {
            var (key, name, desc, glyph) = Capabilities[i];
            var prop = typeof(CapabilitiesConfig).GetProperty(key);
            var isEnabled = (bool)(prop?.GetValue(caps) ?? true);

            var toggle = new ToggleSwitch
            {
                IsOn = isEnabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
            };
            _toggles[key] = toggle;
            toggle.Toggled += Capability_Toggled;

            var item = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Padding = new Thickness(10, 12, 6, 12),
            };

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.85,
            };

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textStack.Children.Add(new TextBlock { Text = desc, FontSize = 11, Opacity = 0.55 });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(toggle, 2);
            item.Children.Add(icon);
            item.Children.Add(textStack);
            item.Children.Add(toggle);

            int row = i / 2;
            int col = i % 2;
            Grid.SetRow(item, row);
            Grid.SetColumn(item, col);
            CapGrid.Children.Add(item);
        }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfile || _toggles.Count == 0)
            return;

        _suppressProfile = true;
        try
        {
            ApplyProfile(ProfileRadio.SelectedIndex);
            UpdateCapabilityProfilePresentation(ProfileRadio.SelectedIndex);
        }
        finally
        {
            _suppressProfile = false;
        }
    }

    private void Capability_Toggled(object sender, RoutedEventArgs e)
    {
        UpdatePermissionVisibility();
        if (_suppressProfile)
            return;

        var profileIndex = DetectProfileIndex();
        _suppressProfile = true;
        try
        {
            ProfileRadio.SelectedIndex = profileIndex;
            UpdateCapabilityProfilePresentation(profileIndex);
        }
        finally
        {
            _suppressProfile = false;
        }
    }

    private void UpdateCapabilityProfilePresentation(int profileIndex)
    {
        CapabilityExpander.Header = profileIndex < 0
            ? "Custom capabilities (review)"
            : "Fine-tune individual capabilities (optional)";
        if (profileIndex < 0)
            CapabilityExpander.IsExpanded = true;
    }

    // Turns the capability toggles on/off to match a profile index (0=Read-only,
    // 1=Standard, 2=Full access). Shared by the radio handler and the default-on-entry path.
    private void ApplyProfile(int index)
    {
        var on = index switch
        {
            0 => ProfileReadOnly,
            1 => ProfileStandard,
            _ => Capabilities.Select(c => c.Key).ToArray(), // Full access
        };
        var onSet = new HashSet<string>(on);
        foreach (var (key, _, _, _) in Capabilities)
            if (_toggles.TryGetValue(key, out var toggle))
                toggle.IsOn = onSet.Contains(key);
    }

    private int DetectProfileIndex()
    {
        if (MatchesProfile(ProfileReadOnly)) return 0;
        if (MatchesProfile(ProfileStandard)) return 1;
        if (MatchesProfile(Capabilities.Select(c => c.Key).ToArray()))
            return _treatBundledAllOnAsPlaceholder ? 1 : 2;

        // An "all capabilities on" bundled config is the shipped placeholder
        // default, not a deliberate Full-access choice, so new users default to
        // Standard (recommended). Every other non-preset set is explicit and must
        // remain visibly custom, including edits made during bundled setup.
        return -1;
    }

    private bool MatchesProfile(string[] onKeys)
    {
        var onSet = new HashSet<string>(onKeys);
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (!_toggles.TryGetValue(key, out var toggle) || toggle.IsOn != onSet.Contains(key))
                return false;
        }
        return true;
    }

    // ── Windows permissions (merged inline from the old standalone step) ──

    private async Task BuildPermissionRows()
    {
        try
        {
            PermRows.Children.Clear();
            _permRows.Clear();
            _permGranted.Clear();
            foreach (var perm in SetupPermissionHelper.All)
            {
                var (status, granted) = await perm.Check();
                _permGranted[perm.Id] = granted;
                var row = SetupPermissionHelper.BuildRow(perm, status, granted);
                _permRows[perm.Id] = row;
                PermRows.Children.Add(row);
            }
            UpdatePermissionVisibility();
        }
        catch (Exception ex)
        {
            PermRows.Children.Clear();
            _permRows.Clear();
            _permGranted.Clear();
            PermRows.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Title = "Couldn't read Windows permission status",
                Message = $"You can continue setup. Review permissions later in Settings. Details: {ex.Message}",
            });
        }
    }

    private void UpdatePermissionVisibility()
    {
        if (_permRows.Count == 0)
            return;
        foreach (var (capKey, permId) in CapPermMap)
            SetPermVisible(permId, IsCapOn(capKey));
        // Notifications is always visible (app-level, not tied to a capability toggle).
    }

    private bool IsCapOn(string key) => _toggles.TryGetValue(key, out var t) && t.IsOn;

    private void SetPermVisible(string id, bool visible)
    {
        if (_permRows.TryGetValue(id, out var row))
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
