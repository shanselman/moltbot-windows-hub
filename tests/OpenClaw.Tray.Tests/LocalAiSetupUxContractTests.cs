using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiSetupUxContractTests
{
    [Fact]
    public void WelcomePage_ShowsLocalAiCompatibilityDetails()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "WelcomePage.xaml.cs"));
        Assert.Contains("WelcomeLocalAiAvailable", xaml);
        Assert.Contains("Glyph=\"&#xE73E;\"", xaml);
        Assert.Contains("x:Uid=\"Onboarding_Welcome_LocalAiAvailableBadge\"", xaml);
        Assert.Contains("Local AI supported", xaml);
        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", xaml);
        AssertInOrder(
            xaml,
            "Text=\"Recommended\"",
            "x:Name=\"LocalAiAvailabilityPanel\"",
            "x:Name=\"LocalAiAvailabilityText\"");
        Assert.DoesNotContain("LocalAiAvailabilityBadge", xaml);
        Assert.Contains("SetupLocalization.Format(", source);
        Assert.Contains("\"Onboarding_Welcome_LocalAiAvailabilityDetail\"", source);
        Assert.DoesNotContain("detected. Install a local gateway", source);
        Assert.Contains("AutomationProperties.SetName(", source);
    }

    /// <summary>
    /// The Welcome-page compatibility panel keeps the localized accessible-name announcement
    /// used when Local AI becomes available.
    /// </summary>
    [Fact]
    public void WelcomePage_LocalAiCompatibilityPanel_IsLocalizedInEverySupportedLocale()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        Assert.Contains("x:Name=\"LocalAiAvailabilityPanel\"", xaml);
        Assert.Contains(
            "SetupLocalization.GetString(\"Onboarding_Welcome_LocalAiAvailableBadge.Text\")", source);
        Assert.Contains("AutomationProperties.GetName(InstallChoice)", source);

        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            Assert.Contains("\"Onboarding_Welcome_LocalAiAvailableBadge.Text\"", resources);
            Assert.Contains("\"Onboarding_Welcome_LocalAiAvailabilityDetail\"", resources);
        }
    }

    /// <summary>
    /// The Welcome-page compatibility panel/accessible name must reflect whether this hardware can run
    /// Local AI at all, not whether the currently configured model is still valid. A stale or
    /// removed SelectedModelId must not hide the panel for an otherwise-capable device.
    /// </summary>
    [Fact]
    public void WelcomePage_LocalAiCompatibilityPanel_GatesOnDeviceEligibilityNotSelectedModel()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        string method = ExtractMethod(source, "DetectLocalAiAvailabilityAsync");

        Assert.Contains("LocalInferenceEligibility.Evaluate(hardware);", method);
        Assert.DoesNotContain("config.LocalAi.SelectedModelId", method);
    }

    [Fact]
    public void CapabilitiesReview_SeparatesReasonActionFromDisabledOptions()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        string infoBar = ExtractElement(xaml, "LocalAiUnavailablePanel", "</InfoBar>");
        string networkingInfoBar = ExtractElement(xaml, "LocalAiNetworkingConsentPanel", "</InfoBar>");

        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("Severity=\"Informational\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("Message=\"This PC does not meet one or more Local AI requirements.\"", infoBar);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.DoesNotContain("<StackPanel", infoBar);
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiAvailabilityRecoveryPanel\"", xaml);
        Assert.Contains("x:Name=\"LocalAiAvailabilityProgressRing\"", xaml);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        AssertInOrder(
            xaml,
            "x:Name=\"LocalAiUnavailablePanel\"",
            "x:Name=\"LocalAiUnavailableDetailsButton\"",
            "x:Name=\"LocalAiAvailabilityRecoveryPanel\"",
            "x:Name=\"LocalAiRecheckAvailabilityButton\"",
            "x:Name=\"LocalAiInstallReviewCard\"",
            "x:Name=\"LocalAiOptionContent\"");
        Assert.Contains("LocalAiSetupAvailabilityCoordinator", source);
        Assert.Contains("TryApplyProbeFailure", source);
        Assert.Contains("ShowLocalAiProbeUnknown", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
        Assert.Contains("GetLocalAiHardwareAsync(forceRefresh: refreshHardwareProbe)", source);
        Assert.Contains("CanApplyLocalAiAvailability(checking.Generation, setupWindow)", source);
        AssertInOrder(
            source,
            "HostHardwareInfo hardware = await hardwareTask;",
            "if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))",
            "_localAiHardware = hardware;");
        AssertInOrder(
            source,
            "WslGlobalConfigStatus networkingStatus = forceNetworkingConsent",
            "if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))",
            "_localAiNetworkingStatus = networkingStatus;");
        AssertInOrder(
            source,
            "deviceEligibility = LocalInferenceEligibility.Evaluate(",
            "if (deviceEligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)",
            "TryApplyProbeFailure(",
            "ShowLocalAiProbeUnknown(incompleteSnapshot);");
        Assert.Contains("LocalAiOptionContent.IsHitTestVisible = isAvailable", source);
        Assert.Contains("LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55", source);
        Assert.Contains("LocalAiToggle.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiModelSelector.IsEnabled = isAvailable", source);
        Assert.Contains("LocalAiNetworkingConsentCheckBox.IsEnabled = isAvailable", source);
        Assert.Contains("Title=\"WSL networking change required\"", networkingInfoBar);
        // Enabling Local AI must never imply consent on its own: the user has to
        // affirmatively accept the global .wslconfig rewrite and one-time WSL shutdown.
        Assert.Contains("<CheckBox", networkingInfoBar);
        Assert.Contains("LocalAiNetworkingConsentCheckBox", networkingInfoBar);
        Assert.DoesNotContain("config.LocalAi.WslMirroredNetworkingConsent = config.LocalAi.Enabled", source);
        Assert.DoesNotContain("config.LocalAi.WslMirroredNetworkingConsent = true", source);
        Assert.Contains(
            "config.LocalAi.WslMirroredNetworkingConsent =\r\n            config.LocalAi.Enabled &&\r\n" +
            "            _localAiNetworkingConsentRequired &&\r\n" +
            "            LocalAiNetworkingConsentCheckBox.IsChecked == true;",
            source);
        Assert.Contains("bytes / (1024d * 1024d * 1024d)", source);
        Assert.Contains("GiB", source);
        Assert.Contains("loads on first request", source);
        Assert.DoesNotContain("full CUDA offload", source);
        Assert.DoesNotContain("LocalAiSettingsDetailText", xaml);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: false)", source);
        Assert.Contains("SetLocalAiOptionAvailability(isAvailable: true)", source);
        string checkingMethod = ExtractMethod(source, "private void ShowLocalAiAvailabilityChecking");
        Assert.Contains("LocalAiToggle.IsOn = _config!.LocalAi.Enabled;", checkingMethod);
        Assert.DoesNotContain("_config!.LocalAi.Enabled = false;", checkingMethod);
        Assert.DoesNotContain("_config.SkipWizard = _skipWizardWithoutLocalAi;", checkingMethod);

        // A probe failure is retryable, not definitive: a successful recheck must restore the
        // user's prior Local AI selection instead of a transient failure having cleared it.
        string probeUnknownMethod = ExtractMethod(source, "private void ShowLocalAiProbeUnknown");
        Assert.Contains("LocalAiToggle.IsOn = _config!.LocalAi.Enabled;", probeUnknownMethod);
        Assert.DoesNotContain("_config!.LocalAi.Enabled = false;", probeUnknownMethod);
        Assert.DoesNotContain("_config.SkipWizard = _skipWizardWithoutLocalAi;", probeUnknownMethod);

        Assert.Contains("Text=\"Local AI\"", xaml);
        Assert.Contains(
            "Install Local AI and an optimized model.",
            xaml);
        Assert.DoesNotContain("Local AI on this PC", xaml);
        Assert.DoesNotContain("Downloads begin only after", xaml);
    }

    /// <summary>
    /// CapabilitiesPage.xaml.cs overwrites InstallDistroTitleText/InstallDistroDetailText at
    /// runtime with SetupReviewSummaryBuilder's DistroTitle/DistroDescription
    /// (src/OpenClaw.SetupEngine/SetupReviewSummary.cs), so the XAML values below are only a
    /// design-time placeholder. A prior revision of this PR updated only the XAML text without
    /// updating the builder, so the app kept rendering the old copy despite the source diff
    /// looking correct. This pins the placeholder text so it stays a visible, reviewable
    /// reminder to keep both in sync; SetupConfigTests.SetupReviewSummary_DistroTitleAndDescription_MatchSimplifiedReviewCopy
    /// pins the runtime/builder side to the same literal values.
    /// </summary>
    [Fact]
    public void CapabilitiesReview_InstallDistroCard_UsesSimplifiedCopy()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));

        Assert.Contains(
            "<TextBlock x:Name=\"InstallDistroTitleText\" Text=\"Install Ubuntu 24.04 in WSL\"",
            xaml);
        Assert.Contains("Text=\"Creates a separate OpenClawGateway instance. Uses several GB.\"", xaml);
        Assert.DoesNotContain("Install an isolated", xaml);
        Assert.DoesNotContain("Separate from any Linux distributions you already have", xaml);
    }

    /// <summary>
    /// The "is Local AI unavailable" gate and the recommended/selected model must be decided
    /// from device-level eligibility (the best catalog model this hardware can run), not from
    /// the currently configured SelectedModelId. A stale/removed model, or one that exists but
    /// this hardware cannot run at all, must be reconciled to a valid one instead of making an
    /// otherwise-capable device look unavailable or leaving setup on a known-incompatible model.
    /// A merely busy GPU (EligibleButBusy) is not reconciled away: CanInstall covers that case
    /// and the same model would still work once the GPU frees up.
    /// </summary>
    [Fact]
    public void CapabilitiesReview_GatesOnDeviceEligibilityAndReconcilesStaleSelectedModel()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        // "InitializeLocalAiReviewAsync" also appears at its earlier call site
        // (AsyncEventHandlerGuard.Run(() => InitializeLocalAiReviewAsync(...))), so search for
        // the method's declaration specifically; otherwise ExtractMethod would grab the body of
        // whatever method follows the call site instead of the real one.
        string method = ExtractMethod(source, "private async Task InitializeLocalAiReviewAsync");

        AssertInOrder(
            method,
            "LocalInferenceEligibilityResult deviceEligibility = LocalInferenceEligibility.Evaluate(_localAiHardware);",
            "if (!deviceEligibility.CanInstall || deviceEligibility.Plan is null || deviceEligibility.SelectedGpu is null)",
            "hardwareReason = DescribeLocalAiUnavailable(deviceEligibility);",
            "!LocalInferenceEligibility.Evaluate(_localAiHardware, selectedModelId).CanInstall",
            "_config.LocalAi.SelectedModelId = null;",
            "_config.LocalAi.SelectedModelId ??= _localAiRecommendedModelId ?? deviceEligibility.Plan.Model.Id;",
            "eligibility = LocalInferenceEligibility.Evaluate(",
            "_config.LocalAi.SelectedModelId);");
    }

    [Fact]
    public void CapabilitiesReview_RecheckAffordance_HasLocalizedResourceKeys()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml"));

        Assert.Contains("x:Uid=\"Onboarding_LocalAi_RecheckAvailabilityButton\"", xaml);

        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Strings",
                locale,
                "Resources.resw"));
            Assert.Contains("Onboarding_LocalAi_RecheckAvailabilityButton.Content", resources);
        }
    }

    /// <summary>
    /// The setup page's unavailable/checking/probe-error InfoBar title, message, and action, its
    /// accessibility help text, its probe-failure reason, and its "why unavailable" dialog must
    /// route through SetupLocalization (not hardcoded English) and have matching resw keys in
    /// every supported locale.
    /// </summary>
    [Fact]
    public void CapabilitiesReview_UnavailableAndProbeErrorCopy_IsLocalizedInEverySupportedLocale()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));

        Assert.Contains("x:Uid=\"Onboarding_LocalAi_UnavailableDetailsButton\"", xaml);

        string[] setupResourceCalls =
        [
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_CheckingHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeUnknownHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableMessage\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableHelpText\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_ProbeFailureReason\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableDetailsDialogTitle\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_UnavailableDetailsDialogClose\")",
            "SetupLocalization.GetString(\"Onboarding_LocalAi_WslConfigReadFailureReason\")",
        ];
        foreach (string call in setupResourceCalls)
            Assert.Contains(call, source);

        // The setup page never hardcodes the English copy it used to.
        Assert.DoesNotContain("\"OpenClaw is checking Local AI requirements.\"", source);
        Assert.DoesNotContain(
            "\"OpenClaw could not verify Local AI requirements. Recheck availability to try again.\"", source);
        Assert.DoesNotContain(
            "\"Unavailable because this PC does not meet the Local AI requirements.\"", source);
        Assert.DoesNotContain("\"Why Local AI is unavailable\"", source);
        Assert.DoesNotContain("OpenClaw cannot safely read the global .wslconfig file.", source);

        string[] resourceKeys =
        [
            "Onboarding_LocalAi_UnavailableDetailsButton.Content",
            "Onboarding_LocalAi_CheckingTitle",
            "Onboarding_LocalAi_CheckingMessage",
            "Onboarding_LocalAi_CheckingHelpText",
            "Onboarding_LocalAi_ProbeUnknownTitle",
            "Onboarding_LocalAi_ProbeUnknownMessage",
            "Onboarding_LocalAi_ProbeUnknownHelpText",
            "Onboarding_LocalAi_UnavailableTitle",
            "Onboarding_LocalAi_UnavailableMessage",
            "Onboarding_LocalAi_UnavailableHelpText",
            "Onboarding_LocalAi_ProbeFailureReason",
            "Onboarding_LocalAi_UnavailableDetailsDialogTitle",
            "Onboarding_LocalAi_UnavailableDetailsDialogClose",
        ];
        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            foreach (string key in resourceKeys)
                Assert.Contains($"\"{key}\"", resources);
        }
    }

    /// <summary>
    /// The detailed diagnostic reason (why hardware is unavailable — insufficient GPU memory,
    /// old driver, missing GPU, incomplete facts, etc.) is shared fact-only from
    /// LocalInferenceEligibilityDiagnostics; both the setup page and the Hub page localize it
    /// through their own resource-string helpers, with matching keys in every supported locale.
    /// </summary>
    [Fact]
    public void LocalAiUnavailableReason_IsLocaleNeutralInSharedAndLocalizedInBothUiOwners()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string diagnostics = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Shared", "Inference", "Catalog", "LocalInferenceEligibilityDiagnostics.cs"));
        string setupSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Presentation", "LocalAiPageViewModel.cs"));
        string hubPageSource = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "LocalAiPage.xaml.cs"));

        // Shared stays locale-neutral: facts and a kind enum, no English sentences.
        Assert.Contains("LocalInferenceUnavailableReasonKind", diagnostics);
        Assert.Contains("GetUnavailableReason", diagnostics);
        Assert.DoesNotContain("model weights, KV cache, and runtime workspace", diagnostics);
        Assert.DoesNotContain("No NVIDIA GPU was reported", diagnostics);

        // The Hub ViewModel is source-linked into this test project without a WinUI resource
        // host and must stay free of LocalizationHelper/resource-key literals; it only exposes
        // the locale-neutral reason for the View to format.
        Assert.DoesNotContain("LocalizationHelper", viewModelSource);
        Assert.Contains("LocalInferenceUnavailableReason? LocalAiUnavailableReason", viewModelSource);

        // The View (LocalAiPage.xaml.cs) and the setup page each format the reason locally,
        // through the shared LocalAi_Reason_* keys.
        string[] reasonKeys =
        [
            "LocalAi_Reason_RuntimeUnavailable",
            "LocalAi_Reason_NoNvidiaGpu",
            "LocalAi_Reason_UnknownModel",
            "LocalAi_Reason_HardwareFactsIncomplete",
            "LocalAi_Reason_InsufficientGpuMemory",
            "LocalAi_Reason_DriverTooOld",
            "LocalAi_Reason_CudaCapabilityTooLow",
            "LocalAi_Reason_Generic",
            "LocalAi_Reason_UnknownModelName",
            "LocalAi_Reason_UnknownDriverVersion",
            "LocalAi_Reason_UnknownMemoryAmount",
            "LocalAi_Reason_GigabytesFormat",
        ];
        foreach (string key in reasonKeys)
        {
            Assert.Contains($"\"{key}\"", hubPageSource);
            Assert.Contains($"\"{key}\"", setupSource);
        }

        // A thrown probe failure and a successful-but-incomplete read both resolve to
        // HardwareFactsIncomplete: one shared message, no separate "probe failure" key.
        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            foreach (string key in reasonKeys)
                Assert.Contains($"\"{key}\"", resources);
        }
    }

    [Fact]
    public void SetupWindow_LocalAiHardwareProbeCache_CanRefreshAfterFault()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));
        string method = ExtractMethod(source, "GetLocalAiHardwareAsync");

        Assert.Contains("bool forceRefresh = false", method);
        Assert.Contains("forceRefresh ||", method);
        Assert.Contains("_localAiHardwareProbeTask.IsFaulted", method);
        Assert.Contains("_localAiHardwareProbeTask.IsCanceled", method);
        Assert.Contains("_localAiHardwareProbeTask = Task.Run", method);
    }

    [Fact]
    public void LocalAiPage_InfoBarPrecedesAndDoesNotDisableReasonAction()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "LocalAiPage.xaml.cs"));
        string infoBar = ExtractElement(xaml, "LocalAiUnavailableInfoBar", "</InfoBar>");

        AssertInOrder(
            xaml,
            "<ScrollViewer VerticalScrollBarVisibility=\"Auto\">",
            "<Grid HorizontalAlignment=\"Stretch\">",
            "<StackPanel Padding=\"24\" Spacing=\"12\" HorizontalAlignment=\"Stretch\" MaxWidth=\"900\">",
            "x:Uid=\"LocalAiPage_Intro\"",
            "x:Name=\"LocalAiUnavailableInfoBar\"",
            "x:Name=\"LocalAiEngineOption\"");
        Assert.Contains("Title=\"Local AI is not available\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("<InfoBar.ActionButton>", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiUnavailableDetailsButton\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiRecheckAvailabilityButton\"", xaml);
        Assert.DoesNotContain("Padding=\"0\"", infoBar);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", infoBar);
        Assert.Contains("x:Name=\"LocalAiUnavailableDetailsTip\"", xaml);
        Assert.Contains("Target=\"{x:Bind LocalAiUnavailableDetailsButton}\"", xaml);
        Assert.Contains("x:Name=\"LocalAiEngineOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiModelOption\"", xaml);
        Assert.Contains("x:Name=\"LocalAiGatewayOption\"", xaml);
        Assert.DoesNotContain("SetOptionAvailability(", source);
        Assert.DoesNotContain("option.IsEnabled = isAvailable", source);
        Assert.Contains("ShowAvailabilityInfoBar", source);
        Assert.Contains("CanRecheckAvailability", source);
        Assert.Contains("LocalAiRecheckAvailability_Click", source);
        Assert.Contains("LocalAiUnavailableDetailsTip.IsOpen = !LocalAiUnavailableDetailsTip.IsOpen", source);
    }

    /// <summary>
    /// The Hub InfoBar must distinguish a definitive "device is unavailable" result from a
    /// merely-unverified probe-error/checking state with a different localized title and
    /// severity, and must show explicit checking/recheck progress (a progress ring plus a
    /// "checking" title/message) instead of silently hiding all availability chrome while a
    /// probe is in flight.
    /// </summary>
    [Fact]
    public void LocalAiPage_DistinguishesDefinitiveUnavailableFromCheckingAndProbeUnknown()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "LocalAiPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Pages", "LocalAiPage.xaml.cs"));

        Assert.Contains("x:Name=\"LocalAiAvailabilityProgressRing\"", xaml);

        string refreshMethod = ExtractMethod(source, "private void RefreshFromViewModel");
        Assert.Contains("_viewModel.IsCheckingAvailability", refreshMethod);
        Assert.Contains("LocalizationHelper.GetString(\"LocalAiPage_CheckingTitle\")", refreshMethod);
        Assert.Contains("LocalizationHelper.GetString(\"LocalAiPage_CheckingMessage\")", refreshMethod);
        Assert.Contains("\"LocalAiPage_UnavailableProbeTitle\"", refreshMethod);
        Assert.Contains("\"LocalAiPage_UnavailableInfoBar.Title\"", refreshMethod);
        Assert.Contains("InfoBarSeverity.Warning", refreshMethod);
        Assert.Contains("InfoBarSeverity.Informational", refreshMethod);
        Assert.Contains("LocalAiAvailabilityProgressRing.IsActive = _viewModel.IsCheckingAvailability", refreshMethod);

        string[] resourceKeys =
        [
            "LocalAiPage_CheckingTitle",
            "LocalAiPage_CheckingMessage",
            "LocalAiPage_UnavailableProbeTitle",
        ];
        foreach (string locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            string resources = File.ReadAllText(Path.Combine(
                root, "src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
            foreach (string key in resourceKeys)
                Assert.Contains($"\"{key}\"", resources);
        }
    }

    /// <summary>
    /// The Hub's recheck command must enforce its own <c>CanRecheckAvailability</c> gate (not
    /// just an availability-cancellation-in-flight check), so a caller cannot re-trigger a
    /// probe outside the states the UI itself allows it in.
    /// </summary>
    [Fact]
    public void LocalAiPageViewModel_RecheckAvailability_EnforcesCanRecheckAvailabilityGate()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Presentation", "LocalAiPageViewModel.cs"));
        string method = ExtractMethod(source, "public bool RecheckAvailability");

        Assert.Contains("!IsActive || !CanRecheckAvailability", method);
    }

    /// <summary>
    /// Setup step 3 must never dead-end: while Local AI availability is still pending
    /// (Checking/ProbeUnknown), the toggle must stay interactive even though every other
    /// Local AI control is disabled, so turning Local AI off is always an escape hatch. Continue
    /// itself must never bypass eligibility or an as-yet-undetermined WSL networking-consent
    /// requirement merely because availability hasn't resolved yet — that would let a fast user
    /// leave step 3 before the consent checkbox is even known to be required.
    /// </summary>
    [Fact]
    public void CapabilitiesReview_PendingAvailabilityIsEscapedByToggleNotByBypassingContinue()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "CapabilitiesPage.xaml.cs"));

        // SetLocalAiOptionAvailability(isAvailable: false) sets LocalAiOptionContent's
        // IsHitTestVisible to false, which blocks pointer input for its whole subtree regardless
        // of a descendant's own IsEnabled; the escape hatch must restore hit-testing on that
        // shared container, not just flip the toggle's own IsEnabled back on.
        string restoreMethod = ExtractMethod(source, "private void RestoreLocalAiToggleAsPendingStateEscapeHatch");
        AssertInOrder(
            restoreMethod,
            "LocalAiOptionContent.IsHitTestVisible = true;",
            "LocalAiToggle.IsEnabled = true;");

        string checkingMethod = ExtractMethod(source, "private void ShowLocalAiAvailabilityChecking");
        AssertInOrder(
            checkingMethod,
            "SetLocalAiOptionAvailability(",
            "RestoreLocalAiToggleAsPendingStateEscapeHatch();");

        string probeUnknownMethod = ExtractMethod(source, "private void ShowLocalAiProbeUnknown");
        AssertInOrder(
            probeUnknownMethod,
            "SetLocalAiOptionAvailability(",
            "RestoreLocalAiToggleAsPendingStateEscapeHatch();");

        // Continue's gate must not special-case pending availability: it only ever short-circuits
        // on the toggle being off, or requires a fully resolved, eligible, consented state.
        string primaryButtonMethod = ExtractMethod(source, "private void UpdatePrimaryButtonState");
        Assert.DoesNotContain("_localAiAvailability", primaryButtonMethod);
        Assert.Contains(
            "(!_localAiRecoveryOnly && LocalAiToggle.IsOn != true) ||",
            primaryButtonMethod);
        Assert.Contains(
            "(LocalAiToggle.IsOn == true &&\r\n             _localAiSelectionEligible &&\r\n             (!_localAiNetworkingConsentRequired || LocalAiNetworkingConsentCheckBox.IsChecked == true));",
            primaryButtonMethod);
    }

    /// <summary>
    /// The Welcome page's accessible-name badge suffix must be idempotent: repeated detections
    /// (e.g. the page reloads after navigating back) must rebuild the announcement from a
    /// captured base name instead of appending the suffix again on every call.
    /// </summary>
    [Fact]
    public void WelcomePage_LocalAiBadgeAccessibleName_IsIdempotentAcrossRepeatedDetections()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.SetupEngine.UI", "Pages", "WelcomePage.xaml"));

        Assert.Contains("_installChoiceBaseAutomationName ??= AutomationProperties.GetName(InstallChoice);", source);
        Assert.Contains("FrameworkElementAutomationPeer.FromElement(InstallChoice)", source);
        Assert.Contains("AutomationEvents.LiveRegionChanged", source);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    private static string ExtractElement(string source, string elementName, string closingTag)
    {
        int start = source.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {elementName}.");
        int end = source.IndexOf(closingTag, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the end of {elementName}.");
        return source[start..(end + closingTag.Length)];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        int previous = -1;
        foreach (string value in values)
        {
            int current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{value}' after the previous value.");
            previous = current;
        }
    }

    private static string ExtractMethod(string source, string methodName)
    {
        int nameStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(nameStart >= 0, $"Could not find method {methodName}.");
        int brace = source.IndexOf('{', nameStart);
        Assert.True(brace >= 0, $"Could not find body for method {methodName}.");
        int depth = 0;
        for (int index = brace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[nameStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find end of method {methodName}.");
    }
}
