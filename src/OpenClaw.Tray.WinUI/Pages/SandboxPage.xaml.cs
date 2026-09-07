using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OpenClawTray.Pages;

public sealed partial class SandboxPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static string L(string key) => LocalizationHelper.GetString(key);
    private static string Lf(string key, params object?[] args) => LocalizationHelper.Format(key, args);
    private bool _suppress;
    private bool _dialogOpen;

    public ObservableCollection<CustomFolderRow> CustomFolders { get; } = new();

    /// <summary>
    /// Cached MxcAvailability for the lifetime of this page instance, or null until
    /// the first background probe completes. Probe() spawns <c>wxc-exec --probe</c>
    /// (up to ~15s on a misbehaving host) plus filesystem walks, so we never run it
    /// on the UI thread; <see cref="RefreshAvailabilityAsync"/> populates this
    /// off-thread and re-renders. Availability-driven UI treats null as "checking".
    /// </summary>
    private OpenClaw.Shared.Mxc.MxcAvailability? _cachedAvailability;
    private bool _availabilityProbeInFlight;

    /// <summary>
    /// Probe MXC availability off the UI thread (once), cache it, then refresh the
    /// availability-driven UI. No-op once a definitive result is cached or while a
    /// probe is already running.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshAvailabilityAsync()
    {
        // Re-probe when we have no result yet, or the last one was a transient
        // probe error. A definitive verdict (supported / host-unsupported) is kept
        // for the page-instance lifetime.
        if (_cachedAvailability is { ProbeErrored: false }) return;
        if (_availabilityProbeInFlight) return;

        _availabilityProbeInFlight = true;
        try
        {
            _cachedAvailability = await System.Threading.Tasks.Task.Run(
                static () => OpenClaw.Shared.Mxc.MxcAvailability.Probe());
        }
        catch (Exception)
        {
            // Probe() is designed not to throw, but never leave the page stuck in
            // "Checking…": synthesize an errored result so the UI shows Retry.
            _cachedAvailability = new OpenClaw.Shared.Mxc.MxcAvailability(
                false, false, false, null,
                new[] { L("SandboxPage_ProbeErrorReason") }, probeErrored: true);
        }
        finally
        {
            _availabilityProbeInFlight = false;

            // await resumed us on the UI thread (DispatcherQueue sync context), so it
            // is safe to touch controls here. Always re-render — on both the happy
            // path and the failure path — so the page never stays in "Checking…".
            NormalizeSandboxToggleForAvailability();
            UpdateSandboxStatusCard();
            UpdateControlsEnabledState();
        }
    }

    // ── Quick-preset definitions ─────────────────────────────────────
    //
    // Picking a preset writes ALL the values below into SettingsManager
    // in one go (auto-saves like any other change). Users can override any
    // individual control afterward — the cards just highlight whichever
    // preset matches the current state.

    private sealed record SandboxPreset(
        string Tag,
        bool SandboxEnabled,
        bool AllowOutbound,
        bool AllowWindowsUi,
        SandboxFolderAccess? DocumentsAccess,
        SandboxFolderAccess? DownloadsAccess,
        SandboxFolderAccess? DesktopAccess,
        SandboxClipboardMode Clipboard,
        int TimeoutMs,
        long MaxOutputBytes);

    private static readonly SandboxPreset s_lockedDown = new(
        Tag: "LockedDown",
        SandboxEnabled: true,
        AllowOutbound: false,
        AllowWindowsUi: false,
        DocumentsAccess: null,
        DownloadsAccess: null,
        DesktopAccess: null,
        Clipboard: SandboxClipboardMode.None,
        TimeoutMs: 30_000,
        MaxOutputBytes: 4 * 1024 * 1024);

    private static readonly SandboxPreset s_balanced = new(
        Tag: "Balanced",
        SandboxEnabled: true,
        AllowOutbound: true,
        AllowWindowsUi: false,
        DocumentsAccess: SandboxFolderAccess.ReadOnly,
        DownloadsAccess: SandboxFolderAccess.ReadOnly,
        DesktopAccess: SandboxFolderAccess.ReadOnly,
        Clipboard: SandboxClipboardMode.Read,
        TimeoutMs: 60_000,
        MaxOutputBytes: 16 * 1024 * 1024);

    private static readonly SandboxPreset s_permissive = new(
        Tag: "Permissive",
        SandboxEnabled: true,
        AllowOutbound: true,
        AllowWindowsUi: false,
        DocumentsAccess: SandboxFolderAccess.ReadWrite,
        DownloadsAccess: SandboxFolderAccess.ReadWrite,
        DesktopAccess: SandboxFolderAccess.ReadWrite,
        Clipboard: SandboxClipboardMode.Both,
        TimeoutMs: 300_000,
        MaxOutputBytes: 64 * 1024 * 1024);

    public SandboxPage()
    {
        InitializeComponent();
        CustomFoldersList.ItemsSource = CustomFolders;
    }

    public void Initialize()
    {
        LoadState();
        ProbeStatus();
        UpdateControlsEnabledState();

        // Kick off the (potentially slow) MXC probe off the UI thread. The page
        // renders a neutral "checking" state until it completes, then re-renders
        // with the real availability — so the settings UI never blocks on
        // wxc-exec --probe.
        AsyncEventHandlerGuard.Run(
            RefreshAvailabilityAsync,
            new OpenClawTray.AppLogger(),
            nameof(RefreshAvailabilityAsync));
    }

    // ── Load + probe ─────────────────────────────────────────────────

    private void LoadState()
    {
        if (CurrentApp.Settings is not { } settings) return;

        _suppress = true;
        try
        {
            SandboxEnabledToggle.IsOn = settings.SystemRunSandboxEnabled;

            NetInternetToggle.IsOn = settings.SystemRunAllowOutbound;
            WindowsUiToggle.IsOn = settings.SystemRunAllowWindowsUi;

            SelectAccessTag(DocsAccessCombo, settings.SandboxDocumentsAccess);
            SelectAccessTag(DownloadsAccessCombo, settings.SandboxDownloadsAccess);
            SelectAccessTag(DesktopAccessCombo, settings.SandboxDesktopAccess);

            CustomFolders.Clear();
            foreach (var f in settings.SandboxCustomFolders ?? new())
                CustomFolders.Add(new CustomFolderRow(f.Path, f.Access));
            RefreshCustomFoldersUi();

            (settings.SandboxClipboard switch
            {
                SandboxClipboardMode.Read => ClipboardReadRadio,
                SandboxClipboardMode.Write => ClipboardWriteRadio,
                SandboxClipboardMode.Both => ClipboardBothRadio,
                _ => ClipboardNoneRadio,
            }).IsChecked = true;

            var secs = Math.Clamp(settings.SandboxTimeoutMs / 1000, 5, 300);
            TimeoutSlider.Value = secs;
            TimeoutLabel.Text = $"Command timeout: {secs} sec";

            SelectMaxOutputTag(settings.SandboxMaxOutputBytes);
        }
        finally
        {
            _suppress = false;
        }

        NormalizeSandboxToggleForAvailability();
        UpdateWindowsUiWarning();
        UpdatePresetHighlight();
        UpdateSandboxStatusCard();
        UpdateControlsEnabledState();
    }

    /// <summary>
    /// Drives the page header (icon + title + subtext + toggle visibility) based on
    /// MXC availability AND the current sandbox toggle state. Definitively
    /// unavailable MXC is normalized to OFF so the UI never claims Node Sandbox is
    /// on when containment cannot run. Transient probe errors can still render the
    /// enabled/strict-blocking state until retry resolves the probe.
    /// </summary>
    private void UpdateSandboxStatusCard()
    {
        var availability = _cachedAvailability;
        var enabled = SandboxEnabledToggle.IsOn;
        var blockHostFallback = CurrentApp.Settings?.SystemRunBlockHostFallbackWhenMxcUnavailable ?? false;

        UpdateUnavailableActionBar(availability, enabled);

        if (availability is null)
        {
            // Probe still running on the background thread — neutral interim state.
            SandboxStatusIcon.Text = "⏳";
            SandboxStatusTitle.Text = "Checking sandbox availability…";
            SandboxStatusSubtext.Text = "Verifying that this PC can contain commands the agent runs.";
            SandboxEnabledToggle.Visibility = Visibility.Collapsed;
            return;
        }

        var available = availability.CanRunSystemRunSandbox;
        if (!available)
        {
            SandboxStatusIcon.Text = "⚠";
            SandboxEnabledToggle.Visibility = Visibility.Visible;

            if (enabled && blockHostFallback)
            {
                SandboxStatusTitle.Text = L("SandboxPage_StatusUnavailableBlockedTitle");
                SandboxStatusSubtext.Text = L("SandboxPage_StatusUnavailableBlockedSubtext");
            }
            else if (enabled)
            {
                SandboxStatusTitle.Text = L("SandboxPage_StatusUnavailableTitle");
                SandboxStatusSubtext.Text = L("SandboxPage_StatusUnavailableSubtext");
            }
            else
            {
                SandboxStatusTitle.Text = L("SandboxPage_StatusUnavailableOffTitle");
                SandboxStatusSubtext.Text = L("SandboxPage_StatusUnavailableOffSubtext");
            }
            return;
        }

        SandboxEnabledToggle.Visibility = Visibility.Visible;

        if (enabled)
        {
            SandboxStatusIcon.Text = "🛡";
            SandboxStatusTitle.Text = L("SandboxPage_StatusOnTitle");
            SandboxStatusSubtext.Text = L("SandboxPage_StatusOnSubtext");
        }
        else
        {
            SandboxStatusIcon.Text = "⚠";
            SandboxStatusTitle.Text = L("SandboxPage_StatusOffTitle");
            SandboxStatusSubtext.Text = L("SandboxPage_StatusOffSubtext");
        }
    }

    /// <summary>
    /// Shows or hides the prominent "Sandbox unavailable" InfoBar based on availability,
    /// and categorizes the failure mode so we can suggest a relevant action:
    ///   - probe errored (transient) → "Retry"
    ///   - host doesn't support sandboxing → "Open Windows Update"
    ///   - wxc-exec.exe missing → "Show install instructions"
    ///   - Anything else → no primary action, just the learn-more hyperlink
    /// </summary>
    private void UpdateUnavailableActionBar(OpenClaw.Shared.Mxc.MxcAvailability? availability, bool sandboxEnabled)
    {
        // Null = still probing; hide the bar until we have a verdict.
        if (availability is null || availability.CanRunSystemRunSandbox)
        {
            UnavailableActionBar.IsOpen = false;
            return;
        }

        var reasons = availability.SystemRunSandboxUnsupportedReasons;
        var reasonText = reasons.Count > 0
            ? string.Join("  ·  ", reasons)
            : L("SandboxPage_UnavailableDefaultReason");

        // A transient probe error (timeout / couldn't launch / garbled output) is NOT
        // the same as the host being unsupported — don't push the user at Windows
        // Update. Offer a retry; the next probe may succeed.
        var isProbeError = availability.ProbeErrored;

        // wxc-exec is present and the probe gave a definitive negative → the Windows
        // host itself doesn't support the sandbox (vs. a missing binary).
        var isWindowsIssue = !isProbeError
            && availability.IsWxcExecResolvable
            && !availability.IsAppContainerAvailable;

        var isSetupIssue = !availability.ProbeSuppressedBySkuGate
            && !availability.IsWxcExecResolvable;
        var blockHostFallback = sandboxEnabled
            && (CurrentApp.Settings?.SystemRunBlockHostFallbackWhenMxcUnavailable ?? false);
        var unavailableBehavior = L(blockHostFallback
            ? "SandboxPage_UnavailableBehaviorBlocked"
            : "SandboxPage_UnavailableBehaviorHostFallback");

        if (isProbeError)
        {
            UnavailableActionBar.Title = L("SandboxPage_ProbeErrorTitle");
            UnavailableActionMessage.Text = Lf("SandboxPage_ProbeErrorMessageFormat", reasonText, unavailableBehavior);
            UnavailablePrimaryButton.Content = L("SandboxPage_Retry");
            UnavailablePrimaryButton.Tag = "retry";
            UnavailablePrimaryButton.Visibility = Visibility.Visible;
        }
        else if (isWindowsIssue)
        {
            UnavailableActionBar.Title = L("SandboxPage_WindowsUnsupportedTitle");
            UnavailableActionMessage.Text = Lf("SandboxPage_WindowsUnsupportedMessageFormat", reasonText, unavailableBehavior);
            UnavailablePrimaryButton.Content = L("SandboxPage_OpenWindowsUpdate");
            UnavailablePrimaryButton.Tag = "windowsupdate";
            UnavailablePrimaryButton.Visibility = Visibility.Visible;
        }
        else if (isSetupIssue)
        {
            UnavailableActionBar.Title = L("SandboxPage_ComponentsMissingTitle");
            UnavailableActionMessage.Text = Lf("SandboxPage_ComponentsMissingMessageFormat", reasonText, unavailableBehavior);
            UnavailablePrimaryButton.Content = L("SandboxPage_ShowInstallInstructions");
            UnavailablePrimaryButton.Tag = "install";
            UnavailablePrimaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            UnavailableActionBar.Title = blockHostFallback
                ? L("SandboxPage_UnavailableBlockedTitle")
                : L("SandboxPage_UnavailableTitle");
            UnavailableActionMessage.Text = $"{reasonText}\n\n{unavailableBehavior}";
            UnavailablePrimaryButton.Visibility = Visibility.Collapsed;
        }

        UnavailableActionBar.IsOpen = true;
    }

    private bool IsSandboxDefinitivelyUnavailable()
    {
        return _cachedAvailability is
        {
            CanRunSystemRunSandbox: false,
            ProbeErrored: false,
            ProbeSuppressedBySkuGate: false,
        };
    }

    private bool NormalizeSandboxToggleForAvailability()
    {
        if (!IsSandboxDefinitivelyUnavailable())
            return false;
        if (CurrentApp.Settings is not { } settings || !settings.SystemRunSandboxEnabled)
            return false;
        if (settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
            return false;

        _suppress = true;
        try
        {
            settings.SystemRunSandboxEnabled = false;
            SandboxEnabledToggle.IsOn = false;
        }
        finally
        {
            _suppress = false;
        }

        Save();
        return true;
    }

    private void OnUnavailableActionClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnUnavailableActionClickAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnUnavailableActionClick));

    private async Task OnUnavailableActionClickAsync(object sender)
    {
        if (sender is not Button btn) return;
        var tag = btn.Tag as string;

        // Transient probe error → clear the cached verdict and re-probe off-thread.
        if (tag == "retry")
        {
            _cachedAvailability = null;
            UpdateSandboxStatusCard();
            UpdateControlsEnabledState();
            await RefreshAvailabilityAsync();
            return;
        }

        try
        {
            var uri = tag switch
            {
                "windowsupdate" => new Uri("ms-settings:windowsupdate"),
                "install" => new Uri("https://github.com/microsoft/mxc#getting-started"),
                _ => null,
            };
            if (uri != null)
                await global::Windows.System.Launcher.LaunchUriAsync(uri);
        }
        // slopwatch-ignore: SW003 UI helper action is best-effort and failure should not break the owning UI flow.
        catch
        {
            // Best-effort — if the URI handler isn't available we just no-op.
        }
    }

    /// <summary>
    /// Enables/disables sub-sections based on MXC availability AND master toggle state.
    /// When sandbox is off (or MXC unavailable), the entire page below the master
    /// toggle dims — presets and their heading included — so users can't tweak
    /// settings that don't apply.
    /// </summary>
    private void UpdateControlsEnabledState()
    {
        // Null availability = still probing; treat as not-yet-active so the
        // controls stay dimmed until the background probe resolves.
        var available = _cachedAvailability?.CanRunSystemRunSandbox ?? false;
        var sandboxOn = SandboxEnabledToggle.IsOn;
        var active = available && sandboxOn;

        // StackPanel doesn't expose IsEnabled, so we mimic the disabled look manually:
        // block hit-testing (no clicks reach children) and dim opacity for visual cue.
        SandboxControlsContainer.IsHitTestVisible = active;
        SandboxControlsContainer.Opacity = active ? 1.0 : 0.45;

        // Dim the Security-level card (Border wrapping the preset section) as a single
        // visual block — the tinted background and content all fade together so the
        // master-control region reads as a unit when off.
        PresetCard.IsHitTestVisible = active;
        PresetCard.Opacity = active ? 1.0 : 0.45;

        PresetLockedButton.IsEnabled = active;
        PresetBalancedButton.IsEnabled = active;
        PresetPermissiveButton.IsEnabled = active;
    }

    private void ProbeStatus()
    {
        UpdateSandboxStatusCard();
    }

    // ── Presets ──────────────────────────────────────────────────────

    private void ApplyPreset(SandboxPreset preset)
    {
        if (CurrentApp.Settings is not { } s) return;

        _suppress = true;
        try
        {
            s.SystemRunSandboxEnabled = preset.SandboxEnabled;
            s.SystemRunAllowOutbound = preset.AllowOutbound;
            s.SystemRunAllowWindowsUi = preset.AllowWindowsUi;
            s.SandboxDocumentsAccess = preset.DocumentsAccess;
            s.SandboxDownloadsAccess = preset.DownloadsAccess;
            s.SandboxDesktopAccess = preset.DesktopAccess;
            s.SandboxClipboard = preset.Clipboard;
            s.SandboxTimeoutMs = preset.TimeoutMs;
            s.SandboxMaxOutputBytes = preset.MaxOutputBytes;
            // Note: custom folders are NOT touched by presets — users curate them.
        }
        finally
        {
            _suppress = false;
        }

        // Reflect into the UI controls and persist.
        LoadState();
        s.Save();

        // If the user just applied a preset and still has custom folder grants,
        // warn them — they may override the preset's intent (especially Locked Down).
        var customCount = s.SandboxCustomFolders?.Count ?? 0;
        if (customCount > 0)
        {
            var plural = customCount == 1 ? "" : "s";
            PresetCustomFoldersWarning.Message = preset.Tag == s_lockedDown.Tag
                ? $"Locked Down applied, but {customCount} custom folder grant{plural} remain active. Remove them in the Files section below for a fully locked-down sandbox."
                : $"{customCount} custom folder grant{plural} remain active alongside this preset.";
            PresetCustomFoldersWarning.IsOpen = true;
        }
        else
        {
            PresetCustomFoldersWarning.IsOpen = false;
        }
    }

    private void OnPresetLockedClick(object sender, RoutedEventArgs e) => ApplyPreset(s_lockedDown);
    private void OnPresetBalancedClick(object sender, RoutedEventArgs e) => ApplyPreset(s_balanced);
    private void OnPresetPermissiveClick(object sender, RoutedEventArgs e) => ApplyPreset(s_permissive);

    private void UpdatePresetHighlight()
    {
        var s = CurrentApp.Settings;
        var active = s is null ? null : DetectActivePreset(s);
        SetPresetCardActive(PresetLockedButton, active?.Tag == s_lockedDown.Tag);
        SetPresetCardActive(PresetBalancedButton, active?.Tag == s_balanced.Tag);
        SetPresetCardActive(PresetPermissiveButton, active?.Tag == s_permissive.Tag);
    }

    private static void SetPresetCardActive(Button button, bool active)
    {
        // Visual cue for the active preset: accent-themed border, slightly thicker.
        // The default ButtonStyle has visual states for BOTH the background and the
        // border brush (PointerOver / Pressed). Setting button.BorderBrush directly
        // only paints the Normal state — on hover the template animates to
        // ButtonBorderBrushPointerOver (grey), which is what was "stealing" the
        // accent. To stop that we override the per-state resources locally on the
        // button so hover/pressed keep the accent (or no change at all when idle).
        button.BorderThickness = new Thickness(active ? 2 : 1);
        var accent = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
        var defaultStroke = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"];
        button.BorderBrush = active ? accent : defaultStroke;

        if (active)
        {
            var defaultBg = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["ControlFillColorDefaultBrush"];

            // Pin background to default fill for all states (no grey flash).
            button.Resources["ButtonBackground"] = defaultBg;
            button.Resources["ButtonBackgroundPointerOver"] = defaultBg;
            button.Resources["ButtonBackgroundPressed"] = defaultBg;

            // Pin border brush to accent for all states (no grey/dark-stroke flash).
            button.Resources["ButtonBorderBrush"] = accent;
            button.Resources["ButtonBorderBrushPointerOver"] = accent;
            button.Resources["ButtonBorderBrushPressed"] = accent;
        }
        else
        {
            button.Resources.Remove("ButtonBackground");
            button.Resources.Remove("ButtonBackgroundPointerOver");
            button.Resources.Remove("ButtonBackgroundPressed");
            button.Resources.Remove("ButtonBorderBrush");
            button.Resources.Remove("ButtonBorderBrushPointerOver");
            button.Resources.Remove("ButtonBorderBrushPressed");
        }

        // Force the visual state to refresh so the new resources take effect immediately.
        Microsoft.UI.Xaml.VisualStateManager.GoToState(button, "Normal", false);
    }

    private static SandboxPreset? DetectActivePreset(SettingsManager s)
    {
        if (Matches(s, s_lockedDown)) return s_lockedDown;
        if (Matches(s, s_balanced)) return s_balanced;
        if (Matches(s, s_permissive)) return s_permissive;
        return null;
    }

    private static bool Matches(SettingsManager s, SandboxPreset p)
    {
        return s.SystemRunSandboxEnabled == p.SandboxEnabled
            && s.SystemRunAllowOutbound == p.AllowOutbound
            && s.SystemRunAllowWindowsUi == p.AllowWindowsUi
            && s.SandboxDocumentsAccess == p.DocumentsAccess
            && s.SandboxDownloadsAccess == p.DownloadsAccess
            && s.SandboxDesktopAccess == p.DesktopAccess
            && s.SandboxClipboard == p.Clipboard
            && s.SandboxTimeoutMs == p.TimeoutMs
            && s.SandboxMaxOutputBytes == p.MaxOutputBytes;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void SelectAccessTag(ComboBox combo, SandboxFolderAccess? access)
    {
        var tag = access switch
        {
            SandboxFolderAccess.ReadOnly => "ReadOnly",
            SandboxFolderAccess.ReadWrite => "ReadWrite",
            _ => "None",
        };
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
    }

    private void SelectMaxOutputTag(long bytes)
    {
        var match = MaxOutputCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => long.TryParse((string?)i.Tag, out var v) && v == bytes);
        MaxOutputCombo.SelectedItem = match ?? MaxOutputCombo.Items[1]; // default 4 MiB
    }

    private static SandboxFolderAccess? ReadAccessTag(ComboBox combo)
    {
        var tag = (string?)((ComboBoxItem?)combo.SelectedItem)?.Tag;
        return tag switch
        {
            "ReadOnly" => SandboxFolderAccess.ReadOnly,
            "ReadWrite" => SandboxFolderAccess.ReadWrite,
            _ => null,
        };
    }

    private void Save()
    {
        if (_suppress) return;
        CurrentApp.Settings?.Save();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        UpdatePresetHighlight();
    }

    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Event handlers ───────────────────────────────────────────────

    private void OnSandboxEnabledToggled(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnSandboxEnabledToggledAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnSandboxEnabledToggled));

    private async Task OnSandboxEnabledToggledAsync()
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;

        var newValue = SandboxEnabledToggle.IsOn;
        var oldValue = s.SystemRunSandboxEnabled;

        if (newValue
            && !oldValue
            && IsSandboxDefinitivelyUnavailable()
            && !s.SystemRunBlockHostFallbackWhenMxcUnavailable)
        {
            await RejectSandboxEnableWhenUnavailableAsync();
            return;
        }

        // Confirm before turning sandbox OFF — this is the high-risk transition.
        if (!newValue && oldValue)
        {
            // Re-entrancy guard: rapid toggling could otherwise stack ContentDialog
            // instances, which raises a COMException ("Cannot show another dialog
            // until the previous one is dismissed").
            if (_dialogOpen)
            {
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Turn off Node Sandbox?",
                Content = "Agent-started Windows commands will run as you and may access your files, network, clipboard, and OpenClaw settings.\n\nThis is the high-risk mode. Only do this if you trust the agent and need it for debugging or performance.",
                PrimaryButtonText = "Turn off",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            ContentDialogResult result;
            _dialogOpen = true;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another dialog is already open (e.g., system surface popped
                // one underneath us). Treat as cancel — keep sandbox on.
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }
            finally
            {
                _dialogOpen = false;
            }

            if (result != ContentDialogResult.Primary)
            {
                _suppress = true;
                try { SandboxEnabledToggle.IsOn = true; } finally { _suppress = false; }
                return;
            }
        }

        s.SystemRunSandboxEnabled = newValue;
        UpdateSandboxStatusCard();
        UpdateControlsEnabledState();
        Save();
    }

    private async Task RejectSandboxEnableWhenUnavailableAsync()
    {
        _suppress = true;
        try { SandboxEnabledToggle.IsOn = false; }
        finally { _suppress = false; }

        UpdateSandboxStatusCard();
        UpdateControlsEnabledState();

        if (_dialogOpen)
            return;

        var reasonText = _cachedAvailability?.SystemRunSandboxUnsupportedReasons.Count > 0
            ? string.Join("\n", _cachedAvailability.SystemRunSandboxUnsupportedReasons)
            : L("SandboxPage_UnavailableDefaultReason");
        var dialog = new ContentDialog
        {
            Title = "Node Sandbox unavailable",
            Content =
                "Node Sandbox can't be turned on because this PC does not provide MXC BaseContainer without host DACL augmentation.\n\n" +
                $"{reasonText}\n\n" +
                "Agent-started commands will keep using the host execution path until MXC is available.",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open. The toggle has already been restored.
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void OnNetInternetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;
        s.SystemRunAllowOutbound = NetInternetToggle.IsOn;
        Save();
    }

    private void OnWindowsUiToggled(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnWindowsUiToggledAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnWindowsUiToggled));

    private async Task OnWindowsUiToggledAsync()
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;

        var newValue = WindowsUiToggle.IsOn;
        var oldValue = s.SystemRunAllowWindowsUi;

        if (newValue && !oldValue)
        {
            if (_dialogOpen)
            {
                RestoreWindowsUiToggle(oldValue);
                return;
            }

            var dialog = new ContentDialog
            {
                Title = L("SandboxPage_AllowWindowsUiDialogTitle"),
                Content = L("SandboxPage_AllowWindowsUiDialogContent"),
                PrimaryButtonText = L("SandboxPage_AllowWindowsUiDialogPrimary"),
                CloseButtonText = L("SandboxPage_AllowWindowsUiDialogCancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            ContentDialogResult result;
            _dialogOpen = true;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                RestoreWindowsUiToggle(oldValue);
                return;
            }
            finally
            {
                _dialogOpen = false;
            }

            if (result != ContentDialogResult.Primary)
            {
                RestoreWindowsUiToggle(oldValue);
                return;
            }
        }

        s.SystemRunAllowWindowsUi = newValue;
        UpdateWindowsUiWarning();
        Save();
    }

    private void RestoreWindowsUiToggle(bool value)
    {
        _suppress = true;
        try { WindowsUiToggle.IsOn = value; }
        finally { _suppress = false; }
        UpdateWindowsUiWarning();
    }

    private void UpdateWindowsUiWarning() =>
        WindowsUiWarningBar.IsOpen = WindowsUiToggle.IsOn;

    private void OnDocsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;
        s.SandboxDocumentsAccess = ReadAccessTag(DocsAccessCombo);
        Save();
    }

    private void OnDownloadsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;
        s.SandboxDownloadsAccess = ReadAccessTag(DownloadsAccessCombo);
        Save();
    }

    private void OnDesktopAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;
        s.SandboxDesktopAccess = ReadAccessTag(DesktopAccessCombo);
        Save();
    }

    private void OnAddCustomFolder(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => PickCustomFolderAsync(SandboxFolderAccess.ReadOnly),
            new OpenClawTray.AppLogger(),
            nameof(OnAddCustomFolder));

    private async System.Threading.Tasks.Task PickCustomFolderAsync(SandboxFolderAccess access)
    {
        var window = CurrentApp.ActiveHubWindow;
        if (window is null) return;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrWhiteSpace(folder.Path)) return;
        if (CustomFolders.Any(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase))) return;

        var row = new CustomFolderRow(folder.Path, access);
        // Mark "initial" as already fired — when the ListView materializes this row's
        // ComboBox and its SelectedIndex binding fires SelectionChanged, we want to
        // skip processing (this isn't a user-driven change, the settings entry was
        // already added below).
        row.InitialSelectionFired = true;
        CustomFolders.Add(row);
        RefreshCustomFoldersUi();

        if (CurrentApp.Settings is { } s)
        {
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = folder.Path, Access = access });
            Save();
        }
    }

    private void OnCustomFolderAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not ComboBox combo) return;
        if (combo.DataContext is not CustomFolderRow row) return;

        // The ComboBox's SelectedIndex binding fires SelectionChanged once on
        // initial materialization. Skip that "fake" change — only act on real
        // user-driven changes.
        if (!row.InitialSelectionFired)
        {
            row.InitialSelectionFired = true;
            return;
        }

        if (CurrentApp.Settings is not { } s) return;

        var newIndex = combo.SelectedIndex;
        var newAccess = newIndex switch
        {
            1 => (SandboxFolderAccess?)SandboxFolderAccess.ReadOnly,
            2 => (SandboxFolderAccess?)SandboxFolderAccess.ReadWrite,
            _ => null, // 0 = Blocked → remove the grant
        };

        var existing = s.SandboxCustomFolders.FirstOrDefault(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));

        if (newAccess is null)
        {
            // User picked "Blocked" — drop the grant entirely.
            if (existing != null) s.SandboxCustomFolders.Remove(existing);
            var rowToRemove = CustomFolders.FirstOrDefault(f =>
                string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
            if (rowToRemove != null) CustomFolders.Remove(rowToRemove);
            RefreshCustomFoldersUi();
        }
        else if (existing != null)
        {
            existing.Access = newAccess.Value;
        }
        else
        {
            // Defensive: row exists in UI but not in settings (shouldn't happen).
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = row.Path, Access = newAccess.Value });
        }

        row.AccessIndex = newIndex;
        Save();
    }

    private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        var row = CustomFolders.FirstOrDefault(f => f.Path == path);
        if (row != null) CustomFolders.Remove(row);
        RefreshCustomFoldersUi();

        if (CurrentApp.Settings is { } s)
        {
            s.SandboxCustomFolders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private void OnClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (CurrentApp.Settings is not { } s) return;
        s.SandboxClipboard = tag switch
        {
            "Read" => SandboxClipboardMode.Read,
            "Write" => SandboxClipboardMode.Write,
            "Both" => SandboxClipboardMode.Both,
            _ => SandboxClipboardMode.None,
        };
        Save();
    }

    private void OnTimeoutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        var secs = (int)Math.Round(e.NewValue);
        TimeoutLabel.Text = $"Max time per command: {secs} sec";
        if (CurrentApp.Settings is not { } s) return;
        s.SandboxTimeoutMs = secs * 1000;
        Save();
    }

    private void OnMaxOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (CurrentApp.Settings is not { } s) return;
        var tag = (string?)((ComboBoxItem?)MaxOutputCombo.SelectedItem)?.Tag;
        if (tag != null && long.TryParse(tag, out var bytes))
        {
            s.SandboxMaxOutputBytes = bytes;
            Save();
        }
    }

    public sealed class CustomFolderRow
    {
        public string Path { get; }
        public int AccessIndex { get; set; }

        /// <summary>
        /// Tracks whether the ComboBox's SelectedIndex-binding has already fired
        /// its initial SelectionChanged event during materialization. Used by
        /// OnCustomFolderAccessChanged to skip that "fake" change.
        /// </summary>
        public bool InitialSelectionFired { get; set; }

        public CustomFolderRow(string path, SandboxFolderAccess access)
        {
            Path = path;
            AccessIndex = access switch
            {
                SandboxFolderAccess.ReadWrite => 2,
                SandboxFolderAccess.ReadOnly => 1,
                _ => 0,
            };
        }
    }
}
