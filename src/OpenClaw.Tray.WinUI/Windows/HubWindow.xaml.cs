using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using OpenClawTray.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class HubWindow : WindowEx
{
    public bool IsClosed { get; private set; }

    private static App CurrentApp => (App)Application.Current;
    private static TaskCompletionSource<bool> CreateCompletedContentReady()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ready.SetResult(true);
        return ready;
    }

    internal AppState? AppModel { get; set; }
    private string _currentAgentId = "main";
    public string CurrentAgentId => _currentAgentId;
    private TaskCompletionSource<bool> _contentReady = CreateCompletedContentReady();
    private AppNotificationService? _appNotificationService;
    private readonly AppNotificationInfoBarPresenter _appNotificationInfoBarPresenter = new();
    private AppNotificationSnapshot? _lastAppNotificationSnapshot;
    private AppNotificationInfoBarPresentation _currentAppNotificationPresentation =
        AppNotificationInfoBarPresentation.Hidden;
    private SettingsWriteOrigin? _commandPaletteSettingsOrigin;
    private bool _suppressAppNotificationClosed;

    private readonly ObservableCollection<NotificationItemViewModel> _bellItems = new();
    private bool _bellListBound;

    // Legacy compatibility alias
    public string SelectedAgentId => _currentAgentId;
    public Action<string?>? OpenDashboardAction { get; set; }
    public Action? CheckForUpdatesAction { get; set; }
    public Action? ConnectAction { get; set; }
    public Action? DisconnectAction { get; set; }
    public Action? ReconnectAction { get; set; }
    public Action? OpenSetupAction { get; set; }
    public Action? OpenConnectionStatusAction { get; set; }
    public Action? OpenVoiceAction { get; set; }
    public OpenClaw.Connection.IGatewayConnectionManager? ConnectionManager { get; set; }
    public OpenClaw.Connection.GatewayRegistry? GatewayRegistry { get; set; }

    // Node service state (set by App.xaml.cs in ShowHub)
    public bool NodeIsConnected { get; set; }
    public bool NodeIsPaired { get; set; }
    public bool NodeIsPendingApproval { get; set; }
    public string? LastAuthError { get; set; }
    public string? NodeShortDeviceId { get; set; }
    public VoiceService? VoiceServiceInstance { get; set; }
    /// <summary>When true, ChatPage should auto-start voice recording on next navigation. Consumed (reset to false) by ChatPage.</summary>
    public bool PendingAutoStartVoice { get; set; }
    /// <summary>Session key the chat surface should select on its next mount. Consumed (cleared) by ChatPage.</summary>
    public string? PendingChatSessionKey { get; set; }
    public string? NodeFullDeviceId { get; set; }
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gatewayNavHideTimer;

    // Cached gateway data — pages read these on navigation
    public SessionInfo[]? LastSessions { get; private set; }
    public ChannelHealth[]? LastChannels { get; private set; }
    public GatewayUsageInfo? LastUsage { get; private set; }
    public GatewayCostUsageInfo? LastUsageCost { get; private set; }
    public GatewayUsageStatusInfo? LastUsageStatus { get; private set; }
    public GatewayNodeInfo[]? LastNodes { get; private set; }

    public System.Text.Json.JsonElement? LastConfig { get; private set; }
    public System.Text.Json.JsonElement? LastConfigSchema { get; private set; }
    public System.Text.Json.JsonElement? LastSkillsData { get; private set; }
    public string? LastSkillsAgentId { get; private set; }
    public System.Text.Json.JsonElement? LastAgentFilesList { get; private set; }
    public string? LastAgentFilesListAgentId { get; private set; }


    // Event for settings saved (App.xaml.cs subscribes)
    public event EventHandler? SettingsSaved;

    public void RaiseSettingsSaved() => SettingsSaved?.Invoke(this, EventArgs.Empty);

    public HubWindow()
    {
        InitializeComponent();
        Title = AppIdentity.DisplayName;
        RefreshDiagnosticsNavVisibility();
        ApplyHighContrastFallbackIfNeeded();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Closed += (s, e) =>
        {
            IsClosed = true;
            _contentReady.TrySetResult(true);
            _gatewayNavHideTimer?.Stop();
            if (_appNotificationService != null)
                _appNotificationService.Changed -= OnAppNotificationChanged;
            if (AppModel != null)
                AppModel.PropertyChanged -= OnAppModelChanged;
        };

        this.SetWindowSize(1000, 650);
        this.CenterOnScreen();
        ApplyWindowStatusIcon(ConnectionStatusAccent.Neutral);

        RootGrid.SizeChanged += OnRootGridSizeChanged;

        ToolTipService.SetToolTip(StatusPillButton, LocalizationHelper.GetString("HubWindow_StatusPill_Tooltip"));
        ToolTipService.SetToolTip(NotificationsBellButton, LocalizationHelper.GetString("HubWindow_Bell_Tooltip"));
    }

    public void RefreshDiagnosticsNavVisibility()
    {
        var isVisible = DiagnosticsGate.IsVisible;
        NavDiagnostics.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (isVisible)
            return;

        RemoveBackStackEntries("debug");
        if (string.Equals(_currentNavTag, "debug", StringComparison.Ordinal))
        {
            NavigateInternal("settings");
            RemoveBackStackEntries("debug");
            UpdateBackButton();
        }
        else
        {
            UpdateBackButton();
        }
    }

    /// <summary>
    /// Subscribe to AppState property changes for title bar and nav updates.
    /// Called from App.ShowHub() after setting AppModel.
    /// </summary>
    internal void BindToAppState()
    {
        if (AppModel != null)
        {
            AppModel.PropertyChanged += OnAppModelChanged;
            UpdateTitleBarStatus(AppModel.Status);
            ScheduleGatewayNavVisibilityForStatus(AppModel.Status, debounceDisconnected: false);

            // Apply agents list that may have arrived before this window opened.
            if (AppModel.AgentsList.HasValue)
                RebuildAgentNavItems(AppModel.AgentsList.Value);
        }
    }

    internal void BindAppNotifications(AppNotificationService service)
    {
        if (_appNotificationService != null)
            _appNotificationService.Changed -= OnAppNotificationChanged;

        _appNotificationService = service;
        _appNotificationService.Changed += OnAppNotificationChanged;
        RenderAppNotification(service.Snapshot);
    }

    private void OnAppNotificationChanged(object? sender, AppNotificationChangedEventArgs args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (IsClosed) return;
            RenderAppNotification(args.Snapshot);
        });
    }

    private void RenderAppNotification(AppNotificationSnapshot snapshot)
    {
        _lastAppNotificationSnapshot = snapshot;

        UpdateNotificationsBell(snapshot);

        _currentAppNotificationPresentation = _appNotificationInfoBarPresenter.Present(
            snapshot,
            _currentAppNotificationPresentation.Notification?.Id,
            AppNotificationInfoBar.IsOpen,
            _currentNavTag,
            LocalizationHelper.GetString("AppNotification_ShowMore"));
        if (!_currentAppNotificationPresentation.IsVisible)
        {
            HideAppNotificationInfoBar();
            return;
        }

        var notification = _currentAppNotificationPresentation.Notification!;
        AppNotificationInfoBar.Visibility = Visibility.Visible;
        AppNotificationInfoBar.Severity = ToInfoBarSeverity(_currentAppNotificationPresentation.Severity);
        AppNotificationInfoBar.Title = notification.Title;
        AppNotificationInfoBar.Message = notification.Message;

        var action = _currentAppNotificationPresentation.Action;
        if (action.Kind != AppNotificationInfoBarActionKind.None)
        {
            AppNotificationActionButton.Content = action.Label;
            AppNotificationActionButton.Visibility = Visibility.Visible;
            AppNotificationActionButton.IsEnabled = action.IsEnabled;
        }
        else
        {
            AppNotificationActionButton.Visibility = Visibility.Collapsed;
            AppNotificationActionButton.IsEnabled = true;
        }

        AppNotificationInfoBar.IsOpen = true;
    }

    private void HideAppNotificationInfoBar()
    {
        _suppressAppNotificationClosed = true;
        AppNotificationInfoBar.IsOpen = false;
        AppNotificationInfoBar.Visibility = Visibility.Collapsed;
        AppNotificationInfoBar.Title = string.Empty;
        AppNotificationInfoBar.Message = string.Empty;
        AppNotificationActionButton.Visibility = Visibility.Collapsed;
        _currentAppNotificationPresentation = AppNotificationInfoBarPresentation.Hidden;
        _suppressAppNotificationClosed = false;
        AppNotificationActionButton.IsEnabled = true;
    }

    private void UpdateAppNotificationActionEnabledState()
    {
        _currentAppNotificationPresentation = _appNotificationInfoBarPresenter.UpdateCurrentTag(
            _currentAppNotificationPresentation,
            _currentNavTag);
        AppNotificationActionButton.IsEnabled =
            _currentAppNotificationPresentation.Action.IsEnabled;
    }

    private static InfoBarSeverity ToInfoBarSeverity(AppNotificationInfoBarSeverity severity) => severity switch
    {
        AppNotificationInfoBarSeverity.Success => InfoBarSeverity.Success,
        AppNotificationInfoBarSeverity.Warning => InfoBarSeverity.Warning,
        AppNotificationInfoBarSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };

    private void UpdateNotificationsBell(AppNotificationSnapshot snapshot)
    {
        var count = snapshot.ActiveNotifications.Count;

        if (NotificationsBadge is not null)
        {
            NotificationsBadge.Value = count;
            NotificationsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncBellItems(snapshot.ActiveNotifications.Select(NotificationItemViewModel.From).ToList());

        SyncBellFlyoutEmptyState();
    }

    private void SyncBellItems(IReadOnlyList<NotificationItemViewModel> desiredItems)
    {
        var desiredIds = desiredItems
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = _bellItems.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(_bellItems[i].Id))
                _bellItems.RemoveAt(i);
        }

        for (var i = 0; i < desiredItems.Count; i++)
        {
            var item = desiredItems[i];
            if (i < _bellItems.Count && string.Equals(_bellItems[i].Id, item.Id, StringComparison.Ordinal))
            {
                if (!_bellItems[i].Equals(item))
                    _bellItems[i] = item;
                continue;
            }

            var existingIndex = -1;
            for (var j = i + 1; j < _bellItems.Count; j++)
            {
                if (string.Equals(_bellItems[j].Id, item.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _bellItems.Move(existingIndex, i);
                if (!_bellItems[i].Equals(item))
                    _bellItems[i] = item;
            }
            else
            {
                _bellItems.Insert(i, item);
            }
        }
    }

    private void SyncBellFlyoutEmptyState()
    {
        var hasItems = _bellItems.Count > 0;

        if (BellNotificationsList is not null)
            BellNotificationsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        if (BellEmptyState is not null)
            BellEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        if (BellClearAllButton is not null)
            BellClearAllButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        if (BellActiveCountText is not null)
            BellActiveCountText.Text = hasItems
                ? LocalizationHelper.Format("NotificationsFlyout_ActiveCountFormat", _bellItems.Count)
                : string.Empty;
    }

    private void OnNotificationsFlyoutOpening(object sender, object e)
    {
        if (BellNotificationsList is not null && !_bellListBound)
        {
            BellNotificationsList.ItemsSource = _bellItems;
            _bellListBound = true;
        }
        SyncBellFlyoutEmptyState();
    }

    private void OnBellClearAllClick(object sender, RoutedEventArgs e)
        => _appNotificationService?.ClearAll();

    private void OnBellDismissNotificationClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string notificationId })
            _appNotificationService?.Dismiss(notificationId);
    }

    private void OnBellOpenPageClick(object sender, RoutedEventArgs e)
    {
        NotificationsFlyout.Hide();
        NavigateTo("notifications");
    }

    private void OnStatusFlyoutOpening(object sender, object e)
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var settings = CurrentApp.Settings;
        var nodeEnabled = settings?.EnableNodeMode == true;
        var enabledCapabilities = CountEnabledCapabilities(settings);
        var op = snapshot?.OperatorState ?? RoleConnectionState.Idle;

        GatewayRowDot.Fill = AccentBrush(ConnectionStatusPresenter.RoleAccent(op));
        GatewayRowDetail.Text = BuildGatewayDetail(snapshot);
        GatewayRowAction.Visibility =
            op is RoleConnectionState.Connected or RoleConnectionState.Connecting
                ? Visibility.Collapsed
                : Visibility.Visible;

        OperatorRowDot.Fill = AccentBrush(ConnectionStatusPresenter.RoleAccent(op));
        OperatorRowDetail.Text = LocalizationHelper.GetString(
            ConnectionStatusPresenter.RoleStateLabelKey(op));

        if (snapshot is not null)
        {
            var (nodeKey, nodeAccent) = ConnectionStatusPresenter.NodeRow(snapshot, nodeEnabled, enabledCapabilities);
            NodeRowDot.Fill = AccentBrush(nodeAccent);
            NodeRowDetail.Text = LocalizationHelper.GetString(nodeKey);
            NodeRowAction.Visibility = ConnectionStatusPresenter.NodeNeedsApproval(snapshot, nodeEnabled)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            NodeRowDot.Fill = AccentBrush(ConnectionStatusAccent.Neutral);
            NodeRowDetail.Text = LocalizationHelper.GetString("HubWindow_Role_Disabled");
            NodeRowAction.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildGatewayDetail(GatewayConnectionSnapshot? snapshot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot?.GatewayName))
            parts.Add(snapshot!.GatewayName!);
        if (!string.IsNullOrWhiteSpace(snapshot?.GatewayUrl))
            parts.Add(snapshot!.GatewayUrl!);
        if (LastGatewaySelf is { ServerVersion: { Length: > 0 } ver })
            parts.Add($"v{ver}");
        return parts.Count > 0
            ? string.Join(" · ", parts)
            : LocalizationHelper.GetString("StatusDisplay_Disconnected");
    }

    private static int CountEnabledCapabilities(SettingsManager? settings)
    {
        if (settings is null) return 0;

        var count = 0;
        if (settings.NodeBrowserProxyEnabled) count++;
        if (settings.NodeCameraEnabled) count++;
        if (settings.NodeCanvasEnabled) count++;
        if (settings.NodeScreenEnabled) count++;
        if (settings.NodeLocationEnabled) count++;
        if (settings.NodeTtsEnabled) count++;
        if (settings.NodeSttEnabled) count++;
        if (settings.NodeOllamaInferenceEnabled) count++;
        return count;
    }

    private void OnStatusFlyoutOpenConnectionClick(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        NavigateTo("connection");
    }

    private void OnStatusFlyoutReconnectClick(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        if (ReconnectAction is not null)
            ReconnectAction.Invoke();
        else
            ConnectAction?.Invoke();
    }

    private void OnStatusFlyoutNodeActionClick(object sender, RoutedEventArgs e)
    {
        StatusFlyout.Hide();
        NavigateTo("connection");
    }


    private void OnAppNotificationInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_suppressAppNotificationClosed)
            return;

        if (_lastAppNotificationSnapshot is not null)
        {
            // Closing the InfoBar hides the entire banner strip for notifications
            // that were already active. The Notifications page remains the source
            // of truth, and deleting the displayed list item can still reveal a
            // remaining hidden item via RenderAppNotification's fallback path.
            _appNotificationInfoBarPresenter.HideActiveNotifications(_lastAppNotificationSnapshot);
        }

        HideAppNotificationInfoBar();
    }

    private void OnAppNotificationActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_currentAppNotificationPresentation.Action.Kind ==
            AppNotificationInfoBarActionKind.ShowMore)
        {
            NavigateTo("notifications");
            return;
        }

        if (_currentAppNotificationPresentation.Action is
            { Kind: AppNotificationInfoBarActionKind.NotificationRoute, Route.Length: > 0 } action)
        {
            var route = action.Route;
            if (AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey))
            {
                CurrentApp.PendingChatSessionKey = sessionKey;
                PendingChatSessionKey = sessionKey;
                if (CurrentPage is ChatPage chatPage)
                    chatPage.SelectSession(sessionKey!);
                else
                    NavigateTo("chat");
            }
            else
            {
                NavigateTo(route);
            }
            _appNotificationService?.Dismiss(_currentAppNotificationPresentation.Notification!.Id);
            return;
        }
    }

    private void OnAppModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsClosed || AppModel == null) return;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.Status):
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (IsClosed) return;
                        _cachedCommands = null;
                        var status = AppModel!.Status;
                        UpdateTitleBarStatus(status);
                        // Defer nav visibility to avoid stowed exceptions during NavigationView layout
                        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            if (IsClosed) return;
                            ScheduleGatewayNavVisibilityForStatus(AppModel!.Status, debounceDisconnected: true);
                        });
                });
                break;
            case nameof(AppState.GatewaySelf):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsClosed) return;
                    UpdateTitleBarStatus(AppModel!.Status);
                });
                break;
            case nameof(AppState.AgentsList):
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsClosed) return;
                    if (AppModel!.AgentsList.HasValue)
                        RebuildAgentNavItems(AppModel.AgentsList.Value);
                });
                break;
        }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[HubWindow] OnAppModelChanged({e.PropertyName}) failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Apply persisted nav-pane state. Called from App.ShowHub() after creating the window.
    /// </summary>
    internal void ApplyNavPaneState(SettingsManager settings)
    {
        if (NavView != null)
        {
            NavView.IsPaneOpen = settings.HubNavPaneOpen;
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minPane = 200;
        const double maxPane = 260;
        const double ratio = 0.25;

        double desired = e.NewSize.Width * ratio;
        NavView.OpenPaneLength = Math.Clamp(desired, minPane, maxPane);
    }

    private void OnNavContentHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        NavContentClip.Rect = new global::Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
    }

    private void OnNavPaneToggleButtonClick(object sender, RoutedEventArgs e)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // The top-left brand mark doubles as the nav pane toggle. Hovering swaps the
    // lobster logo for the toggle glyph, matching the GitHub Copilot app.
    private void OnBrandTogglePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        BrandToggleMark.Opacity = 0;
        BrandToggleGlyph.Opacity = 1;
    }

    private void OnBrandTogglePointerExited(object sender, PointerRoutedEventArgs e)
    {
        BrandToggleMark.Opacity = 1;
        BrandToggleGlyph.Opacity = 0;
    }

    // ── Back navigation (title-bar back button + Alt+Left) ──────────────────
    //
    // We host a single native-style back button in the custom title bar and
    // drive it off ContentFrame's real back stack. NavigationView's own back
    // button is collapsed because its chrome is hoisted into the custom title
    // bar; this button is the equivalent affordance.

    private void OnBackRequested(object sender, RoutedEventArgs e) => GoBack();

    /// <summary>True when the content frame's back-stack can navigate back.</summary>
    public bool CanGoBack => ContentFrame?.CanGoBack ?? false;

    /// <summary>Navigate back one entry when possible. Exposed for the navigation service.</summary>
    public void NavigateBack() => GoBack();

    private void GoBack()
    {
        RemoveUnavailableGatewayBackStackEntries();

        if (!ContentFrame.CanGoBack)
        {
            UpdateBackButton();
            return;
        }

        ContentFrame.GoBack();
    }

    /// <summary>
    /// Enable/disable the title-bar back button to mirror ContentFrame's back
    /// stack (greyed out at the root, exactly like NavigationView's native
    /// back button). Called after every navigation.
    /// </summary>
    private void UpdateBackButton()
    {
        RemoveUnavailableGatewayBackStackEntries();
        NavBackButton.IsEnabled = ContentFrame.CanGoBack;
    }

    /// <summary>
    /// Navigate to the default page. Call after setting AppModel.
    /// </summary>
    public void NavigateToDefault()
    {
        if (ContentFrame.Content == null)
        {
            NavigateTo("connection");
        }
    }

    /// <summary>Returns the currently displayed page in the content frame.</summary>
    public object? CurrentPage => ContentFrame?.Content;

    // Canonical tag of the page currently shown in ContentFrame; tracked here
    // (rather than relying on NavView.SelectedItem) so navigation identity
    // includes the tag — important for agent-scoped pages where several tags
    // map to the same Page type (e.g. "sessions" vs "agent:main:sessions"
    // both → SessionsPage).
    private string? _currentNavTag;

    // Set true while a programmatic SelectedItem update is in flight, to
    // suppress the resulting SelectionChanged from re-entering NavigateInternal.
    private bool _syncingNavSelection;

    /// <summary>
    /// Navigate to a specific page by tag name (e.g. "connection", "sessions", "channels").
    /// Cross-page links and the rail both flow through here; the resulting
    /// <see cref="ContentFrame"/> back-stack entry powers the title-bar back button.
    /// </summary>
    public void NavigateTo(string tag) =>
        NavigateInternal(HubPageRegistry.NormalizeTag(tag, _currentAgentId));

    private void NavigateInternal(string tag)
    {
        if (tag == "debug" && !DiagnosticsGate.IsVisible)
            tag = "settings";

        var pageType = HubPageRegistry.ResolvePageType(tag);
        if (pageType == null) return;

        // Identity dedupe: navigation identity = (PageType, normalized tag).
        // Page-type-only dedupe would collapse distinct logical destinations
        // that share a Page (e.g. agent switching on WorkspacePage), and
        // would also push duplicate back-stack entries when the user
        // re-invokes the current page.
        if (ContentFrame.SourcePageType == pageType && _currentNavTag == tag)
        {
            _contentReady = CreateCompletedContentReady();
            AccessibilityNavigationSignal.WritePageReady(pageType.Name);
            return;
        }

        // Best-effort rail highlight before the page swaps in.
        SyncNavSelection(tag);

        // Pass the tag as the navigation parameter so OnContentFrameNavigated
        // can recover the canonical destination on Back/Forward.
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentReady = ready;
        if (!ContentFrame.Navigate(pageType, tag))
            CompleteContentReady(ready, pageName: null);
    }

    /// <summary>
    /// Reflect <paramref name="tag"/> in the NavigationView rail. Suppresses the
    /// resulting SelectionChanged so this programmatic update does not re-enter
    /// <see cref="NavigateInternal"/> (which would push a duplicate back-stack
    /// entry). This matters when called from Back/Forward in OnContentFrameNavigated.
    /// </summary>
    private void SyncNavSelection(string? tag)
    {
        if (tag == null) return;
        var item = FindNavItemForTag(NavView.MenuItems, tag)
                ?? FindNavItemForTag(NavView.FooterMenuItems, tag);
        if (item != null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingNavSelection = true;
            try { NavView.SelectedItem = item; }
            finally { _syncingNavSelection = false; }
            return;
        }

        if (item == null && NavView.SelectedItem != null)
        {
            _syncingNavSelection = true;
            try { NavView.SelectedItem = null; }
            finally { _syncingNavSelection = false; }
        }
    }

    public async Task WaitForCurrentContentReadyAsync()
    {
        var ready = _contentReady;
        var completed = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        if (completed == ready.Task)
            await ready.Task;
        else
            ready.TrySetResult(true);
    }

    private static NavigationViewItem? FindNavItemForTag(IList<object> items, string tag)
    {
        foreach (var raw in items)
        {
            if (raw is NavigationViewItem item)
            {
                if ((item.Tag as string) == tag) return item;
                if (item.MenuItems.Count > 0)
                {
                    var nested = FindNavItemForTag(item.MenuItems, tag);
                    if (nested != null)
                    {
                        // Expand parent so the user can see the selected child.
                        item.IsExpanded = true;
                        return nested;
                    }
                }
            }
        }
        return null;
    }

    private void UpdateTitleBarStatus(ConnectionStatus status)
    {
        var snapshot = CurrentApp.ConnectionManager?.CurrentSnapshot;
        var (text, accent) = ComputePillState(status, snapshot);
        StatusPillText.Text = text;
        StatusPillDot.Fill = AccentBrush(accent);
        ApplyWindowStatusIcon(accent);
    }

    internal void UpdateTitleBarStatus(GatewayConnectionSnapshot snapshot, ConnectionStatus status)
    {
        var (text, accent) = ComputePillState(status, snapshot);
        StatusPillText.Text = text;
        StatusPillDot.Fill = AccentBrush(accent);
        ApplyWindowStatusIcon(accent);
    }

    // Mirrors the companion-app status dot onto the desktop/taskbar window icon:
    // the lobster with a coloured dot in the bottom-right corner.
    private void ApplyWindowStatusIcon(ConnectionStatusAccent accent)
    {
        try
        {
            this.SetIcon(StatusBadgeIconFactory.GetBadgedIconPath(accent));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to update window status icon: {ex.Message}");
        }
    }

    private static (string Text, ConnectionStatusAccent Accent) ComputePillState(
        ConnectionStatus status, GatewayConnectionSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            var (labelKey, accent) = ConnectionStatusPresenter.Pill(snapshot.OverallState);
            return (LocalizationHelper.GetString(labelKey), accent);
        }

        return status switch
        {
            ConnectionStatus.Connected => (LocalizationHelper.GetString("StatusDisplay_Connected"), ConnectionStatusAccent.Success),
            ConnectionStatus.Connecting => (LocalizationHelper.GetString("StatusDisplay_Connecting"), ConnectionStatusAccent.Caution),
            ConnectionStatus.Error => (LocalizationHelper.GetString("StatusDisplay_Error"), ConnectionStatusAccent.Critical),
            _ => (LocalizationHelper.GetString("StatusDisplay_Disconnected"), ConnectionStatusAccent.Neutral),
        };
    }

    private static string AccentBrushKey(ConnectionStatusAccent accent) => accent switch
    {
        ConnectionStatusAccent.Success => "SystemFillColorSuccessBrush",
        ConnectionStatusAccent.Caution => "SystemFillColorCautionBrush",
        ConnectionStatusAccent.Critical => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorNeutralBrush",
    };

    private static Brush AccentBrush(ConnectionStatusAccent accent)
    {
        var resources = Application.Current.Resources;
        if (resources.TryGetValue(AccentBrushKey(accent), out var brush) && brush is Brush typed)
            return typed;

        if (resources.TryGetValue("SystemFillColorNeutralBrush", out var neutral) && neutral is Brush fallback)
            return fallback;

        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void ScheduleGatewayNavVisibilityForStatus(ConnectionStatus status, bool debounceDisconnected)
    {
        switch (GatewayNavVisibilityDebouncePolicy.GetDecision(status, debounceDisconnected))
        {
            case GatewayNavVisibilityDecision.ShowNow:
                _gatewayNavHideTimer?.Stop();
                UpdateGatewayNavVisibility(connected: true);
                break;
            case GatewayNavVisibilityDecision.HideNow:
                _gatewayNavHideTimer?.Stop();
                UpdateGatewayNavVisibility(connected: false);
                break;
            case GatewayNavVisibilityDecision.ScheduleHide:
                ScheduleGatewayNavHide();
                break;
        }
    }

    private void ScheduleGatewayNavHide()
    {
        if (DispatcherQueue is null)
        {
            UpdateGatewayNavVisibility(connected: false);
            return;
        }

        _gatewayNavHideTimer ??= DispatcherQueue.CreateTimer();
        _gatewayNavHideTimer.Interval = GatewayNavVisibilityDebouncePolicy.DisconnectHideDelay;
        _gatewayNavHideTimer.Tick -= OnGatewayNavHideTimerTick;
        _gatewayNavHideTimer.Tick += OnGatewayNavHideTimerTick;
        _gatewayNavHideTimer.Stop();
        _gatewayNavHideTimer.Start();
    }

    private void OnGatewayNavHideTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (IsClosed || AppModel is null)
            return;

        if (GatewayNavVisibilityDebouncePolicy.ShouldHideAfterDelay(AppModel.Status))
            UpdateGatewayNavVisibility(connected: false);
    }

    private void UpdateGatewayNavVisibility(bool connected)
    {
        try
        {
            var vis = connected ? Visibility.Visible : Visibility.Collapsed;
            var currentTag = _currentNavTag ?? (NavView?.SelectedItem as NavigationViewItem)?.Tag as string;
            var keepCurrentGatewayPageVisible = !connected &&
                HubPageRegistry.ShouldKeepCurrentPageVisibleDuringDisconnect(currentTag);

            NavChat.Visibility = vis;
            NavSessions.Visibility = vis;
            NavExtensions.Visibility = vis;
            NavChannels.Visibility = vis;
            NavInstances.Visibility = vis;
            NavCron.Visibility = vis;
            NavAdvanced.Visibility = keepCurrentGatewayPageVisible ? Visibility.Visible : vis;
            NavGatewaySeparator.Visibility = vis;

            if (!connected)
            {
                if (keepCurrentGatewayPageVisible)
                    return;

                RemoveUnavailableGatewayBackStackEntries();
                if (HubPageRegistry.IsGatewayPageTag(currentTag))
                {
                    foreach (NavigationViewItem item in NavView!.MenuItems.OfType<NavigationViewItem>())
                    {
                        if (item.Tag as string == "connection")
                        {
                            NavView.SelectedItem = item;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[HubWindow] UpdateGatewayNavVisibility failed: {ex.Message}");
            throw;
        }
    }

    private void RemoveUnavailableGatewayBackStackEntries()
    {
        if (AppModel?.Status == ConnectionStatus.Connected)
            return;

        RemoveBackStackEntries(HubPageRegistry.IsGatewayPageTag);
    }

    private void RemoveBackStackEntries(string tag) =>
        RemoveBackStackEntries(candidate => string.Equals(candidate, tag, StringComparison.Ordinal));

    private void RemoveBackStackEntries(Func<string, bool> shouldRemove)
    {
        for (var i = ContentFrame.BackStack.Count - 1; i >= 0; i--)
        {
            if (ContentFrame.BackStack[i].Parameter is string tag && shouldRemove(tag))
                ContentFrame.BackStack.RemoveAt(i);
        }
    }

    public GatewaySelfInfo? LastGatewaySelf => AppModel?.GatewaySelf;

    private void RebuildAgentNavItems(System.Text.Json.JsonElement data)
    {
        if (!data.TryGetProperty("agents", out var agentsEl) ||
            agentsEl.ValueKind != System.Text.Json.JsonValueKind.Array) return;

        AgentsNavItem.MenuItems.Clear();

        foreach (var agent in agentsEl.EnumerateArray())
        {
            var id = agent.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id)) continue;
            var name = agent.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            var agentItem = new NavigationViewItem
            {
                Content = name ?? id,
                Tag = $"agent:{id}",
                Icon = BuildAgentItemIcon()
            };

            AgentsNavItem.MenuItems.Add(agentItem);
        }
    }

    /// <summary>Extract agent IDs from cached agents data.</summary>
    public List<string> GetAgentIds() => AppModel?.GetAgentIds() ?? new List<string> { "main" };

    public System.Text.Json.JsonElement? LastAgentsData => AppModel?.AgentsList;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Skip when the selection was set programmatically by
        // OnContentFrameNavigated reflecting a Back/Forward — the page
        // is already showing and re-running NavigateInternal would push
        // a duplicate back-stack entry.
        if (_syncingNavSelection) return;

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateInternal(HubPageRegistry.NormalizeTag(tag, _currentAgentId));
        }
    }

    /// <summary>
    /// Authoritative post-navigation hook. Runs for every successful
    /// Frame.Navigate (including Back/Forward), so it's the single place that
    /// rebuilds <see cref="_currentNavTag"/> / <see cref="_currentAgentId"/>,
    /// re-syncs the rail + back button, and re-runs
    /// <see cref="InitializeCurrentPage"/> for the page that's now visible.
    /// </summary>
    private void OnContentFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var tag = e.Parameter as string;
        _currentNavTag = tag;

        // Keep _currentAgentId aligned with the page that's now visible.
        if (tag != null && tag.StartsWith("agent:"))
        {
            var newAgent = HubPageRegistry.ParseAgentId(tag);
            if (newAgent != _currentAgentId)
            {
                _currentAgentId = newAgent;
                _cachedCommands = null;
            }
        }

        // Reflect the restored page in the rail. Back/Forward don't route
        // through NavigateInternal, so this is the only place the rail
        // highlight gets re-synced for them.
        SyncNavSelection(tag);
        UpdateBackButton();

        InitializeCurrentPage();
        UpdateAppNotificationActionEnabledState();
        ArmContentReady(
            e.Content as FrameworkElement,
            e.Content?.GetType().Name);

        // Navigation activation hook: resolve + assign a DI-backed view model for the
        // navigated page. Pages present in the page->view-model map (currently Settings)
        // get their view model resolved, activated, and assigned as DataContext; other pages
        // only advance the navigation scope (deactivating any prior view model). Contained in a
        // try/catch so a misbehaving view model's activation cannot escape this XAML event handler
        // and crash the app.
        if (e.Content is { } content)
        {
            try
            {
                CurrentApp.PageActivator?.OnNavigatedTo(content, tag);
            }
            catch (Exception ex)
            {
                OpenClawTray.Services.Logger.Error($"[HubWindow] Page activation failed: {ex}");
            }
        }
    }

    private void OnContentFrameNavigationFailed(object sender, Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        _contentReady.TrySetResult(true);
    }

    private void ArmContentReady(
        FrameworkElement? element,
        string? pageName)
    {
        var ready = _contentReady;
        if (element == null || element.IsLoaded)
        {
            CompleteContentReady(ready, pageName);
            return;
        }

        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            element.Loaded -= loaded;
            CompleteContentReady(ready, pageName);
        };
        element.Loaded += loaded;
    }

    private void CompleteContentReady(
        TaskCompletionSource<bool> ready,
        string? pageName)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (ReferenceEquals(_contentReady, ready) &&
                    ready.TrySetResult(true))
                {
                    AccessibilityNavigationSignal.WritePageReady(pageName);
                }
            });
    }

    /// <summary>
    /// Persist the NavigationView's expanded/compact state on every toggle.
    /// Both PaneOpening and PaneClosing route here; we read the current
    /// state from the sender so we don't have to distinguish the two.
    /// </summary>
    private void OnNavPaneStateChanged(NavigationView sender, object args)
    {
        var settings = CurrentApp.Settings;
        if (settings == null) return;
        // PaneOpening fires BEFORE IsPaneOpen flips, PaneClosing fires
        // BEFORE it flips the other way. Use the event identity to know
        // the new state rather than reading IsPaneOpen.
        var newState = args is NavigationViewPaneClosingEventArgs ? false : true;
        if (settings.HubNavPaneOpen == newState) return;
        settings.HubNavPaneOpen = newState;
        // slopwatch-ignore: SW003 Optional persisted state fallback is intentional; caller continues with defaults or prior state.
        try { settings.Save(); } catch { /* swallow — don't block UI */ }
    }

    private void InitializeCurrentPage()
    {
        switch (ContentFrame.Content)
        {
            case ChatPage chat: chat.Initialize(); break;
            case SessionsPage sessions: sessions.Initialize(); break;
            case ConnectionPage connection: connection.Initialize(); break;
            case ChannelsPage channels: channels.Initialize(); break;
            case UsagePage usage: usage.Initialize(); break;
            case CronPage cron: cron.Initialize(); break;
            case ConfigPage config:
                try { config.Initialize(); }
                catch (Exception ex)
                {
                    OpenClawTray.Services.Logger.Error($"[HubWindow] ConfigPage seed failed: {ex}");
                }
                break;
            case InstancesPage instances: instances.Initialize(); break;
            case PermissionsPage: break;
            case SandboxPage sandbox: sandbox.Initialize(); break;
            case VoiceSettingsPage voice: voice.Initialize(CurrentApp.VoiceService); break;
            case AgentEventsPage agentEvents:
                agentEvents.Initialize(this);
                agentEvents.ClearCentralCache = () => AppModel?.ClearAgentEvents();
                agentEvents.PopulateAgentFilter(this);
                // When navigated via top-level nav (tag "agentevents") show all
                // agents; when reached via an agent-scoped tag (e.g.
                // "agent:main:agentevents") scope to that agent. Reading
                // _currentNavTag (set by OnContentFrameNavigated) is more
                // reliable than peeking NavView.SelectedItem, which may briefly
                // disagree during Back/Forward.
                var eventsAgentFilter = _currentNavTag?.StartsWith("agent:") == true ? _currentAgentId : null;
                agentEvents.SetAgentFilter(eventsAgentFilter);
                // Seed existing events from AppState
                if (agentEvents.EventCount == 0 && AppModel?.AgentEvents is { Count: > 0 } events)
                {
                    for (int i = events.Count - 1; i >= 0; i--)
                        agentEvents.AddEvent(events[i]);
                }
                break;
            case WorkspacePage workspace:
                workspace.AgentId = _currentAgentId;
                workspace.Initialize();
                break;
            case BindingsPage bindings: bindings.Initialize(); break;
            case SettingsPage settings: settings.Initialize(); break;
            case NotificationsPage notifications: notifications.Initialize(_appNotificationService); break;
            case DebugPage debug: debug.Initialize(); break;
        }
    }

    public void SetActivityFilter(string? filter)
    {
        // ActivityPage has been removed; the method is kept as a no-op so any
        // remaining external callers (e.g. legacy deep links) don't NRE.
        _ = filter;
    }

    // ── Command Search (Ctrl+E / Ctrl+K / Ctrl+F) — title bar AutoSuggestBox ──

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            global::Windows.System.VirtualKey.Control).HasFlag(
            global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && (e.Key == global::Windows.System.VirtualKey.E ||
                     e.Key == global::Windows.System.VirtualKey.K ||
                     e.Key == global::Windows.System.VirtualKey.F))
        {
            e.Handled = true;
            TitleSearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            TitleSearchBox.Text = "";
            return;
        }

        // Alt+Left → back, matching the shell-wide navigation gesture and
        // NavigationView's built-in keyboard accelerator.
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            global::Windows.System.VirtualKey.Menu).HasFlag(
            global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt && e.Key == global::Windows.System.VirtualKey.Left && ContentFrame.CanGoBack)
        {
            e.Handled = true;
            GoBack();
        }
    }

    private ImmutableArray<HubCommand>? _cachedCommands;

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _cachedCommands ??= BuildCommandList();
        sender.ItemsSource = HubPageRegistry.SearchCommands(_cachedCommands.Value, sender.Text);
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is HubCommand cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is HubCommand cmd)
        {
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(cmd);
        }
        else if (sender.ItemsSource is IEnumerable<HubCommand> items &&
            items.FirstOrDefault() is { } first)
        {
            // Enter pressed without selecting — execute first match
            sender.Text = "";
            sender.ItemsSource = null;
            _cachedCommands = null;
            ExecuteCommand(first);
        }
    }

    internal ImmutableArray<HubCommand> BuildCommandList()
    {
        var settings = CurrentApp.Settings;
        var toggles = settings is null
            ? null
            : new HubCommandToggleState(
                settings.EnableNodeMode,
                settings.NodeCameraEnabled,
                settings.NodeCanvasEnabled,
                settings.NodeScreenEnabled,
                settings.NodeBrowserProxyEnabled);
        var sessions = AppModel?.Sessions?.Select(session => session.Key).ToImmutableArray()
            ?? ImmutableArray<string>.Empty;
        var resources = HubPageRegistry.CommandResourceKeys.ToImmutableDictionary(
            key => key,
            LocalizationHelper.GetString,
            StringComparer.Ordinal);

        return HubPageRegistry.BuildCommands(new HubCommandContext(
            _currentAgentId,
            DiagnosticsGate.IsVisible,
            toggles,
            sessions,
            resources));
    }

    private void ToggleCommandPalettePermission(HubSettingToggle toggle)
    {
        var settings = CurrentApp.SettingsOrNull;
        if (settings == null)
            return;

        var settingName = GetSettingName(toggle);
        var nextValue = !GetCurrentSettingValue(settings, toggle);
        try
        {
            if (CurrentApp.SettingsStore is { } store)
            {
                _commandPaletteSettingsOrigin ??= store.CreateOrigin();
                store.Update(_commandPaletteSettingsOrigin, edit =>
                    ApplySettingValue(edit, toggle, nextValue));
            }
            else
            {
                Services.Logger.Warn($"[HubWindow] ISettingsStore unavailable for {settingName}. Falling back to SettingsManager.Save.");
                ApplySettingValue(settings, toggle, nextValue);
                settings.Save();
            }

            RaiseSettingsSaved();
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[HubWindow] Failed to persist {settingName}: {ex.Message}");
        }
    }

    private static string GetSettingName(HubSettingToggle toggle) => toggle switch
    {
        HubSettingToggle.NodeMode => nameof(SettingsManager.EnableNodeMode),
        HubSettingToggle.Camera => nameof(SettingsManager.NodeCameraEnabled),
        HubSettingToggle.Canvas => nameof(SettingsManager.NodeCanvasEnabled),
        HubSettingToggle.ScreenCapture => nameof(SettingsManager.NodeScreenEnabled),
        HubSettingToggle.BrowserControl => nameof(SettingsManager.NodeBrowserProxyEnabled),
        _ => throw new ArgumentOutOfRangeException(nameof(toggle))
    };

    private static bool GetCurrentSettingValue(SettingsManager settings, HubSettingToggle toggle) =>
        toggle switch
        {
            HubSettingToggle.NodeMode => settings.EnableNodeMode,
            HubSettingToggle.Camera => settings.NodeCameraEnabled,
            HubSettingToggle.Canvas => settings.NodeCanvasEnabled,
            HubSettingToggle.ScreenCapture => settings.NodeScreenEnabled,
            HubSettingToggle.BrowserControl => settings.NodeBrowserProxyEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(toggle))
        };

    private static void ApplySettingValue(
        ISettingsEditor settings,
        HubSettingToggle toggle,
        bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }

    private static void ApplySettingValue(
        SettingsManager settings,
        HubSettingToggle toggle,
        bool value)
    {
        switch (toggle)
        {
            case HubSettingToggle.NodeMode: settings.EnableNodeMode = value; break;
            case HubSettingToggle.Camera: settings.NodeCameraEnabled = value; break;
            case HubSettingToggle.Canvas: settings.NodeCanvasEnabled = value; break;
            case HubSettingToggle.ScreenCapture: settings.NodeScreenEnabled = value; break;
            case HubSettingToggle.BrowserControl: settings.NodeBrowserProxyEnabled = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(toggle));
        }
    }

    private void ExecuteCommand(HubCommand command)
    {
        switch (command.Action.Kind)
        {
            case HubCommandActionKind.Navigate when command.Action.Value is { Length: > 0 } tag:
                NavigateTo(tag);
                break;
            case HubCommandActionKind.OpenDashboard:
                ((IAppCommands)Application.Current).OpenDashboard(command.Action.Value);
                break;
            case HubCommandActionKind.ToggleSetting when command.Action.Toggle is { } toggle:
                ToggleCommandPalettePermission(toggle);
                break;
        }
    }

    #region High Contrast icon fallback

    // Fixed-color SVGs provide the normal Hub presentation. In High Contrast,
    // App.xaml resolves this path to system-foreground Fluent glyphs without
    // constructing or subscribing to the legacy WinRT accessibility API.
    private static readonly Dictionary<string, string> s_highContrastGlyphFallback = new()
    {
        { "chat",        "\uE8BD" },
        { "connection",  "\uE839" },
        { "sessions",    "\uE8F2" },
        { "skills",      "\uE945" },
        { "channels",    "\uEC05" },
        { "instances",   "\uE977" },
        { "agentevents", "\uE943" },
        { "bindings",    "\uE8AD" },
        { "config",      "\uE90F" },
        { "usage",       "\uE9D9" },
        { "cron",        "\uE787" },
        { "voice",       "\uE720" },
        { "settings",    "\uE713" },
        { "permissions", "\uEA18" },
        { "sandbox",     "\uE72E" },
        { "activity",    "\uEA95" },
        { "notifications", "\uE7F4" },
        { "debug",       "\uEBE8" },
    };

    private const string AdvancedGroupGlyph = "\uE950";
    private const string AgentsGroupGlyph = "\uE99A";
    private bool _isHighContrast;

    private void ApplyHighContrastFallbackIfNeeded()
    {
        const string resourceKey = "HubNavigationUseHighContrastIcons";
        _isHighContrast = Application.Current.Resources.ContainsKey(resourceKey)
            && Application.Current.Resources[resourceKey] is true;
        if (!_isHighContrast)
            return;

        SwapToFontIcons(NavView.MenuItems);
        SwapToFontIcons(NavView.FooterMenuItems);
    }

    private void SwapToFontIcons(IList<object> items)
    {
        foreach (var value in items)
        {
            if (value is not NavigationViewItem item)
                continue;

            item.Icon = ResolveHighContrastIcon(item);
            if (item.MenuItems.Count > 0)
                SwapToFontIcons(item.MenuItems);
        }
    }

    private IconElement ResolveHighContrastIcon(NavigationViewItem item)
    {
        if (item.Tag is string tag)
        {
            if (s_highContrastGlyphFallback.TryGetValue(tag, out var glyph))
                return FluentIconCatalog.Build(glyph, 20);
            if (tag.StartsWith("agent:", StringComparison.Ordinal))
                return FluentIconCatalog.Build(AgentsGroupGlyph, 20);
        }

        if (item == AgentsNavItem)
            return FluentIconCatalog.Build(AgentsGroupGlyph, 20);
        if (item == NavAdvanced)
            return FluentIconCatalog.Build(AdvancedGroupGlyph, 20);

        return FluentIconCatalog.Build("\uE700", 20);
    }

    private IconElement BuildAgentItemIcon()
    {
        if (_isHighContrast)
            return FluentIconCatalog.Build(AgentsGroupGlyph, 20);

        return new ImageIcon
        {
            Source = (ImageSource)NavView.Resources["Agents_Icon"]
        };
    }

    #endregion
}
