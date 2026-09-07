using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Chat;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using OpenClaw.Connection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace OpenClawTray.Pages;

public sealed partial class ChatPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private HubWindow? _hub;
    private MountedReactorChat? _reactorHost;
    private IChatDataProvider? _mountedProvider;
    private IChatDataProvider? _accessibilityTestProvider;
    private string? _mountedThreadId;
    private string? _chatUrl;
    private bool _webViewInitialized;
    private bool _webViewMode;
    private bool _pageActive;
    private readonly SemaphoreSlim _speakerMuteGate = new(1, 1);
    private int _voiceSettingsDialogOpen;
    private bool _navigationStarted;
    private CancellationTokenSource? _navigationCts;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _navCompletedHandler;
    private global::Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _navStartingHandler;
    private IGatewayConnectionManager? _connectionManager;
    private IChatPagePanelHost? _panelHost;
    private IChatPagePanelHost PanelHost => _panelHost ??= new ChatPagePanelHost(this);
    private string? _pendingWebViewSessionKey;
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public ChatPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageActive = false;
        UpdateNativeChatSurfaceActive();

        // Don't tear down the native chat host — preserve it across page
        // navigations so that scroll position, selected session, and loaded
        // history survive. ShowReactorSurface's _mountedProvider check
        // will reuse the existing host when the page reloads.
        // DisposeReactorHost() is intentionally NOT called here.

        _navigationCts?.Cancel();
        if (WebView.CoreWebView2 != null)
        {
            if (_navCompletedHandler != null)
                WebView.CoreWebView2.NavigationCompleted -= _navCompletedHandler;
            if (_navStartingHandler != null)
                WebView.CoreWebView2.NavigationStarting -= _navStartingHandler;
        }

        CurrentApp.SettingsChanged -= OnSettingsSaved;

        if (App.Current is App app)
            app.ChatProviderChanged -= OnAppChatProviderChanged;

        if (App.Current is App app2)
            app2.SpeakerMuteChanged -= OnSpeakerMuteChanged;

        // MEDIUM 6: detach the static debug-override subscription so that
        // an unloaded ChatPage doesn't keep responding to overrides changes
        // (the page keeps the static handler alive otherwise).
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
    }

    /// <summary>Trigger voice recording programmatically (e.g. from V hotkey).</summary>
    public void TriggerAutoStartVoice()
    {
        if (_reactorHost?.HasVoiceTrigger == true)
        {
            _reactorHost.TriggerVoiceRecording();
            return;
        }
        // Composer may not have rendered yet — retry until trigger is registered
        RetryTriggerVoice(retries: 15, delayMs: 100);
    }

    private void RetryTriggerVoice(int retries, int delayMs)
    {
        if (retries <= 0) return;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(delayMs);
            if (_reactorHost?.HasVoiceTrigger == true)
            {
                _reactorHost.TriggerVoiceRecording();
            }
            else
            {
                RetryTriggerVoice(retries - 1, delayMs);
            }
        });
    }

    public void Initialize()
    {
        _pageActive = true;
        _hub = CurrentApp.ActiveHubWindow as HubWindow;

        // Compute a "open in browser" URL once so the toolbar button works
        // even when the gateway isn't fully reachable yet.
        if (CurrentApp.Settings is not null)
        {
            var url = TryComputeChatUrl(CurrentApp.Settings);
            if (!string.IsNullOrEmpty(url))
            {
                _chatUrl = url;
            }
        }

        // Re-mount on settings change so toggling "Use standard Gateway Chat
        // interface" swaps the surface live.
        CurrentApp.SettingsChanged -= OnSettingsSaved;
        CurrentApp.SettingsChanged += OnSettingsSaved;

        if (App.Current is App app)
        {
            app.ChatProviderChanged -= OnAppChatProviderChanged;
            app.ChatProviderChanged += OnAppChatProviderChanged;
            app.SpeakerMuteChanged -= OnSpeakerMuteChanged;
            app.SpeakerMuteChanged += OnSpeakerMuteChanged;
        }

        // Also react to the per-surface debug override picked from DebugPage.
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed -= OnDebugOverrideChanged;
        OpenClawTray.Chat.DebugChatSurfaceOverrides.Changed += OnDebugOverrideChanged;

        ApplyChatSurface();
    }

    private void OnSettingsSaved(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnDebugOverrideChanged(object? sender, EventArgs e) => ApplyChatSurface();

    private void OnSpeakerMuteChanged(bool muted)
    {
        DispatcherQueue?.TryEnqueue(() => _reactorHost?.SetSpeakerMuted(muted));
    }

    private void OnAppChatProviderChanged(object? sender, EventArgs e)
    {
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplyChatSurface();
            return;
        }

        _ = dispatcher.TryEnqueue(ApplyChatSurface);
    }

    internal void SelectSession(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
            return;

        if (_hub is not null)
            _hub.PendingChatSessionKey = sessionKey;
        CurrentApp.PendingChatSessionKey = sessionKey;
        ApplyChatSurface();
    }

    private void ApplyChatSurface()
    {
        if (CurrentApp.Settings is null) return;

        var decision = ChatSurfaceResolver.Resolve(
            ChatSurfaceTarget.HubChat,
            CurrentApp.Settings.UseLegacyWebChat,
            _chatUrl,
            TryComputeChatUrl(CurrentApp.Settings));

        _chatUrl = decision.ChatUrl;

        if (decision.UseLegacyWebChat)
            ShowWebViewSurface(forceNavigate: decision.ChatUrlChanged);
        else
            ShowReactorSurface();
    }

    private static string? TryComputeChatUrl(SettingsManager settings)
    {
        return InteractiveGatewayCredentialResolver.TryResolve(
            (App.Current as App)?.Registry,
            SettingsManager.SettingsDirectoryPath,
            DeviceIdentityFileReader.Instance,
            settings.GetEffectiveGatewayUrl(),
            settings.LegacyToken,
            settings.LegacyBootstrapToken,
            (record, candidate) =>
                (App.Current as App)?.ManagedLocalPortProvenance
                    ?.IsStrongCredentialAllowed(record, candidate) == true,
            out var credential) &&
            credential is { IsBootstrapToken: false }
            ? ChatSurfaceResolver.BuildChatUrl(credential.GatewayUrl, credential.Token)
            : null;
    }

    private void ShowReactorSurface()
    {
        // Hide WebView2-specific UI; mount the Reactor host (idempotent).
        _webViewMode = false;
        StopWebViewNavigation();
        WebView.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ToolbarBorder.Visibility = Visibility.Collapsed;
        HomeButton.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;
        DevToolsButton.Visibility = Visibility.Collapsed;

        var app = App.Current as App;
        var provider = ResolveChatProvider(app);
        // A missing IChatComposerFactory registration is a composition bug, not a
        // "disconnected" state: once the DI container exists, require it so the
        // failure surfaces loudly through the existing app-level unhandled-exception
        // handler/crash log rather than being silently treated as "no provider yet".
        // Only an as-yet-uninitialized container (a normal startup race, same timing
        // window during which the provider itself is also legitimately absent) is
        // still tolerated here.
        var composerFactory = app?.Services is { } services
            ? services.GetRequiredService<IChatComposerFactory>()
            : null;
        Func<string, Task>? readAloud = app is null ? null : ReadChatTextAloudAsync;

        // Consume a pending session-key hand-off from SessionsPage or a
        // notification toast so the chat root mounts with that thread selected.
        // Any pending key forces a remount — _mountedThreadId only records what
        // we asked for, not what the user later picked inside the composer's
        // dropdown, so we cannot use it to detect "already on the right thread".
        var pendingSessionKey = _hub?.PendingChatSessionKey
            ?? (App.Current as App)?.PendingChatSessionKey;
        if (!string.IsNullOrEmpty(pendingSessionKey))
        {
            if (_hub is not null) _hub.PendingChatSessionKey = null;
            if (App.Current is App currentApp) currentApp.PendingChatSessionKey = null;
        }
        var threadIdToMount = pendingSessionKey ?? _mountedThreadId;
        var forceRemount = !string.IsNullOrEmpty(pendingSessionKey);

        if (_reactorHost is not null
            && ReferenceEquals(_mountedProvider, provider)
            && !forceRemount)
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ChatHost.Visibility = Visibility.Visible;
            UpdateNativeChatSurfaceActive();
            // Check for pending auto-start voice even when already mounted
            if (_hub?.PendingAutoStartVoice == true)
            {
                _hub.PendingAutoStartVoice = false;
                _reactorHost.TriggerVoiceRecording();
            }
            return;
        }

        DisposeReactorHost();

        if (provider is null || composerFactory is null)
        {
            // If we already have a mounted chat, keep it visible rather than
            // flashing the disconnected placeholder. The ChatProviderChanged
            // event will remount when the provider becomes available again.
            if (_reactorHost is not null)
                return;

            PlaceholderPanel.Visibility = Visibility.Visible;
            ChatHost.Visibility = Visibility.Collapsed;
            UpdateNativeChatSurfaceActive();
            return;
        }

        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ChatHost.Visibility = Visibility.Visible;
        var composerSession = ReactorChatHostExtensions.CreateComposerSession(
            ChatHost,
            composerFactory,
            provider,
            onVoiceRequest: VoiceTranscribeAsync,
            onAttachClick: OnAttachClicked,
            onSettingsClick: () => _hub?.NavigateTo("voice"),
            onSpeakerMuteChanged: muted => _ = OnSpeakerMuteChangedAsync(muted),
            initialMuted: ShouldStartSpeakerMuted(CurrentApp.Settings));
        _reactorHost = CurrentApp.ActiveHubWindow!.MountReactorChat(
            ChatHost,
            provider,
            composerSession,
            initialThreadId: threadIdToMount,
            onReadAloud: readAloud,
            onStopSpeaking: () => app?.StopChatSpeaking(),
            onOpenCheckpoints: OpenSessionCheckpoints);
        _mountedProvider = provider;
        _mountedThreadId = threadIdToMount;
        UpdateNativeChatSurfaceActive();

        // If the V hotkey (or another caller) requested auto-start voice,
        // trigger it after the UI thread processes the mount (composer needs
        // to render first so TriggerVoiceRecording is registered).
        if (_hub?.PendingAutoStartVoice == true)
        {
            _hub.PendingAutoStartVoice = false;
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _reactorHost?.TriggerVoiceRecording();
            });
        }
    }

    private void OpenSessionCheckpoints(string sessionKey) =>
        AsyncEventHandlerGuard.Run(
            () => SessionCheckpointDialogCoordinator.ShowAsync(
                XamlRoot,
                sessionKey,
                isHostAvailable: () => _pageActive && XamlRoot is not null),
            new OpenClawTray.AppLogger(),
            nameof(OpenSessionCheckpoints));

    private IChatDataProvider? ResolveChatProvider(App? app)
    {
        if (app?.ChatProvider is { } liveProvider)
            return liveProvider;

        // The accessibility suite launches an isolated app process without a
        // gateway. Mount a deterministic provider only under its explicit
        // test flag so Axe scans the real Reactor timeline and composer,
        // not merely the disconnected page shell.
        if (Environment.GetEnvironmentVariable("OPENCLAW_ACCESSIBILITY_TEST_CHAT") == "1"
            && Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } dataDirectory)
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable(
                        "OPENCLAW_ACCESSIBILITY_TEST_CHAT_FIXTURE"),
                    AccessibilityHistoryCollisionFixture.FixtureName,
                    StringComparison.Ordinal))
            {
                return _accessibilityTestProvider ??=
                    AccessibilityHistoryCollisionFixture.Create(
                        dataDirectory,
                        action => DispatcherQueue.TryEnqueue(() => action()));
            }

            return _accessibilityTestProvider ??= new AccessibilityChatDataProvider();
        }

        return null;
    }

    private sealed class AccessibilityChatDataProvider : IChatDataProvider
    {
        private const string DefaultThreadId = "accessibility-main";
        private const string MainThreadId = "agent:main:main";
        private const string ForkThreadId = "agent:main:fork";
        private static readonly ChatDataSnapshot Snapshot = CreateSnapshot();

        public string DisplayName => "Accessibility test chat";

        public event EventHandler<ChatDataChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested
        {
            add { }
            remove { }
        }

        public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        public Task SendMessageAsync(
            string threadId,
            string message,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RespondToPermissionAsync(
            string threadId,
            string requestId,
            string action,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ChatDataSnapshot CreateSnapshot()
        {
            static ChatTimelineState CreateTimeline(string id)
            {
                var timeline = ChatTimelineState.Initial() with
                {
                    Entries = ChatTimelineState.Initial().Entries
                        .Add(new ChatTimelineItem(
                            $"accessibility-user-{id}",
                            ChatTimelineItemKind.User,
                            "Verify the native chat surface."))
                        .Add(new ChatTimelineItem(
                            $"accessibility-assistant-{id}",
                            ChatTimelineItemKind.Assistant,
                            "The timeline and composer are ready.")),
                    NextId = 3,
                    HistoryLoaded = true,
                };

                if (!string.Equals(id, DefaultThreadId, StringComparison.Ordinal))
                    return timeline;

                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolStartEvent(
                        "Tool",
                        "Tool",
                        ToolCallId: "proof-parent-bash",
                        IdentityStrength: ChatToolIdentityStrength.Fallback,
                        RunId: "proof-run-bash"));
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolPresentationEvent(
                        "proof-parent-bash",
                        "Bash",
                        ChatToolIdentityStrength.Specific,
                        new JsonObject
                        {
                            ["command"] = "powershell -NoProfile -Command Get-ChildItem .\\src",
                        },
                        ChildToolCallId: "proof-child-bash",
                        RunId: "proof-run-bash"));
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolOutputEvent(
                        "Synthetic output only.",
                        "proof-child-bash",
                        "proof-run-bash"));

                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolStartEvent(
                        "Tool",
                        "Tool",
                        ToolCallId: "proof-parent-patch",
                        IdentityStrength: ChatToolIdentityStrength.Fallback,
                        RunId: "proof-run-patch"));
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolPresentationEvent(
                        "proof-parent-patch",
                        "Apply Patch",
                        ChatToolIdentityStrength.Specific,
                        new JsonObject
                        {
                            ["file_path"] = "src\\OpenClaw.Chat\\ChatTimelineReducer.cs",
                        },
                        ChildToolCallId: "proof-child-patch",
                        RunId: "proof-run-patch"));
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolOutputEvent(
                        string.Empty,
                        "proof-parent-patch",
                        "proof-run-patch"));

                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolStartEvent(
                        "Untrusted command title omitted",
                        "Tool",
                        new JsonObject
                        {
                            ["command"] = "[redacted]",
                        },
                        ToolCallId: "proof-parent-untrusted",
                        IdentityStrength: ChatToolIdentityStrength.Fallback,
                        RunId: "proof-run-untrusted"));
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatToolOutputEvent(
                        string.Empty,
                        "proof-parent-untrusted",
                        "proof-run-untrusted"));
                return ChatTimelineReducer.Apply(timeline, new ChatTurnEndEvent());
            }

            return new ChatDataSnapshot(
                [
                    new ChatThread
                    {
                        Id = DefaultThreadId,
                        Title = "Accessibility session",
                        Status = ChatThreadStatus.Running,
                        Activity = ChatActivity.Idle,
                        Model = "test-model",
                    },
                    new ChatThread
                    {
                        Id = MainThreadId,
                        Title = $"Route target: {MainThreadId}",
                        Status = ChatThreadStatus.Running,
                        Activity = ChatActivity.Idle,
                        Model = "test-model",
                    },
                    new ChatThread
                    {
                        Id = ForkThreadId,
                        Title = $"Route target: {ForkThreadId}",
                        Status = ChatThreadStatus.Running,
                        Activity = ChatActivity.Idle,
                        Model = "test-model",
                    },
                ],
                new Dictionary<string, ChatTimelineState>
                {
                    [DefaultThreadId] = CreateTimeline(DefaultThreadId),
                    [MainThreadId] = CreateTimeline(MainThreadId),
                    [ForkThreadId] = CreateTimeline(ForkThreadId),
                },
                DefaultThreadId,
                "Connected (accessibility test)",
                ["test-model"],
                new ChatComposeTarget(DefaultThreadId, IsReady: true));
        }
    }

    private void ShowWebViewSurface(bool forceNavigate = false)
    {
        // Consume pending session key for WebView mode.
        var pendingSessionKey = _hub?.PendingChatSessionKey
            ?? (App.Current as App)?.PendingChatSessionKey;
        if (!string.IsNullOrEmpty(pendingSessionKey))
        {
            if (_hub is not null) _hub.PendingChatSessionKey = null;
            if (App.Current is App currentApp) currentApp.PendingChatSessionKey = null;
            _pendingWebViewSessionKey = pendingSessionKey;
        }
        else
        {
            _pendingWebViewSessionKey = null;
        }

        // Tear down native chat (so the WebView2 owns the row) and (re)init WebView2.
        _webViewMode = true;
        DisposeReactorHost();

        ChatHost.Visibility = Visibility.Collapsed;
        UpdateNativeChatSurfaceActive();
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ToolbarBorder.Visibility = Visibility.Visible;
        HomeButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        DevToolsButton.Visibility = Visibility.Visible;

        if (_webViewInitialized)
        {
            // Already initialized — show it. The caller's `forceNavigate`
            // flag is informational; we always re-navigate so a settings
            // change (token / gateway URL) reaches the WebView.
            if (!NavigateWebViewToCurrentChatUrl())
                ShowMissingChatCredentialError();
            _ = forceNavigate; // explicit: parameter is currently advisory
            return;
        }

        if (string.IsNullOrEmpty(_chatUrl))
        {
            ShowMissingChatCredentialError();
            return;
        }

        if (CurrentApp.Settings is null) return;
        _ = InitializeWebViewAsync(CurrentApp.Settings);
    }

    private bool NavigateWebViewToCurrentChatUrl()
    {
        if (string.IsNullOrEmpty(_chatUrl) || WebView.CoreWebView2 is null)
            return false;

        ChatPagePanelStates.ApplyShowingWebView(PanelHost);

        var url = _chatUrl;
        if (!string.IsNullOrEmpty(_pendingWebViewSessionKey))
        {
            var baseUrl = System.Text.RegularExpressions.Regex.Replace(_chatUrl, @"[&?]session=[^&]*", "");
            var separator = baseUrl.Contains('?') ? "&" : "?";
            url = $"{baseUrl}{separator}session={Uri.EscapeDataString(_pendingWebViewSessionKey)}";
            _pendingWebViewSessionKey = null;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;
        WebView.CoreWebView2.Navigate(url);
        return true;
    }

    private void ShowMissingChatCredentialError()
    {
        StopWebViewNavigation();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = LocalizationHelper.GetString("ChatPage_OpenConnectionSettings");
    }

    private void StopWebViewNavigation()
    {
        try
        {
            WebView.CoreWebView2?.Stop();
            WebView.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ChatPage WebView stop failed: {ex.Message}");
        }
    }

    private void DisposeReactorHost()
    {
        var host = _reactorHost;
        _reactorHost = null;
        _mountedProvider = null;
        _mountedThreadId = null;
        UpdateNativeChatSurfaceActive();
        try { host?.Dispose(); }
        catch (Exception ex) { Logger.Debug($"ChatPage: Reactor host dispose tear-down race: {ex.Message}"); }
    }

    private void UpdateNativeChatSurfaceActive()
    {
        if (App.Current is App app)
            app.SetHubNativeChatSurfaceActive(_pageActive && !_webViewMode && _reactorHost is not null);
    }

    private async Task InitializeWebViewAsync(SettingsManager settings)
    {
        try
        {
            if (!InteractiveGatewayCredentialResolver.TryResolve(
                CurrentApp.Registry,
                SettingsManager.SettingsDirectoryPath,
                DeviceIdentityFileReader.Instance,
                settings.GetEffectiveGatewayUrl(),
                settings.LegacyToken,
                settings.LegacyBootstrapToken,
                (record, candidate) =>
                    CurrentApp.ManagedLocalPortProvenance
                        ?.IsStrongCredentialAllowed(record, candidate) == true,
                out var credential) ||
                credential == null)
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = LocalizationHelper.GetString("ChatPage_OpenConnectionSettings");
                return;
            }

            if (credential.IsBootstrapToken)
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = LocalizationHelper.GetString("ChatPage_GatewayPairingIncomplete");
                return;
            }

            if (!GatewayChatHelper.TryBuildChatUrl(credential.GatewayUrl, credential.Token, out var chatUrl, out var errorMessage))
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                return;
            }
            _chatUrl = chatUrl;
            _chatUrl = chatUrl;

            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            WaitingPanel.Visibility = Visibility.Visible;
            WaitingStatusText.Text = LocalizationHelper.GetString("ChatPage_ChatSurfaceComingOnline");
            RetryChatButton.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            await GatewayChatHelper.InitializeWebView2Async(WebView);
            _webViewInitialized = true;

            _navCompletedHandler = (s, e) =>
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;

                if (e.IsSuccess)
                {
                    // Hide the web Control UI sidebar — Hub NavigationView handles top-level nav.
                    _ = WebView.CoreWebView2.ExecuteScriptAsync(@"
                        (function() {
                            var style = document.createElement('style');
                            style.textContent = 'nav, [data-sidebar], .sidebar, aside { display: none !important; } main, [data-main], .main-content { margin-left: 0 !important; width: 100% !important; max-width: 100% !important; }';
                            document.head.appendChild(style);
                        })();
                    ");
                    ChatPagePanelStates.ApplyShowingWebView(PanelHost);
                    _ = CaptureVisualTestChatAsync();
                }
                else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted ||
                         e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                         e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset ||
                         e.WebErrorStatus == CoreWebView2WebErrorStatus.ServerUnreachable)
                {
                    ChatPagePanelStates.ApplyShowingError(PanelHost);
                    ErrorText.Text = string.Format(LocalizationHelper.GetString("ChatPage_CannotConnectToGateway"), credential.GatewayUrl);
                }
            };
            WebView.CoreWebView2.NavigationCompleted += _navCompletedHandler;

            _navStartingHandler = (s, e) =>
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            };
            WebView.CoreWebView2.NavigationStarting += _navStartingHandler;

            _connectionManager = CurrentApp.ConnectionManager;
            _navigationCts?.Cancel();
            _navigationCts = new CancellationTokenSource();
            _ = NavigateWhenChatReadyAsync(_connectionManager, credential.GatewayUrl, _navigationCts.Token);
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = string.Format(LocalizationHelper.GetString("ChatPage_WebView2InitFailed"), ex.Message);
        }
    }

    private async Task NavigateWhenChatReadyAsync(
        IGatewayConnectionManager? connectionManager,
        string gatewayUrl,
        CancellationToken cancellationToken)
    {
        if (_navigationStarted) return;

        try
        {
            Logger.Info("[ChatPage] Waiting for operator handshake before chat navigation");
            var ready = await ChatNavigationReadiness.WaitForOperatorHandshakeAsync(connectionManager, TimeSpan.FromSeconds(30), cancellationToken);
            if (!ready)
            {
                ShowChatReadinessFailure(LocalizationHelper.GetString("ChatPage_TimedOutHandshake"));
                Logger.Warn("[ChatPage] Timed out waiting for operator handshake before chat navigation");
                return;
            }

            Logger.Info("[ChatPage] Operator handshake ready; probing chat HTTP surface");
            ready = await ProbeChatSurfaceAsync(_chatUrl!, TimeSpan.FromSeconds(30), cancellationToken);
            if (!ready)
            {
                ShowChatReadinessFailure(string.Format(LocalizationHelper.GetString("ChatPage_TimedOutChat"), gatewayUrl));
                Logger.Warn("[ChatPage] Timed out waiting for chat HTTP surface before navigation");
                return;
            }

            WaitingStatusText.Text = LocalizationHelper.GetString("ChatPage_ChatReady");
            var app = (App)Application.Current;
            var bootstrapped = await OnboardingChatBootstrapper.BootstrapAsync(
                connectionManager?.OperatorClient,
                app.Settings,
                TimeSpan.FromSeconds(90),
                cancellationToken,
                registry: app.Registry).ConfigureAwait(true);
            if (!bootstrapped && !app.Settings.HasInjectedFirstRunBootstrap)
            {
                Logger.Warn("[ChatPage] Gateway hatching bootstrap did not complete; navigating to empty chat");
            }

            if (cancellationToken.IsCancellationRequested || _navigationStarted) return;

            _navigationStarted = true;
            ChatPagePanelStates.ApplyShowingWebView(PanelHost);
            Logger.Info("[ChatPage] Chat HTTP surface is serving; navigating WebView");
            if (!NavigateWebViewToCurrentChatUrl())
                ShowMissingChatCredentialError();
        }
        // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowChatReadinessFailure(string.Format(LocalizationHelper.GetString("ChatPage_ChatFailedToStart"), ex.Message));
            Logger.Warn($"[ChatPage] Chat readiness wait failed: {ex.Message}");
        }
    }

    private static async Task<bool> ProbeChatSurfaceAsync(string chatUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var attempts = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, chatUrl);
                using var response = await s_httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(true);

                if ((int)response.StatusCode is >= 200 and < 400)
                    return true;

                Logger.Warn($"[ChatPage] Chat readiness probe attempt {attempts} returned {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                Logger.Warn($"[ChatPage] Chat readiness probe attempt {attempts} failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(true);
        }

        return false;
    }

    private void ShowChatReadinessFailure(string message)
    {
        LoadingRing.IsActive = false;
        ChatPagePanelStates.ApplyShowingReadinessFailure(PanelHost);
        WaitingStatusText.Text = message;
    }

    private async Task CaptureVisualTestChatAsync()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return;
        if (WebView.CoreWebView2 == null) return;

        try
        {
            await Task.Delay(5000);
            var outputDir = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
            if (string.IsNullOrWhiteSpace(outputDir)) return;

            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"chat-{DateTime.Now:yyyyMMddHHmmss}.png");
            using var stream = new InMemoryRandomAccessStream();
            await WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Seek(0);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            Logger.Info($"[VisualTest] Captured chat WebView {path}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[VisualTest] Chat WebView capture failed: {ex.Message}");
        }
    }

    private static bool TryBuildChatUrl(string gatewayUrl, string token, out string url, out string errorMessage)
    {
        url = string.Empty;
        errorMessage = string.Empty;

        if (!GatewayUrlHelper.TryNormalizeWebSocketUrl(gatewayUrl, out var normalizedUrl) ||
            !Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var gatewayUri))
        {
            errorMessage = string.Format(LocalizationHelper.GetString("ChatPage_InvalidGatewayUrl"), gatewayUrl);
            return false;
        }

        var scheme = gatewayUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var builder = new UriBuilder(gatewayUri) { Scheme = scheme, Port = gatewayUri.Port };
        var baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
        url = $"{baseUrl}?token={Uri.EscapeDataString(token)}";
        return true;
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized && !string.IsNullOrEmpty(_chatUrl))
            WebView.CoreWebView2?.Navigate(_chatUrl);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized)
            WebView.CoreWebView2?.Reload();
    }

    private void OnDevTools(object sender, RoutedEventArgs e)
    {
        if (_webViewMode && _webViewInitialized)
            WebView.CoreWebView2?.OpenDevToolsWindow();
    }

    private void OnPopout(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_chatUrl)) return;
        try { Process.Start(new ProcessStartInfo(_chatUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"ChatPage: popout shell launch failed for url: {ex.Message}"); }
    }

    private void OnRetryChat(object sender, RoutedEventArgs e)
    {
        if (!_webViewInitialized || string.IsNullOrEmpty(_chatUrl))
            return;

        _navigationStarted = false;
        _navigationCts?.Cancel();
        _navigationCts = new CancellationTokenSource();
        ChatPagePanelStates.ApplyShowingRetryInProgress(PanelHost);
        LoadingRing.IsActive = true;
        _ = NavigateWhenChatReadyAsync(_connectionManager, CurrentApp.Registry?.GetById(CurrentApp.Registry.ActiveGatewayId ?? "")?.Url ?? "gateway", _navigationCts.Token);
    }

    private async Task<string?> VoiceTranscribeAsync(CancellationToken cancellationToken, Action? onRecordingStarted)
    {
        if (CurrentApp.Settings?.NodeSttEnabled != true)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffMessage"),
                LocalizationHelper.GetString("ChatVoiceDialog_OpenPermissionsSettings"),
                NavigateToPermissionsSettings);
            return null;
        }

        var voiceService = _hub?.VoiceServiceInstance;
        var host = _reactorHost;
        if (voiceService is null)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_InputOffMessage"),
                LocalizationHelper.GetString("ChatVoiceDialog_OpenPermissionsSettings"),
                NavigateToPermissionsSettings);
            return null;
        }

        // If the STT model isn't downloaded yet, prompt the user and open voice settings.
        if (!voiceService.IsModelDownloaded)
        {
            await ShowVoiceSettingsDialogAsync(
                LocalizationHelper.GetString("ChatVoiceDialog_ModelRequiredTitle"),
                LocalizationHelper.GetString("ChatVoiceDialog_ModelRequiredMessage"),
                LocalizationHelper.GetString("ChatVoiceDialog_OpenVoiceSettings"),
                NavigateToVoiceSettings);
            return null;
        }

        // Subscribe to streaming events during recording
        void OnTranscription(string text) => host?.SetVoiceTranscript(text);
        void OnAudioLevel(float level) => host?.SetVoiceAudioLevel(level);

        voiceService.TranscriptionReceived += OnTranscription;
        voiceService.AudioLevelChanged += OnAudioLevel;
        onRecordingStarted?.Invoke();
        try
        {
            var args = new SttListenArgs
            {
                TimeoutMs = 120_000,
                Language = ""
            };
            var result = await voiceService.ListenOnceAsync(args, cancellationToken);
            return result?.Text;
        }
        finally
        {
            voiceService.TranscriptionReceived -= OnTranscription;
            voiceService.AudioLevelChanged -= OnAudioLevel;
            host?.SetVoiceTranscript(null);
            host?.SetVoiceAudioLevel(0f);
        }
    }

    private async Task ReadChatTextAloudAsync(string text)
    {
        if (!await EnsureTtsReadyForChatAsync())
            return;

        await CurrentApp.SpeakChatTextAsync(text);
    }

    private async Task OnSpeakerMuteChangedAsync(bool muted)
    {
        if (!await _speakerMuteGate.WaitAsync(0))
            return;

        try
        {
            if (muted)
            {
                (App.Current as App)?.SetChatSpeakerMuted(true);
                return;
            }

            if (IsTtsReadyForChat())
            {
                (App.Current as App)?.SetChatSpeakerMuted(false);
                return;
            }

            (App.Current as App)?.SetChatSpeakerMuted(true);
            _reactorHost?.SetSpeakerMuted(true);
            await ShowTtsUnavailableDialogAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Speaker mute change failed: {ex.Message}");
        }
        finally
        {
            _speakerMuteGate.Release();
        }
    }

    private async Task<bool> EnsureTtsReadyForChatAsync()
    {
        if (IsTtsReadyForChat())
            return true;

        await ShowTtsUnavailableDialogAsync();
        return false;
    }

    private static bool IsTtsReadyForChat()
    {
        return SpeechSetupReadiness.IsChatTtsPlaybackReady(CurrentApp.Settings);
    }

    private async Task ShowTtsUnavailableDialogAsync()
    {
        await ShowVoiceSettingsDialogAsync(
            LocalizationHelper.GetString("ChatVoiceDialog_OutputOffTitle"),
            LocalizationHelper.GetString("ChatVoiceDialog_OutputOffMessage"),
            LocalizationHelper.GetString("ChatVoiceDialog_OpenPermissionsSettings"),
            NavigateToPermissionsSettings);
    }

    private static bool ShouldStartSpeakerMuted(SettingsManager? settings)
    {
        return !SpeechSetupReadiness.IsAutomaticChatTtsEnabled(settings);
    }

    private async Task ShowVoiceSettingsDialogAsync(string title, string message, string primaryButtonText, Action openSettings)
    {
        if (Interlocked.Exchange(ref _voiceSettingsDialogOpen, 1) == 1)
            return;

        var tcs = new TaskCompletionSource();
        if (DispatcherQueue is null || !DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = primaryButtonText,
                    CloseButtonText = LocalizationHelper.GetString("ChatVoiceDialog_Dismiss"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content?.XamlRoot
                };
                dialog.Opened += (s, _) =>
                {
                    if (s is ContentDialog d)
                    {
                        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(d.XamlRoot))
                        {
                            if (popup.Child is UIElement overlay && overlay != d)
                            {
                                overlay.Tapped += (_, _) => d.Hide();
                                break;
                            }
                        }
                    }
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    openSettings();
            }
            catch (InvalidOperationException ex)
            {
                Logger.Warn($"Voice settings dialog could not be shown: {ex.Message}");
            }
            finally
            {
                tcs.TrySetResult();
            }
        }))
        {
            Interlocked.Exchange(ref _voiceSettingsDialogOpen, 0);
            return;
        }

        try
        {
            await tcs.Task;
        }
        finally
        {
            Interlocked.Exchange(ref _voiceSettingsDialogOpen, 0);
        }
    }

    private void NavigateToVoiceSettings()
    {
        if (_hub is not null)
            _hub.NavigateTo("voice");
        else
            (App.Current as App)?.ShowHub("voice");
    }

    private void NavigateToPermissionsSettings()
    {
        if (_hub is not null)
            _hub.NavigateTo("permissions");
        else
            (App.Current as App)?.ShowHub("permissions");
    }

    private void OnAttachClicked()
    {
        Logger.Info("[ChatPage] OnAttachClicked invoked");
        _ = PickAndAttachFileAsync();
    }

    private async Task PickAndAttachFileAsync()
    {
        try
        {
            if (_hub is null)
            {
                Logger.Warn("[ChatPage] PickAndAttachFileAsync: _hub is null, cannot open picker");
                return;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle((Window)_hub!);
            var paths = await Win32FilePickerHelper.PickMultipleFilesAsync(hwnd, LocalizationHelper.GetString("ChatPage_AttachFile"));

            if (paths.Count == 0)
            {
                Logger.Info("[ChatPage] File picker cancelled by user");
                return;
            }

            var attachments = new List<ChatAttachment>(paths.Count);
            foreach (var path in paths)
            {
                Logger.Info($"[ChatPage] File selected: {path}");
                attachments.Add(await ChatAttachment.FromFileAsync(path));
            }
            _reactorHost?.AttachFiles(attachments);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Warn($"[ChatPage] Attachment rejected: {ex.Message}");
            await ShowAttachmentErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"[ChatPage] File picker error: {ex}");
        }
    }

    private async Task ShowAttachmentErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = LocalizationHelper.GetString("ChatPage_CannotAttachFile"),
                Content = message,
                CloseButtonText = LocalizationHelper.GetString("ChatPage_OK"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Logger.Debug($"ChatPage: dialog display failed (already logged upstream): {ex.Message}"); }
    }

    // WinUI adapter for ChatPagePanelStates. Maps the pure ChatPanelVisibility
    // enum onto Microsoft.UI.Xaml.Visibility on the named ChatPage panels so
    // panel-transition logic can be unit-tested without a WinUI runtime.
    //
    // All setters mutate UIElement.Visibility and therefore must run on the
    // UI thread; Debug.Assert enforces this in debug builds. The owning
    // ChatPage's PanelHost lazy init is also UI-thread-only by construction
    // (only called from event handlers and UI flows that already dispatch).
    private sealed class ChatPagePanelHost : IChatPagePanelHost
    {
        private readonly ChatPage _page;
        public ChatPagePanelHost(ChatPage page) => _page = page;

        public ChatPanelVisibility WebView
        {
            get => From(_page.WebView.Visibility);
            set { AssertUiThread(); _page.WebView.Visibility = To(value); }
        }

        public ChatPanelVisibility ErrorPanel
        {
            get => From(_page.ErrorPanel.Visibility);
            set { AssertUiThread(); _page.ErrorPanel.Visibility = To(value); }
        }

        public ChatPanelVisibility WaitingPanel
        {
            get => From(_page.WaitingPanel.Visibility);
            set { AssertUiThread(); _page.WaitingPanel.Visibility = To(value); }
        }

        public ChatPanelVisibility RetryChatButton
        {
            get => From(_page.RetryChatButton.Visibility);
            set { AssertUiThread(); _page.RetryChatButton.Visibility = To(value); }
        }

        public ChatPanelVisibility LoadingRing
        {
            get => From(_page.LoadingRing.Visibility);
            set { AssertUiThread(); _page.LoadingRing.Visibility = To(value); }
        }

        public ChatPanelVisibility PlaceholderPanel
        {
            get => From(_page.PlaceholderPanel.Visibility);
            set { AssertUiThread(); _page.PlaceholderPanel.Visibility = To(value); }
        }

        private void AssertUiThread()
        {
            // Null DispatcherQueue is itself an anomaly during a setter call
            // (means the page is detached or torn down) -- default to false
            // so the assert fires loudly instead of silently passing.
            System.Diagnostics.Debug.Assert(
                _page.DispatcherQueue?.HasThreadAccess ?? false,
                "ChatPagePanelHost mutated off the UI thread (or DispatcherQueue is null)");
        }

        private static ChatPanelVisibility From(Visibility v) =>
            v == Visibility.Visible ? ChatPanelVisibility.Visible : ChatPanelVisibility.Collapsed;

        private static Visibility To(ChatPanelVisibility v) =>
            v == ChatPanelVisibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
