using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using System.Numerics;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WelcomePage : Page
{
    private SetupConfig? _config;
    private bool _installSelected = true; // default selection
    private bool _suppressSelectionWrite;
    private string? _installChoiceBaseAutomationName;

    public WelcomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _installSelected = SetupWindow.Active?.IsWelcomeInstallSelected ?? true;
        _suppressSelectionWrite = true;
        try
        {
            GatewayChoiceSelector.SelectedIndex = _installSelected ? 0 : 1;
        }
        finally
        {
            _suppressSelectionWrite = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartMascotBreatheAnimation();
        AsyncEventHandlerGuard.Run(
            DetectLocalAiAvailabilityAsync,
            NullLogger.Instance,
            nameof(DetectLocalAiAvailabilityAsync));
    }

    private async Task DetectLocalAiAvailabilityAsync()
    {
        SetupWindow? setupWindow = SetupWindow.Active;
        SetupConfig? config = _config;
        if (setupWindow is null || config is null)
            return;

        WslViabilityResult wslViability = await setupWindow.GetWslViabilityAsync();
        if (!IsLoaded || !ReferenceEquals(SetupWindow.Active, setupWindow))
            return;
        if (wslViability.BlocksSetup)
            return;

        var hardware = await setupWindow.GetLocalAiHardwareAsync();
        if (!IsLoaded || !ReferenceEquals(SetupWindow.Active, setupWindow))
            return;

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
        if (!eligibility.CanInstall || eligibility.SelectedGpu is null)
            return;

        LocalAiAvailabilityText.Text = SetupLocalization.Format(
            "Onboarding_Welcome_LocalAiAvailabilityDetail",
            eligibility.SelectedGpu.Name);
        LocalAiAvailabilityPanel.Visibility = Visibility.Visible;
        // Capture the control's base accessible name once, so repeated detections (e.g. the
        // page is re-loaded after navigating back) rebuild the announcement from the same
        // starting point instead of appending the availability suffix again on every call.
        _installChoiceBaseAutomationName ??= AutomationProperties.GetName(InstallChoice);
        AutomationProperties.SetName(
            InstallChoice,
            $"{_installChoiceBaseAutomationName}, " +
            $"{SetupLocalization.GetString("Onboarding_Welcome_LocalAiAvailableBadge.Text")}");
        // FromElement returns null until a screen reader (or other AT client) has already
        // queried this element for a peer. This probe can complete before that happens, so the
        // live-region announcement would otherwise be silently skipped; force peer creation so
        // the event always has somewhere to go.
        AutomationPeer automationPeer = FrameworkElementAutomationPeer.FromElement(InstallChoice)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(InstallChoice);
        automationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void StartMascotBreatheAnimation()
    {
        var visual = ElementCompositionPreview.GetElementVisual(MascotHero);
        var compositor = visual.Compositor;
        var centerX = MascotHero.ActualWidth > 0 ? MascotHero.ActualWidth / 2 : MascotHero.Width / 2;
        var centerY = MascotHero.ActualHeight > 0 ? MascotHero.ActualHeight / 2 : MascotHero.Height / 2;
        visual.CenterPoint = new Vector3((float)centerX, (float)centerY, 0f);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        pulse.InsertKeyFrame(0.5f, new Vector3(1.025f, 1.025f, 1f));
        pulse.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        pulse.Duration = TimeSpan.FromMilliseconds(4200);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", pulse);
    }

    private void GatewayChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A single-select ListView can be cleared to no selection (Ctrl+click / automation).
        // The Welcome choice must always have exactly one option selected, so restore the last
        // known selection instead of leaving the persisted value stale behind an empty list.
        if (GatewayChoiceSelector.SelectedIndex is not (0 or 1))
        {
            _suppressSelectionWrite = true;
            try
            {
                GatewayChoiceSelector.SelectedIndex = _installSelected ? 0 : 1;
            }
            finally
            {
                _suppressSelectionWrite = false;
            }

            return;
        }

        if (!_suppressSelectionWrite)
            SetInstallSelected(GatewayChoiceSelector.SelectedIndex == 0);
    }

    private void SetInstallSelected(bool installSelected)
    {
        _installSelected = installSelected;
        SetupWindow.Active?.SetWelcomeInstallSelected(installSelected);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToSecurityNotice(back: true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_installSelected)
        {
            AsyncEventHandlerGuard.Run(
                StartInstallAsync,
                NullLogger.Instance,
                nameof(Next_Click));
        }
        else
        {
            SetupWindow.Active?.NavigateToAdvancedSetup();
        }
    }

    private async Task StartInstallAsync()
    {
        var config = _config ?? throw new InvalidOperationException("Setup configuration has not been loaded.");
        var setupWindow = SetupWindow.Active;
        if (setupWindow is null)
            return;

        var dataDir = setupWindow.DataDir;

        // The progress ring carries the checking state (its automation name is
        // "Checking existing WSL setup"). Leave the option title alone: replacing it
        // hides which option is being acted on for as long as the check runs.
        NextButton.IsEnabled = false;
        InstallCheckProgress.IsActive = true;
        InstallCheckProgress.Visibility = Visibility.Visible;
        var navigating = false;
        try
        {
            while (true)
            {
                WslViabilityResult wslViability =
                    await setupWindow.GetWslViabilityAsync(refresh: true);
                if (wslViability.BlocksSetup)
                {
                    var readinessRoot = XamlRoot;
                    if (setupWindow.IsClosed || readinessRoot is null)
                        return;

                    var retry = await new ContentDialog
                    {
                        Title = "WSL2 is not ready",
                        Content = wslViability.Description,
                        PrimaryButtonText = "Try again",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = readinessRoot,
                    }.ShowAsync();

                    if (retry != ContentDialogResult.Primary)
                        return;
                    continue;
                }

                break;
            }

            ExistingConfigDetector.ExistingConfig existing;
            while (true)
            {
                try
                {
                    existing = await Task.Run(() => ExistingConfigDetector.Detect(
                        dataDir,
                        config.DistroName,
                        setupWindow.LocalDataDir));
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    var errorRoot = XamlRoot;
                    if (setupWindow.IsClosed || errorRoot is null)
                        return;

                    // Inspection failure is usually transient, so offer a way forward
                    // instead of ending the flow on the recommended option.
                    var retry = await new ContentDialog
                    {
                        Title = "Could not inspect WSL",
                        Content = ex.Message,
                        PrimaryButtonText = "Try again",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = errorRoot,
                    }.ShowAsync();

                    if (retry != ContentDialogResult.Primary)
                        return;
                }
            }

            var xamlRoot = XamlRoot;
            if (setupWindow.IsClosed || xamlRoot is null)
                return;

            InstallCheckProgress.IsActive = false;
            InstallCheckProgress.Visibility = Visibility.Collapsed;
            var summary = ExistingConfigDetector.BuildReplacementSummary(existing);
            var requiresDestructiveConfirmation =
                ExistingConfigDetector.RequiresDestructiveConfirmation(existing);

            var dialog = new ContentDialog
            {
                Title = requiresDestructiveConfirmation
                    ? $"Permanently delete WSL distro '{config.DistroName}'?"
                    : existing.HasLocalGateway || existing.HasDistro || existing.HasDistroDataDirectory
                        ? "Replace existing WSL gateway?"
                        : "Install a new WSL gateway?",
                Content = summary,
                PrimaryButtonText = requiresDestructiveConfirmation
                    ? "Delete and replace"
                    : "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            config.ConfirmedDestructiveDistroName = requiresDestructiveConfirmation
                ? config.DistroName
                : null;

            navigating = true;
            setupWindow.NavigateToCapabilities();
        }
        finally
        {
            if (!navigating && !setupWindow.IsClosed)
            {
                InstallCheckProgress.IsActive = false;
                InstallCheckProgress.Visibility = Visibility.Collapsed;
                NextButton.IsEnabled = true;
            }
        }
    }
}
