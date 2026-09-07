using Microsoft.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClawTray.Chat;

public sealed record OpenClawReactorChatRootProps(
    IChatDataProvider Provider,
    ChatComposerSession ComposerSession,
    string? InitialThreadId = null,
    Func<string, Task>? OnReadAloud = null,
    Action? OnStopSpeaking = null,
    Action<string>? OnOpenCheckpoints = null,
    bool IsCompact = false);

/// <summary>
/// Production Reactor root for the native chat surface. It owns the provider
/// subscription and renders the message timeline and composer in one tree.
/// </summary>
public sealed class OpenClawReactorChatRoot : Component<OpenClawReactorChatRootProps>
{
    private static bool s_showToolCalls = true;
    private static int s_toolCallsCollapseVersion;
    private static event EventHandler? ToolCallsVisibilityChanged;

    private string? _pendingSelectedThreadId;

    public static void SetToolCallsVisible(bool visible)
    {
        if (s_showToolCalls == visible)
            return;

        if (!visible && s_showToolCalls)
            s_toolCallsCollapseVersion++;

        s_showToolCalls = visible;
        ToolCallsVisibilityChanged?.Invoke(null, EventArgs.Empty);
    }

    public override Element Render()
    {
        var props = Props;
        var (snapshot, setSnapshot) = UseState<ChatDataSnapshot?>(null, threadSafe: true);
        var initialSelection = props.InitialThreadId
            ?? (props.Provider as OpenClawChatDataProvider)?.CachedLastChatState?.DefaultThreadId;
        var (selectedId, setSelectedId) = UseState<string?>(initialSelection, threadSafe: true);
        var selectedIdRef = UseRef<string?>(initialSelection);
        selectedIdRef.Current = selectedId;
        var (scrollToBottomToken, setScrollToBottomToken) = UseState(0, threadSafe: true);
        var (showToolCalls, setShowToolCalls) = UseState(s_showToolCalls, threadSafe: true);
        var (toolCallsCollapseVersion, setToolCallsCollapseVersion) =
            UseState(s_toolCallsCollapseVersion, threadSafe: true);
        var (firstSendInFlight, setFirstSendInFlight) = UseState(false, threadSafe: true);

        UseEffect((Func<Action>)(() =>
        {
            EventHandler visibilityChanged = (_, _) =>
            {
                setShowToolCalls(s_showToolCalls);
                setToolCallsCollapseVersion(s_toolCallsCollapseVersion);
            };
            ToolCallsVisibilityChanged += visibilityChanged;
            return () => ToolCallsVisibilityChanged -= visibilityChanged;
        }), Array.Empty<object>());

        UseEffect((Func<Action>)(() =>
        {
            var provider = props.Provider;
            EventHandler<ChatDataChangedEventArgs> onChanged = (_, args) =>
            {
                setSnapshot(args.Snapshot);
                if (args.Snapshot.ComposeTarget.SessionKey is { } composeKey
                    && args.Snapshot.Timelines.TryGetValue(composeKey, out var timeline)
                    && timeline.Entries.Any(entry => entry.Kind == ChatTimelineItemKind.User))
                {
                    setFirstSendInFlight(false);
                }

                if (selectedIdRef.Current is null && args.Snapshot.DefaultThreadId is { } defaultThreadId)
                {
                    selectedIdRef.Current = defaultThreadId;
                    setSelectedId(defaultThreadId);
                }
            };

            provider.Changed += onChanged;
            _ = LoadAsync(
                provider,
                setSnapshot,
                () => selectedIdRef.Current,
                next =>
                {
                    selectedIdRef.Current = next;
                    setSelectedId(next);
                });
            return () => provider.Changed -= onChanged;
        }), props.Provider);

        if (snapshot is null)
            return RenderLoading();

        var selectedMaterializedThread = selectedId is null
            ? null
            : snapshot.Threads.FirstOrDefault(thread => string.Equals(thread.Id, selectedId, StringComparison.Ordinal));
        if (selectedMaterializedThread is null
            && selectedId is not null
            && snapshot.DefaultThreadId is { } fallbackId
            && ChatLifecycleSelectionPolicy.ShouldFallback(
                selectedId,
                _pendingSelectedThreadId,
                fallbackId))
        {
            selectedIdRef.Current = fallbackId;
            setSelectedId(fallbackId);
            selectedMaterializedThread = snapshot.Threads.FirstOrDefault(thread =>
                string.Equals(thread.Id, fallbackId, StringComparison.Ordinal));
        }

        var effectiveThread = selectedMaterializedThread ?? CreateComposeOnlyThread(props.Provider, snapshot);
        if (effectiveThread is { } selected && string.Equals(_pendingSelectedThreadId, selected.Id, StringComparison.Ordinal))
            _pendingSelectedThreadId = null;

        var connectionState = ToConnectionState(snapshot.ConnectionStatus);
        var isGatewayConnected = string.Equals(connectionState, "connected", StringComparison.Ordinal);
        if (isGatewayConnected
            && selectedMaterializedThread is not null
            && props.Provider is OpenClawChatDataProvider nativeProvider)
        {
            RunFireAndForget(ct => nativeProvider.LoadHistoryAsync(selectedMaterializedThread.Id, force: false, ct));
        }

        var timeline = effectiveThread is not null
            && snapshot.Timelines.TryGetValue(effectiveThread.Id, out var currentTimeline)
            ? currentTimeline
            : ChatTimelineState.Initial();
        var timelineGeneration = effectiveThread is not null
            && snapshot.TimelineGenerations?.TryGetValue(effectiveThread.Id, out var generation) == true
                ? generation
                : 0L;
        var historyRevision = effectiveThread is not null
            && snapshot.HistoryRevisions?.TryGetValue(effectiveThread.Id, out var revision) == true
                ? revision
                : 0L;
        var entryMetadata = effectiveThread is not null && props.Provider is OpenClawChatDataProvider metadataProvider
            ? metadataProvider.GetEntryMetadata(effectiveThread.Id)
            : null;
        var entries = (IReadOnlyList<ChatTimelineItem>)timeline.Entries;
        var queuedMessages = effectiveThread is not null
            && snapshot.QueuedMessagesByThread?.TryGetValue(effectiveThread.Id, out var queued) == true
                ? queued
                : Array.Empty<ChatQueuedMessage>();
        var hasPendingQueuedSend = queuedMessages.Any(message =>
            message.SendState is ChatQueuedMessageSendState.Queued or ChatQueuedMessageSendState.Sending);
        var currentTurnHasAssistant = false;
        for (var index = timeline.Entries.Count - 1; index >= 0; index--)
        {
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.User)
                break;
            if (timeline.Entries[index].Kind == ChatTimelineItemKind.Assistant)
            {
                currentTurnHasAssistant = true;
                break;
            }
        }

        var showThinking = timeline.TurnActive && !currentTurnHasAssistant;
        var isEmptyConversation = entries.Count == 0 && !showThinking && timeline.PendingPermission is null;
        var isComposeOnly = effectiveThread is not null && selectedMaterializedThread is null;
        var hasRealThreads = snapshot.Threads.Length > 0;
        var welcomeEligible = isEmptyConversation
            && isGatewayConnected
            && (
                (isComposeOnly && !hasRealThreads)
                || (!isComposeOnly && timeline.HistoryLoaded));
        var welcomeEligibilityKey = welcomeEligible
            ? $"{effectiveThread?.Id}|{isComposeOnly}|{timeline.HistoryLoaded}|{hasRealThreads}"
            : null;
        var welcomeEligibilityKeyRef = UseRef<string?>(welcomeEligibilityKey);
        welcomeEligibilityKeyRef.Current = welcomeEligibilityKey;
        var (settledWelcomeKey, setSettledWelcomeKey) = UseState<string?>(null, threadSafe: true);
        UseEffect((Func<Action>)(() =>
        {
            if (welcomeEligibilityKey is null)
            {
                setSettledWelcomeKey(null);
                return static () => { };
            }

            var cancelled = false;
            var expectedKey = welcomeEligibilityKey;
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                if (!cancelled
                    && string.Equals(
                        welcomeEligibilityKeyRef.Current,
                        expectedKey,
                        StringComparison.Ordinal))
                {
                    setSettledWelcomeKey(expectedKey);
                }
            });
            return () => cancelled = true;
        }),
            welcomeEligibilityKey);

        var emptyConversationIsAuthoritative = welcomeEligibilityKey is not null
            && string.Equals(
                settledWelcomeKey,
                welcomeEligibilityKey,
                StringComparison.Ordinal);
        var mode = effectiveThread is null
                   || (isEmptyConversation && !emptyConversationIsAuthoritative)
            ? ReactorChatTimelineMode.Loading
            : isEmptyConversation
                ? ReactorChatTimelineMode.Empty
                : ReactorChatTimelineMode.Timeline;
        Func<string, ChatMediaContentInfo, CancellationToken, Task<AssistantMediaResolutionResult>>?
            mediaResolver = props.Provider is OpenClawChatDataProvider dataProvider
                ? dataProvider.ResolveAssistantMediaAsync
                : null;

        var timelineProps = new ChatTimelinePresentationContext(
            effectiveThread?.Id,
            entries,
            false,
            null,
            entryMetadata,
            timelineGeneration,
            "OpenClaw Windows Tray",
            "Assistant",
            effectiveThread?.Model,
            showToolCalls
                ? ChatUsageFormatter.Format(entries, entryMetadata) ?? ChatUsageFormatter.Format(effectiveThread)
                : null,
            showThinking,
            showToolCalls,
            toolCallsCollapseVersion,
            props.OnReadAloud,
            props.OnStopSpeaking,
            scrollToBottomToken,
            effectiveThread is { } permissionThread
                ? (requestId, action) => OnPermission(permissionThread.Id, requestId, action)
                : null,
            mediaResolver);

        void SelectThread(string threadId)
        {
            _pendingSelectedThreadId = threadId;
            selectedIdRef.Current = threadId;
            setSelectedId(threadId);
            if (props.Provider is OpenClawChatDataProvider native)
                native.RememberSelectedThread(threadId);
        }

        // Bound once (idempotent) so the composer controller can hand a freshly
        // created "/new" session, or a session-picker selection, back to the root's
        // selection state without the controller depending on Reactor state directly.
        props.ComposerSession.Controller.BindSelectionHandoff(SelectThread);

        Action<string>? onSuggestionPicked = null;
        if (mode == ReactorChatTimelineMode.Empty && effectiveThread is { } suggestionThread)
        {
            onSuggestionPicked = suggestion =>
            {
                if (firstSendInFlight)
                    return;

                setFirstSendInFlight(true);
                setScrollToBottomToken(scrollToBottomToken + 1);
                ObserveFireAndForget(props.ComposerSession.Controller.SendCoreAsync(
                    suggestionThread.Id,
                    suggestionThread.Title,
                    suggestion,
                    Array.Empty<ChatAttachment>()));
            };
        }

        var timelineElement = Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            mode,
            timelineProps,
            onSuggestionPicked,
            firstSendInFlight,
            OnOpenCheckpoints: props.OnOpenCheckpoints,
            HistoryRevision: historyRevision));

        Element composerElement;
        if (effectiveThread is null)
        {
            composerElement = Empty();
        }
        else
        {
            var composerInputs = new ChatComposerInputs(
                ConnectionState: connectionState,
                TurnActive: timeline.TurnActive,
                CurrentThread: effectiveThread,
                AvailableChannels: VisibleChannels(snapshot.Threads, effectiveThread),
                AvailableModels: snapshot.AvailableModels,
                ModelChoices: snapshot.ModelChoices,
                MessageOptionsDisabled: timeline.TurnActive || hasPendingQueuedSend,
                QueuedMessages: queuedMessages,
                AvailableCommands: snapshot.AvailableCommands,
                CommandsSupported: snapshot.CommandsSupported);
            composerElement = Component<ReactorChatComposer, ReactorChatComposerViewProps>(new(
                props.ComposerSession,
                composerInputs,
                snapshot,
                () => setScrollToBottomToken(scrollToBottomToken + 1),
                props.IsCompact));
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            timelineElement.Grid(row: 0),
            composerElement.Grid(row: 1))
            .HAlign(HorizontalAlignment.Stretch)
            .VAlign(VerticalAlignment.Stretch);
    }

    private static Element RenderLoading() =>
        Component<ReactorChatTimeline, ReactorChatTimelineProps>(new(
            ReactorChatTimelineMode.Loading,
            new ChatTimelinePresentationContext(null, Array.Empty<ChatTimelineItem>(), false, null),
            null,
            false));

    private ChatThread? CreateComposeOnlyThread(
        IChatDataProvider provider,
        ChatDataSnapshot snapshot)
    {
        var composeKey = _pendingSelectedThreadId
            ?? (snapshot.ComposeTarget.IsReady ? snapshot.ComposeTarget.SessionKey : null);
        if (composeKey is null)
            return null;

        var cached = (provider as OpenClawChatDataProvider)?.CachedLastChatState;
        return new ChatThread
        {
            Id = composeKey,
            AgentId = snapshot.ComposeTarget.AgentId,
            Title = _pendingSelectedThreadId is null
                ? cached?.ThreadTitle ?? "OpenClaw Windows Tray"
                : LocalizationHelper.GetString("Chat_PendingNewSessionTitle"),
            Model = cached?.Model,
            ModelProvider = cached?.ModelProvider,
            Status = ChatThreadStatus.Running,
            Activity = ChatActivity.Idle,
        };
    }

    private static IReadOnlyList<ChatThread> VisibleChannels(ChatThread[] threads, ChatThread effectiveThread)
    {
        var visible = SessionVisibilityFilter.VisibleChatPickerThreads(threads, effectiveThread.Id)
            .Where(thread => !string.IsNullOrWhiteSpace(thread.Title)
                && thread.IsVisibleInSessionPicker(effectiveThread.Id))
            .ToList();
        if (!visible.Any(thread => string.Equals(thread.Id, effectiveThread.Id, StringComparison.Ordinal)))
            visible.Insert(0, effectiveThread);
        return visible;
    }

    private void OnPermission(string threadId, string requestId, string action) =>
        RunFireAndForget(ct => Props.Provider.RespondToPermissionAsync(threadId, requestId, action, ct));

    private static string ToConnectionState(string? value) =>
        value?.StartsWith("Incompatible", StringComparison.OrdinalIgnoreCase) == true
            ? "incompatible-gateway"
            : value?.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) == true
                ? "connected"
                : value?.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase) == true
                    ? "connecting"
                    : "disconnected";

    private static void RunFireAndForget(Func<CancellationToken, Task> operation)
    {
        _ = Task.Run(async () =>
        {
            try { await operation(CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        });
    }

    private static void ObserveFireAndForget(Task task)
    {
        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task operation)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[chat] operation failed: {ex}"); }
        }
    }

    private static async Task LoadAsync(
        IChatDataProvider provider,
        Action<ChatDataSnapshot?> setSnapshot,
        Func<string?> getSelected,
        Action<string?> setSelected)
    {
        try
        {
            var snapshot = await provider.LoadAsync();
            setSnapshot(snapshot);
            if (getSelected() is null && snapshot.DefaultThreadId is { } defaultThreadId)
                setSelected(defaultThreadId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[chat] load failed: {ex}");
        }
    }
}
