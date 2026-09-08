using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

#if OPENCLAW_TRAY_TESTS
// Shim for the test-only compilation. The real LocalizationHelper lives in
// OpenClaw.Tray.WinUI and depends on Microsoft.Windows.ApplicationModel.Resources
// which isn't available to the test project. Returning the resource key keeps
// the notification text identifiable in tests without pulling in WinAppSDK.
internal static class LocalizationHelper
{
    public static string GetString(string resourceKey) => resourceKey switch
    {
        "Chat_TruncationMarkerFormat" => " … [{0} bytes truncated]",
        "Chat_Permission_Allow" => "Allow once",
        "Chat_Permission_AllowAlways" => "Always allow",
        "Chat_Permission_Deny" => "Deny once",
        "Chat_Permission_CommandApprovalTitle" => "Command approval requested",
        "Chat_Permission_ResultSubmittedFormat" => "Approval {0} submitted for {1}.",
        "Chat_Error_SendReturnedStatusFormat" => "Gateway returned send status '{0}'.",
        "Chat_Error_SendFailedFormat" => "Send failed: {0}",
        _ => resourceKey
    };
}
#endif

/// <summary>
/// Adapts <see cref="IChatGatewayBridge"/> (which wraps a live
/// <see cref="OpenClawGatewayClient"/>) into the
/// <see cref="IChatDataProvider"/> contract consumed by the native chat components.
/// </summary>
/// <remarks>
/// Maps gateway signals into <see cref="ChatTimelineState"/> events:
/// <list type="bullet">
///   <item><c>SessionsUpdated</c> → rebuild <see cref="ChatThread"/> set.</item>
///   <item><c>chat.history</c> RPC → fold past messages into the timeline
///         (called automatically once per thread on first selection).</item>
///   <item><c>ChatMessageReceived</c> (role=assistant, final) →
///         <see cref="ChatMessageEvent"/> + <see cref="ChatTurnEndEvent"/>.</item>
///   <item><c>ChatMessageReceived</c> (role=user) → ignored (the local
///         <see cref="SendMessageAsync"/> already added the user entry).</item>
///   <item><c>AgentEventReceived</c> stream=assistant → streaming deltas
///         (<see cref="ChatMessageDeltaEvent"/>).</item>
///   <item><c>AgentEventReceived</c> stream=reasoning → reasoning entry
///         (<see cref="ChatReasoningEvent"/>/<see cref="ChatReasoningDeltaEvent"/>).</item>
///   <item><c>AgentEventReceived</c> stream=lifecycle phase=start/end/error →
///         <see cref="ChatThinkingEvent"/>/<see cref="ChatTurnEndEvent"/>/<see cref="ChatErrorEvent"/>.</item>
///   <item><c>AgentEventReceived</c> stream=tool/job → tool start/output/error
///         and turn-end timeline events.</item>
/// </list>
/// <para>
/// Active <c>runId</c>s are tracked per thread (set on lifecycle.start,
/// cleared on lifecycle.end) so <see cref="StopResponseAsync"/> can issue
/// a <c>chat.abort</c> RPC. Immutable session IDs returned by
/// <c>chat.history</c> are persisted per thread and forwarded on
/// subsequent <see cref="SendMessageAsync"/> calls.
/// </para>
/// </remarks>
public sealed class OpenClawChatDataProvider : IChatDataProvider
{
    internal const int MaxEntryTextBytes = 256 * 1024;
    private readonly IChatGatewayBridge _bridge;
    private readonly ChatTelemetryTracker _telemetry = new();
    private readonly ChatMetadataStore _metadataStore;
    private readonly ChatStatePersistence _persistence;
    private readonly ChatConversationState _state;
    private readonly ChatHistoryLoader _historyLoader;
    private readonly Action<Action>? _post;
    private readonly Func<Func<Task>, Task> _deferredAbortScheduler;
    private readonly object _publishGate = new();
    private readonly Dictionary<int, int> _deliveryDepthByThread = [];
    private readonly ManualResetEventSlim _disposeCompleted = new();
    private readonly List<ChatProviderNotification> _pendingPublishNotifications = [];
    private ChatDataSnapshot? _pendingPublishSnapshot;
    private int _activeDeliveries;
    private bool _publishScheduled;
    private bool _publishDisposed;

    /// <summary>Whether any thread is in an aborted state (suppress TTS/notifications).</summary>
    public bool IsResponseSuppressed => _state.IsResponseSuppressed;

    public string DisplayName => "OpenClaw gateway";

    /// <summary>Last-known chat state from a previous session, used for pre-connection UI.</summary>
    internal LastChatState? CachedLastChatState => _state.CachedLastChatState;

    public event EventHandler<ChatDataChangedEventArgs>? Changed;
    public event EventHandler<ChatProviderNotificationEventArgs>? NotificationRequested;

#if OPENCLAW_TRAY_TESTS
    internal Action<ChatDataSnapshot>? BeforePublishForTests { get; set; }
    internal bool PublishDisposedForTests
    {
        get
        {
            lock (_publishGate)
                return _publishDisposed;
        }
    }
#endif

    /// <param name="bridge">Adapter wrapping the live gateway client.</param>
    /// <param name="post">
    /// Optional UI-thread marshaling callback. Pass
    /// <c>action =&gt; dispatcherQueue.TryEnqueue(() =&gt; action())</c> from
    /// production code so that <see cref="Changed"/>/<see cref="NotificationRequested"/>
    /// callbacks observed by FunctionalUI components fire on the UI thread.
    /// When <c>null</c>, callbacks fire on whatever thread the gateway raised
    /// the source event on (acceptable in unit tests).
    /// </param>
    public OpenClawChatDataProvider(IChatGatewayBridge bridge, Action<Action>? post = null)
        : this(bridge, post, ChatMetadataStore.DefaultToolMetaCacheFilePath)
    {
    }

    internal OpenClawChatDataProvider(
        IChatGatewayBridge bridge,
        Action<Action>? post,
        string toolMetaCacheFilePath,
        string? attachmentMetaCacheFilePath = null,
        string? lastChatStateFilePath = null,
        TimeSpan? lastChatStateSaveDelay = null,
        Func<TimeSpan, CancellationToken, Func<Task>, Task>? historyRetryScheduler = null,
        Action? historyFailureReservedForTesting = null,
        Func<Func<Task>, Task>? deferredAbortScheduler = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _post = post;
        _deferredAbortScheduler =
            deferredAbortScheduler ?? (work => Task.Run(work));
        _metadataStore = new ChatMetadataStore(
            toolMetaCacheFilePath,
            attachmentMetaCacheFilePath);
        _persistence = new ChatStatePersistence(
            lastChatStateFilePath,
            lastChatStateSaveDelay);
        _state = new ChatConversationState(
            bridge.CurrentStatus,
            _persistence.InitialLastChatState,
            bridge.GetCurrentModelsList());
        _historyLoader = new ChatHistoryLoader(
            bridge,
            _state,
            _metadataStore,
            _persistence,
            _telemetry,
            historyRetryScheduler,
            historyFailureReservedForTesting);
        _historyLoader.Completed += OnHistoryLoadCompleted;

        _bridge.StatusChanged += OnStatusChanged;
        _bridge.SessionsUpdated += OnSessionsUpdated;
        _bridge.SessionCommandCompleted += OnSessionCommandCompleted;
        _bridge.ChatMessageReceived += OnChatMessageReceived;
        _bridge.AgentEventReceived += OnAgentEventReceived;
        _bridge.ModelsListUpdated += OnModelsListUpdated;

        // Bridge ctor may have been invoked AFTER the gateway client was
        // already Connected, in which case the StatusChanged → Connected
        // edge that would normally trigger the models.list / sessions.list
        // refresh was missed. Now that our handlers are wired, ask the
        // bridge to send those requests proactively so the composer's
        // channel + model dropdowns populate on first paint — without this,
        // the dropdowns sit on a single placeholder until the user sends
        // their first message.
        _bridge.StartProactiveBootstrap();
    }

    public Task<ChatDataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = _bridge.GetSessionList() ?? Array.Empty<SessionInfo>();
        return Task.FromResult(_state.Load(sessions, ProjectionContext()));
    }

    internal void RememberSelectedThread(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
            return;

        if (_state.RememberSelectedThread(threadId) is { } state)
            _persistence.SaveSelectedState(state);
    }

    // Explicit interface implementation (no attachments).
    Task IChatDataProvider.SendMessageAsync(string threadId, string message, CancellationToken cancellationToken)
        => SendMessageAsync(threadId, message, cancellationToken, attachments: null);

    public async Task SendMessageAsync(string threadId, string message, CancellationToken cancellationToken = default, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasAttachments = attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message) && !hasAttachments)
            throw new ArgumentException("Message or attachment is required.", nameof(message));

        var trimmed = message.Trim();
        var nonce = Guid.NewGuid().ToString("N");
        var attachmentPresentations = GatewayMediaMessageProjection.CreateLocalPresentations(
            attachments,
            static () => Guid.NewGuid().ToString("N"));
        var attachmentCorrelationSignature =
            GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(attachmentPresentations);

        // Cache image bytes only under collision-resistant keys carried by
        // local structured descriptors.
        if (hasAttachments)
        {
            for (var i = 0; i < Math.Min(attachments!.Count, attachmentPresentations.Count); i++)
            {
                var source = attachments[i];
                var presentation = attachmentPresentations[i];
                if (presentation.CanAccessPreviewCache && !string.IsNullOrEmpty(source.Content))
                {
                    if (!ChatImagePreviewCache.TryStoreBase64(
                            presentation.PreviewCacheKey!,
                            source.Content))
                    {
                        Logger.Debug(
                            $"ChatDataProvider: image attachment preview rejected for '{presentation.DisplayFileName}'");
                    }
                }
            }
        }

        // Build the display text for the user bubble. When attachments are
        // present, append a structured indicator line so the bubble is never
        // blank even if the typed message was empty. Uses a unique prefix
        // ("\u200B📎 " / "\u200B🖼️ ") with a zero-width space to prevent
        // false positives from normal user text.
        var safeUserText = GatewayMediaMessageProjection.NormalizeEchoCorrelationText(trimmed);
        var displayText = safeUserText;
        if (hasAttachments)
        {
            var chips = ChatMetadataStore.BuildAttachmentMarkerLines(attachments!);
            displayText = string.IsNullOrEmpty(safeUserText)
                ? chips
                : $"{safeUserText}\n{chips}";
        }

        var admission = _state.AdmitMessage(
            threadId,
            trimmed,
            displayText,
            nonce,
            attachments,
            DateTimeOffset.UtcNow,
            ProjectionContext(),
            timelineText: safeUserText,
            attachmentPresentations: attachmentPresentations,
            attachmentCorrelationSignature: attachmentCorrelationSignature);
        _telemetry.StartLocalTurn(
            admission.MessageId,
            threadId,
            queued: admission.Queued,
            admission.RuntimeGeneration);
        if (!_state.IsRuntimeGenerationCurrent(
                threadId,
                admission.RuntimeGeneration))
        {
            _telemetry.FinishByMessageId(
                admission.MessageId,
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.Superseded);
        }
        var dispatch = admission.Dispatch;
        var queueCompletion = dispatch is not null &&
                              dispatch.Request.LifecycleCommand is null
            ? _telemetry.PrepareDispatchLocalTurn(
                dispatch.Request.Id,
                dispatch.Request.SendRunId)
            : null;
        Publish(admission.Snapshot);

        if (dispatch is not null)
            await DispatchQueuedSendAsync(
                dispatch,
                queueCompletion,
                rethrow: true,
                cancellationToken);
    }

    internal Task<bool> EnqueueCompactCommandAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(threadId))
            throw new ArgumentException("Thread id is required.", nameof(threadId));

        var snapshot = _state.EnqueueCompact(
            threadId,
            DateTimeOffset.UtcNow,
            ProjectionContext());
        Publish(snapshot);
        TryDispatchNextQueuedSend(threadId);
        return Task.FromResult(true);
    }

    internal async Task<ChatLifecycleCommandResult> ExecuteLifecycleCommandAsync(
        string threadId,
        ChatLifecycleCommandKind command,
        CancellationToken cancellationToken = default)
    {
        ChatLifecycleCommandResult result;
        try
        {
            result = await new ChatLifecycleCommandDispatcher(_bridge)
                .ExecuteAsync(threadId, command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ChatLifecycleCommandResult(
                command,
                Succeeded: false,
                Error: $"The lifecycle command failed: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            // For /new timeouts, refresh the session list so the user can see
            // whether a session was created server-side before the response
            // arrived. The sessions.create protocol has no idempotency key,
            // so we cannot reliably auto-select the created session; the error
            // message guides the user to check the list manually.
            if (command == ChatLifecycleCommandKind.New)
            {
                try { await _bridge.RequestSessionsAsync().ConfigureAwait(false); }
                catch { /* best-effort reconciliation */ }
            }
            ApplyEventAndPublish(
                threadId,
                new ChatErrorEvent(result.Error ?? "The lifecycle command failed."));
            return result;
        }

        if (command == ChatLifecycleCommandKind.New)
        {
            try
            {
                await _bridge.RequestSessionsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ApplyEventAndPublish(
                    threadId,
                    new ChatStatusEvent($"The new session was created, but the session list could not refresh: {ex.Message}", ChatTone.Warning));
            }
        }
        else if (command == ChatLifecycleCommandKind.Compact)
        {
            _ = LoadHistoryAsync(threadId, force: true, authoritative: true);
        }
        else if (command == ChatLifecycleCommandKind.Reset)
        {
            ApplySuccessfulReset(threadId);
        }

        return result;
    }

    public Task<bool> CancelQueuedMessageAsync(string threadId, string queuedMessageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId))
            throw new ArgumentException("Thread id is required.", nameof(threadId));
        if (string.IsNullOrEmpty(queuedMessageId))
            throw new ArgumentException("Queued message id is required.", nameof(queuedMessageId));

        var (canceled, snapshot) = _state.CancelQueuedMessage(
            threadId,
            queuedMessageId,
            ProjectionContext());
        var telemetryCompletion = canceled
            ? _telemetry.PrepareFinishByMessageId(
                queuedMessageId,
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.QueuedCanceled)
            : null;
        _telemetry.CompletePreparedTurn(telemetryCompletion);
        if (snapshot is not null)
            Publish(snapshot);

        return Task.FromResult(canceled);
    }

    private async Task DispatchQueuedSendAsync(
        ChatQueuedSendDispatch dispatch,
        ChatTelemetryTracker.QueuePhaseCompletion? queueCompletion,
        bool rethrow,
        CancellationToken cancellationToken = default)
    {
        var request = dispatch.Request;
        if (request.LifecycleCommand is { } lifecycleCommand)
        {
            await DispatchQueuedLifecycleCommandAsync(
                dispatch,
                lifecycleCommand,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _telemetry.CompleteQueueDispatch(queueCompletion);
        var threadId = request.ThreadId;
        var hasAttachments = request.Attachments is { Count: > 0 };
        ChatTelemetryOperation? sendOperation = null;

        try
        {
            await AwaitPendingSessionOptionPatchAsync(threadId, cancellationToken);
            var preparation = _state.PrepareSendAttempt(
                dispatch,
                ProjectionContext());
            if (!preparation.IsCurrent)
            {
                if (preparation.Snapshot is not null)
                {
                    Publish(preparation.Snapshot);
                    TryDispatchNextQueuedSend(threadId);
                }
                return;
            }
            sendOperation = _telemetry.StartSendAttempt(request.Id);
            var sendResult = await _bridge.SendChatMessageForRunAsync(
                request.Text,
                threadId,
                dispatch.SessionId,
                request.Attachments,
                idempotencyKey: request.SendRunId);
            var admissionStatus = ToTelemetryAdmissionStatus(
                ChatSendQueuePolicy.ClassifyAdmission(sendResult));
            var admissionOutcome = admissionStatus == ChatAdmissionTelemetryStatus.Canceled
                ? ChatTelemetryOutcome.Canceled
                : sendResult.IsTerminalFailure
                    ? ChatTelemetryOutcome.Failure
                    : ChatTelemetryOutcome.Success;
            _telemetry.FinishSendAttempt(
                sendOperation,
                admissionStatus,
                admissionOutcome);
            if (admissionStatus == ChatAdmissionTelemetryStatus.Accepted)
                _telemetry.ObserveAdmissionAccepted(request.Id);
            if (sendResult.IsTerminalFailure)
            {
                var rejectedCompletion = _telemetry.PrepareFinishByMessageId(
                    request.Id,
                    admissionOutcome,
                    ChatTurnTelemetryReason.SendRejected);
                _telemetry.CompletePreparedTurn(rejectedCompletion);
                var failure = !string.IsNullOrWhiteSpace(sendResult.Error)
                    ? sendResult.Error!
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        LocalizationHelper.GetString("Chat_Error_SendReturnedStatusFormat"),
                        sendResult.Status);
                throw new InvalidOperationException(failure);
            }

            var acceptedRunId = string.IsNullOrWhiteSpace(sendResult.RunId)
                ? null
                : sendResult.RunId!;
            var commit = _state.CommitSendResult(
                dispatch,
                sendResult,
                ProjectionContext());
            if (commit.BindAcceptedRun && acceptedRunId is not null)
                _telemetry.BindAcceptedRun(request.Id, acceptedRunId);
            if (commit.RequeueRequired)
                _telemetry.RequeueLocalTurn(request.Id);
            HandleOpenedLifecycle(
                threadId,
                commit.OpenedLifecycle,
                commit.RuntimeGeneration);
            ChatTelemetryTracker.PreparedTurnCompletion? staleCompletion = null;
            if (!commit.IsCurrent)
            {
                staleCompletion = _telemetry.PrepareFinishByMessageId(
                    request.Id,
                    ChatTelemetryOutcome.Canceled,
                    ChatTurnTelemetryReason.Superseded);
            }

            if (commit.AcceptedSnapshot is not null)
                Publish(commit.AcceptedSnapshot);
            if (commit.RequeuedSnapshot is not null)
                Publish(commit.RequeuedSnapshot);
            if (commit.RetryDeferredSend)
                ScheduleQueuedSendDrain(threadId, commit.DeferredRetryDelay);

            if (commit.StaleRunIdToAbort is not null)
            {
                _telemetry.CompletePreparedTurn(staleCompletion);
                try
                {
                    Logger.Info($"[Reset] Aborting late pre-reset send runId='{commit.StaleRunIdToAbort}' threadId='{threadId}'");
                    await _bridge.SendChatAbortAsync(commit.StaleRunIdToAbort, threadId);
                }
                catch (Exception abortEx)
                {
                    Logger.Warn($"[Reset] Failed to abort late pre-reset send runId='{commit.StaleRunIdToAbort}': {abortEx.Message}");
                }
            }
            if (!commit.IsCurrent && commit.AcceptedSnapshot is not null)
                TryDispatchNextQueuedSend(threadId);

            if (hasAttachments && commit.IsCurrent)
            {
                _metadataStore.CacheAttachments(
                    threadId,
                    dispatch.SessionId,
                    dispatch.ResetVersion,
                    request.Text,
                    request.Attachments!,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
        catch (Exception ex)
        {
            _telemetry.FinishSendAttempt(
                sendOperation,
                ChatAdmissionTelemetryStatus.Exception,
                ex is OperationCanceledException
                    ? ChatTelemetryOutcome.Canceled
                    : ChatTelemetryOutcome.Failure,
                ex);
            var failure = _state.FailSend(
                dispatch,
                ex.Message,
                string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationHelper.GetString("Chat_Error_SendFailedFormat"),
                    ex.Message),
                ProjectionContext());
            if (!failure.IsCurrent)
            {
                if (failure.Snapshot is not null)
                {
                    Publish(failure.Snapshot);
                    TryDispatchNextQueuedSend(threadId);
                }
                return;
            }

            var rejectedCompletion = _telemetry.PrepareFinishByMessageId(
                request.Id,
                ex is OperationCanceledException
                    ? ChatTelemetryOutcome.Canceled
                    : ChatTelemetryOutcome.Failure,
                ChatTurnTelemetryReason.SendRejected);
            _telemetry.CompletePreparedTurn(rejectedCompletion);
            Logger.Warn($"[Queue] chat.send failed threadId='{threadId}' queuedMessageId='{request.Id}' sendRunId='{request.SendRunId}': {ex.Message}");
            // Surface as an error in the timeline + notification, while the
            // failed queue card keeps the attempted text visible for retry/edit.
            Publish(failure.Snapshot!);
            RaiseNotification(new ChatProviderNotification(
                ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_SendFailed"), ex.Message));
            TryDispatchNextQueuedSend(threadId);
            if (rethrow)
                throw;
        }
    }

    private async Task DispatchQueuedLifecycleCommandAsync(
        ChatQueuedSendDispatch dispatch,
        ChatLifecycleCommandKind command,
        CancellationToken cancellationToken)
    {
        var request = dispatch.Request;
        var threadId = request.ThreadId;
        if (!_state.IsQueuedDispatchCurrent(dispatch))
            return;

        ChatLifecycleCommandResult result;
        try
        {
            result = await new ChatLifecycleCommandDispatcher(_bridge)
                .ExecuteAsync(threadId, command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new ChatLifecycleCommandResult(
                command,
                Succeeded: false,
                Error: "The queued lifecycle command was canceled.");
        }
        catch (Exception ex)
        {
            result = new ChatLifecycleCommandResult(
                command,
                Succeeded: false,
                Error: ex.Message);
        }

        var completion = _state.CompleteQueuedLifecycle(
            dispatch,
            result.Succeeded,
            result.Error,
            ProjectionContext());
        if (!completion.Succeeded)
            return;

        if (completion.Snapshot is not null)
            Publish(completion.Snapshot);
        if (result.Succeeded && command == ChatLifecycleCommandKind.Compact)
            _ = LoadHistoryAsync(threadId, force: true, authoritative: true);
        TryDispatchNextQueuedSend(threadId);
    }

    private static ChatAdmissionTelemetryStatus ToTelemetryAdmissionStatus(
        ChatAdmissionOutcome outcome) => outcome switch
    {
        ChatAdmissionOutcome.Accepted => ChatAdmissionTelemetryStatus.Accepted,
        ChatAdmissionOutcome.Deferred => ChatAdmissionTelemetryStatus.Deferred,
        ChatAdmissionOutcome.Rejected => ChatAdmissionTelemetryStatus.Rejected,
        ChatAdmissionOutcome.Canceled => ChatAdmissionTelemetryStatus.Canceled,
        _ => ChatAdmissionTelemetryStatus.Other,
    };

    public async Task StopResponseAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var abort = _state.BeginAbort(threadId);
        var runId = abort.RunId;
        var hadActiveTurn = abort.HadActiveTurn;
        _telemetry.FinishActiveTurn(
            threadId,
            ChatTelemetryOutcome.Canceled,
            ChatTurnTelemetryReason.AbortRequested);

        Logger.Info($"[ABORT] StopResponseAsync threadId='{threadId}' runId='{runId ?? "(null)"}' hadActiveTurn={hadActiveTurn} deferred={string.IsNullOrEmpty(runId)}");

        if (!string.IsNullOrEmpty(runId))
        {
            try
            {
                Logger.Info($"[ABORT] Sending chat.abort for runId='{runId}'");
                await _bridge.SendChatAbortAsync(runId, threadId);
                Logger.Info($"[ABORT] chat.abort sent successfully");
            }
            catch (Exception ex)
            {
                _state.RollbackAbort(threadId, runId);
                Logger.Warn($"[ABORT] chat.abort failed, cleared suppression: {ex.Message}");
                RaiseNotification(new ChatProviderNotification(
                    ChatProviderNotificationKind.Error, threadId, LocalizationHelper.GetString("Chat_Notification_AbortFailed"), ex.Message));
                ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
                return;
            }

        }
        else
        {
            Logger.Info($"[ABORT] No runId yet — queued pending abort for threadId='{threadId}'");
        }

        // Persist is handled by the deferred abort path (lifecycle.start or
        // lifecycle.end) which runs after the gateway has recorded the message.

        // If there was a real in-flight turn, mark the partial assistant text
        // as aborted so users can tell it isn't a complete response (per spec
        // Edge Cases — "Aborted runs: Show with abort indicator").
        if (hadActiveTurn)
        {
            ApplyEventAndPublish(threadId, new ChatStatusEvent("Aborted", ChatTone.Warning));
        }

        _state.CompleteAbort(threadId, runId);

        // Always clear local "turn active" state — the gateway will emit a
        // lifecycle.end if the abort succeeds, but we want the UI to reflect
        // the user's intent immediately.
        ApplyEventAndPublish(
            threadId,
            new ChatTurnEndEvent(RetainToolCorrelations: false));
    }

    /// <summary>
    /// Fetch the conversation transcript for <paramref name="threadId"/> from
    /// the gateway (via <c>chat.history</c>) and fold it into the local
    /// timeline. Idempotent — the first successful call per thread populates
    /// the timeline; subsequent calls are no-ops unless <paramref name="force"/>
    /// is true. Safe to call from any thread.
    /// </summary>
    public Task LoadHistoryAsync(
        string threadId,
        bool force = false,
        CancellationToken cancellationToken = default,
        bool authoritative = false) =>
        _historyLoader.LoadAsync(
            threadId,
            force,
            cancellationToken,
            authoritative);

    internal Task ReplaceHistoryAfterCheckpointRestoreAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var transition = _state.BeginHistoryReplacement(
            threadId,
            ProjectionContext());
        if (transition is null)
            return Task.CompletedTask;

        Publish(transition.Snapshot);
        return _historyLoader.LoadReplacementAsync(
            threadId,
            transition.Token,
            cancellationToken);
    }

    private void OnHistoryLoadCompleted(
        object? sender,
        ChatHistoryLoadResult result)
    {
        void Deliver()
        {
            var snapshot = _state.SnapshotIfHistoryTokenCurrent(
                result.Token,
                ProjectionContext());
            if (snapshot is null)
                return;
            if (result.PublishSnapshot)
            {
                Changed?.Invoke(this, new ChatDataChangedEventArgs(snapshot));
                if (snapshot.Threads.Length > 0 ||
                    snapshot.AvailableModels.Length > 0)
                {
                    _persistence.DebounceSnapshot(snapshot);
                }
            }
            if (result.Notification is not null)
            {
                NotificationRequested?.Invoke(
                    this,
                    new ChatProviderNotificationEventArgs(result.Notification));
            }
        }

        if (_post is null)
            Deliver();
        else
            _post(Deliver);
    }

    public Task SetThreadSuspendedAsync(string threadId, bool suspended, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask; // Not supported by gateway — no-op.
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask; // Not supported by gateway — no-op.
    }

    public async Task SetModelAsync(string threadId, string model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The gateway's sessions.patch schema treats `model` as a non-empty
        // string; a blank value here is a no-op rather than a clear. Use
        // ClearModelAsync to revert a session to the gateway default.
        if (string.IsNullOrWhiteSpace(model)) return;
        await TrackSessionOptionPatchAsync(threadId, () => _bridge.PatchSessionModelAsync(threadId, model));
    }

    public async Task ClearModelAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Tri-state clear: removes the session's model override (explicit null)
        // so it tracks the gateway/agent default again.
        await TrackSessionOptionPatchAsync(threadId, () => _bridge.ClearSessionModelAsync(threadId));
    }

    private async Task TrackSessionOptionPatchAsync(string threadId, Func<Task> patchOperation)
    {
        var lease = _state.BeginSessionOptionPatch(threadId);
        Exception? failure = null;
        try
        {
            if (lease.Previous is not null)
            {
                try { await lease.Previous; }
                catch (Exception ex)
                {
                    Logger.Debug($"ChatDataProvider: continuing session option patch after previous patch failed: {ex.Message}");
                }
            }
            await patchOperation();
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            _state.CompleteSessionOptionPatch(lease, failure);
        }
    }

    private async Task AwaitPendingSessionOptionPatchAsync(string threadId, CancellationToken cancellationToken)
    {
        var pending = _state.GetPendingSessionOptionPatch(threadId);
        if (pending is not null)
        {
            try { await pending.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Debug($"ChatDataProvider: continuing send after session option patch failed: {ex.Message}");
            }
        }
    }

    public async Task SetThinkingLevelAsync(string threadId, string thinkingLevel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await TrackSessionOptionPatchAsync(
            threadId,
            () => _bridge.PatchSessionThinkingLevelAsync(threadId, thinkingLevel));
    }

    public async Task ClearThinkingLevelAsync(string threadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await TrackSessionOptionPatchAsync(
            threadId,
            () => _bridge.ClearSessionThinkingLevelAsync(threadId));
    }

    public async Task EnsureCommandCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_state.TryBeginCommandCatalogFetch(out var epoch))
            return;

        CommandCatalog catalog;
        try
        {
            // Chat composer slash completion can only insert text-invokable
            // commands. Request the protocol's text scope so native-only
            // commands never surface in the composer catalog.
            catalog = await _bridge.ListCommandsAsync(new CommandCatalogQuery { Scope = "text" }).ConfigureAwait(false)
                      ?? new CommandCatalog { IsSupported = true };
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatProvider] EnsureCommandCatalogAsync failed: {ex.Message}");
            if (_state.FailCommandCatalogFetch(epoch))
                PublishCommandCatalogIfFresh(epoch);
            return;
        }

        if (!_state.CompleteCommandCatalogFetch(epoch, catalog))
            return;
        Logger.Info($"[ChatProvider] commands.list: supported={catalog.IsSupported} count={catalog.Commands.Count}");
        // Re-validate freshness at UI-thread delivery time rather than
        // publishing a snapshot captured under the lock above. This closes the
        // window where a disconnect occurring between snapshot build and
        // Publish could let a stale "connected + commands" snapshot arrive after
        // the disconnect snapshot.
        PublishCommandCatalogIfFresh(epoch);
    }

    /// <summary>
    /// Publishes a freshly-built snapshot on the UI thread, but only if the
    /// connection <paramref name="epoch"/> captured for this commands.list fetch
    /// is still current when delivery runs. If a disconnect/reconnect superseded
    /// the fetch in the meantime, the stale publish is dropped (the status
    /// handler's own publish carries the authoritative state).
    /// </summary>
    private void PublishCommandCatalogIfFresh(int epoch)
    {
        void Deliver()
        {
            var snapshot = _state.SnapshotCommandCatalogIfFresh(
                epoch,
                ProjectionContext());
            if (snapshot is not null)
                Changed?.Invoke(this, new ChatDataChangedEventArgs(snapshot));
        }

        if (_post is null)
            Deliver();
        else
            _post(Deliver);
    }

    public Task SetPermissionModeAsync(string threadId, bool allowAll, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string threadId, string requestId, bool allow, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(
            threadId,
            requestId,
            allow ? ChatPermissionActionKeys.AllowOnce : ChatPermissionActionKeys.Deny,
            cancellationToken);

    public async Task RespondToPermissionAsync(string threadId, string requestId, string action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(requestId))
            return;

        var decision = NormalizeApprovalAction(action);
        // Use the operator-approvals gateway RPC (``exec.approval.resolve``)
        // rather than the ``/approve <id> <decision>`` chat slash command.
        //
        // Why: slash commands are processed as ordinary chat input on the
        // agent's main turn — but when an exec approval is pending, the agent
        // is BLOCKED waiting on that approval. The slash command therefore
        // sits in the input queue until the run times out, by which point the
        // approval has already expired and the approve/deny is a no-op. The
        // RPC bypasses the chat queue and resolves the approval immediately.
        Logger.Info($"[Approval] user response requestId={requestId} decision={decision} thread='{threadId}'");

        try
        {
            await _bridge.ResolveExecApprovalAsync(requestId, decision);
        }
        catch (Exception ex)
        {
            // Send failed: leave the Allow/Deny banner up so the user can
            // retry. Clearing it on failure would silently swallow the
            // problem and leave the agent waiting on an approval that the
            // user has no way to re-issue.
            Logger.Warn($"[Approval] response send failed requestId={requestId}: {ex.Message} (banner preserved for retry)");
            return;
        }

        ClearPendingPermissionAndPublish(threadId, expectedRequestId: requestId,
            decision: ChatDecisionForApprovalAction(decision));
    }

    private static string FormatApprovalResult(string decision, string detail, string requestId)
        => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("Chat_Permission_ResultSubmittedFormat"),
            LabelForApprovalAction(decision),
            string.IsNullOrWhiteSpace(detail) ? requestId : detail);

    private static string LabelForApprovalAction(string decision)
    {
        if (string.Equals(decision, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase))
            return LocalizationHelper.GetString("Chat_Permission_AllowAlways");
        if (string.Equals(decision, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase))
            return LocalizationHelper.GetString("Chat_Permission_Allow");
        return LocalizationHelper.GetString("Chat_Permission_Deny");
    }

    private static ChatTone ApprovalToneForDecision(string decision)
        => string.Equals(decision, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
            ? ChatTone.Warning
            : ChatTone.Success;

    private string NormalizeApprovalAction(string? action)
    {
        if (string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase))
            return ChatPermissionActionKeys.AllowAlways;
        if (string.Equals(action, ChatPermissionActionKeys.AllowOnce, StringComparison.OrdinalIgnoreCase))
            return ChatPermissionActionKeys.AllowOnce;
        if (!string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase))
            Logger.Warn($"[Approval] unknown action '{action ?? "<null>"}'; defaulting to deny");
        return ChatPermissionActionKeys.Deny;
    }

    private static ChatPermissionDecision ChatDecisionForApprovalAction(string action)
        => string.Equals(action, ChatPermissionActionKeys.AllowAlways, StringComparison.OrdinalIgnoreCase)
            ? ChatPermissionDecision.AllowedAlways
            : string.Equals(action, ChatPermissionActionKeys.Deny, StringComparison.OrdinalIgnoreCase)
                ? ChatPermissionDecision.Denied
                : ChatPermissionDecision.Allowed;

    // expectedRequestId: when non-null, the clear is a no-op unless the
    // currently-pending banner's RequestId matches. This protects against
    // the responder-race where a fresh approval arrives between the
    // user's tap and the post-send clear.
    //
    // decision: terminal state to stamp on the matching inline timeline
    // entry. The user's local Allow/Deny click passes Allowed/Denied so
    // the bubble collapses to the correct badge immediately. The
    // backstop path triggered by the gateway echo passes Expired, which
    // only takes effect if the user hasn't already decided locally
    // (ResolvePermission protects already-decided entries).
    private void ClearPendingPermissionAndPublish(string threadId, string? expectedRequestId = null,
        ChatPermissionDecision decision = ChatPermissionDecision.Expired)
    {
        var pendingId = _state.PendingPermissionId(threadId);
        if (pendingId is null)
        {
            Logger.Info($"[Approval] clear requested but no PendingPermission for thread='{threadId}'");
            return;
        }
        if (expectedRequestId is not null &&
            !string.Equals(pendingId, expectedRequestId, StringComparison.Ordinal))
        {
            Logger.Info($"[Approval] clear skipped — pending is '{pendingId}', expected '{expectedRequestId}' (newer approval superseded)");
            return;
        }
        Logger.Info($"[Approval] clearing PendingPermission requestId='{pendingId}' on thread='{threadId}' decision={decision}");
        var snapshot = _state.ClearPendingPermission(
            threadId,
            expectedRequestId,
            decision,
            ProjectionContext());
        Publish(snapshot);
    }

    public ValueTask DisposeAsync()
    {
        var transition = _state.DisposeState();
        if (!transition.IsFirstDispose)
        {
            if (IsDeliveringOnCurrentThread())
                return ValueTask.CompletedTask;
            _disposeCompleted.Wait();
            WaitForInFlightDeliveries();
            return ValueTask.CompletedTask;
        }

        try
        {
            lock (_publishGate)
            {
                _publishDisposed = true;
                _pendingPublishSnapshot = null;
                _pendingPublishNotifications.Clear();
                _publishScheduled = false;
            }
            WaitForInFlightDeliveries();
            _persistence.SaveSnapshot(_state.Snapshot(ProjectionContext()));
            _telemetry.FinishAll(
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.Disposed);
            _historyLoader.Completed -= OnHistoryLoadCompleted;
            _historyLoader.Dispose();
            _metadataStore.Dispose();
            _persistence.Dispose();
            _bridge.StatusChanged -= OnStatusChanged;
            _bridge.SessionsUpdated -= OnSessionsUpdated;
            _bridge.SessionCommandCompleted -= OnSessionCommandCompleted;
            _bridge.ChatMessageReceived -= OnChatMessageReceived;
            _bridge.AgentEventReceived -= OnAgentEventReceived;
            _bridge.ModelsListUpdated -= OnModelsListUpdated;
            _bridge.Dispose();
            return ValueTask.CompletedTask;
        }
        finally
        {
            _disposeCompleted.Set();
        }
    }

    /// <summary>
    /// Snapshot of per-entry metadata for one thread, defensively copied so
    /// callers (typically the renderer) can read it concurrently with future
    /// adapter mutations. Returns an empty dictionary if nothing is tracked.
    /// </summary>
    public IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadata(string threadId)
        => _state.GetEntryMetadata(threadId);

    internal Task<AssistantMediaResolutionResult> ResolveAssistantMediaAsync(
        string sessionKey,
        ChatMediaContentInfo media,
        CancellationToken cancellationToken) =>
        _bridge.ResolveAssistantMediaAsync(sessionKey, media, cancellationToken);

    // ── Event handlers ──

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (_state.IsDisposed)
            return;
        var transition = _historyLoader.ApplyStatusAndAdvanceGeneration(
            status,
            ProjectionContext());
        if (transition.Reconnected || transition.Disconnected)
        {
            _telemetry.FinishBeforeConnectionGeneration(
                transition.HistoryGeneration,
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.Disconnected);
        }
        Publish(transition.Snapshot);

        var interruptedMessage = LocalizationHelper.GetString(
            "Chat_Notification_ConnectionInterrupted");
        foreach (var threadId in transition.InterruptedThreads)
        {
            ApplyEventAndPublish(
                threadId,
                new ChatStatusEvent(interruptedMessage, ChatTone.Warning));
            ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
        }
        if (transition.Disconnected)
            _state.ClearToolReplayState();
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        if (_state.IsDisposed)
            return;
        var transition = _state.ApplySessions(
            sessions ?? [],
            ProjectionContext());
        Publish(transition.Snapshot);

        foreach (var threadId in transition.QueuedThreadsToDrain)
        {
            TryDispatchNextQueuedSend(threadId);
        }
    }

    internal static bool ShouldPreserveLiveEntryDuringAuthoritativeReload(
        ChatEntryMetadata? metadata,
        int maxHistorySequence,
        DateTimeOffset historyRequestStartedAt) =>
        ChatConversationState.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            metadata,
            maxHistorySequence,
            historyRequestStartedAt);

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        if (_state.IsDisposed)
            return;
        if (result is not { Ok: true } || string.IsNullOrWhiteSpace(result.Key))
        {
            return;
        }

        if (string.Equals(result.Method, "sessions.compact", StringComparison.Ordinal))
        {
            _ = LoadHistoryAsync(result.Key, force: true, authoritative: true);
            return;
        }

        if (!string.Equals(result.Method, "sessions.reset", StringComparison.Ordinal))
            return;

        ApplySuccessfulReset(result.Key);
    }

    private void ApplySuccessfulReset(string threadId)
    {
        var transition = _state.ResetThread(
            threadId,
            ProjectionContext());
        _historyLoader.ApplyReset(
            threadId,
            transition.ResetGeneration);
        _telemetry.FinishThreadBeforeResetGeneration(
            threadId,
            transition.ResetGeneration,
            ChatTelemetryOutcome.Canceled,
            ChatTurnTelemetryReason.Reset);
        Publish(transition.Snapshot);
        if (_persistence.ApplyReset(
                transition.ThreadId,
                transition.ResetGeneration))
        {
            _persistence.SaveAbortedIds();
        }
        _metadataStore.EvictReset(
            transition.ThreadId,
            transition.OldSessionId,
            transition.ResetGeneration);
        AbortSubmittedRunsAfterReset(threadId, transition.SubmittedRunIds);
    }

    private void AbortSubmittedRunsAfterReset(string threadId, IReadOnlyList<string> runIds)
    {
        if (runIds.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            foreach (var runId in runIds)
            {
                if (string.IsNullOrWhiteSpace(runId))
                    continue;

                try
                {
                    Logger.Info($"[Reset] Sending chat.abort for pre-reset submitted runId='{runId}' threadId='{threadId}'");
                    await _bridge.SendChatAbortAsync(runId, threadId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Reset] chat.abort failed for pre-reset runId='{runId}' threadId='{threadId}': {ex.Message}");
                }
            }
        });
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo info)
    {
        if (_state.IsDisposed)
            return;
        var snapshot = _state.ApplyModels(info, ProjectionContext());
        Logger.Info($"[ChatBridge] OnModelsListUpdated: count={snapshot.AvailableModels.Length}");
        Publish(snapshot);
    }

    private void OnChatMessageReceived(object? sender, ChatMessageInfo message)
    {
        if (message is null || _state.IsDisposed)
            return;
        if (string.IsNullOrEmpty(message.SessionKey))
        {
            Logger.Warn($"[ChatProvider] Dropping chat message with empty sessionKey (role={message.Role})");
            RaiseKeylessEventDiagnosticOnce();
            return;
        }

        var traceText = message.Text ?? string.Empty;
        Logger.Info(
            $"[ChatTrace] chat.message thread='{message.SessionKey}' role='{message.Role}' " +
            $"final={message.IsFinal} len={traceText.Length} h={ChatContentFormatting.ChatTraceHash(traceText)}");
        var threadId = message.SessionKey;
        var role = message.Role?.ToLowerInvariant() ?? string.Empty;
        var rawText = message.Text ?? string.Empty;
        var projection = role == "user"
            ? GatewayMediaMessageProjection.Project(rawText)
            : null;
        var gate = _state.GateIncomingChatMessage(message, ProjectionContext(), projection);
        HandleOpenedLifecycle(
            threadId,
            gate.OpenedLifecycle,
            gate.RuntimeGeneration);
        if (gate.Suppressed)
        {
            Logger.Debug($"[ABORT] Suppressed ChatMessage for threadId='{threadId}' (role={message.Role})");
            return;
        }
        if (gate.Drop)
        {
            if (gate.Snapshot is not null)
                Publish(gate.Snapshot);
            if (gate.RequestRemoteBackfill)
                _ = FetchRemoteUserMessageAsync(threadId, openResetGateOnSuccess: true);
            Logger.Debug($"[Reset] Dropping stale chat message after reset for threadId='{threadId}' role='{role}'");
            return;
        }

        if (role == "system" &&
            string.Equals(message.OpenClawKind, "compaction", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(message.Text))
        {
            ApplyEventAndPublish(
                threadId,
                new ChatStatusEvent(
                    ChatContentFormatting.TruncateForChatEntry(message.Text),
                    ChatTone.Dim),
                _state.BuildLiveMetadata(
                    threadId,
                    message.Ts,
                    message.OpenClawId,
                    message.OpenClawSeq,
                    openClawKind: message.OpenClawKind,
                    compactionTokensBefore: message.CompactionTokensBefore,
                    compactionTokensAfter: message.CompactionTokensAfter));
            return;
        }

        if (role == "user")
        {
            if (ChatContentFormatting.LooksLikeApprovalSlashCommand(rawText))
            {
                var echo = _state.ConsumeLocalEcho(
                    message,
                    removeQueuedMessage: true,
                    ProjectionContext(),
                    projection);
                if (echo.Consumed)
                {
                    return;
                }
                ApplyEventAndPublish(
                    threadId,
                    new ChatStatusEvent(rawText.Trim(), ChatTone.Dim),
                    _state.BuildLiveMetadata(threadId, message.Ts));
                return;
            }
            if (NativeToolProjector.LooksLikeSystemControlNote(rawText))
            {
                if (!string.IsNullOrEmpty(message.Text))
                {
                    ApplyEventAndPublish(
                        threadId,
                        new ChatStatusEvent(
                            ChatContentFormatting.TruncateForChatEntry(message.Text),
                            ChatTone.Dim),
                        _state.BuildLiveMetadata(threadId, message.Ts));
                }
                return;
            }

            var localEcho = _state.ConsumeLocalEcho(
                message,
                removeQueuedMessage: false,
                ProjectionContext(),
                projection);
            if (localEcho.Consumed)
            {
                if (localEcho.Snapshot is not null)
                    Publish(localEcho.Snapshot);
                return;
            }
            if (!string.IsNullOrEmpty(message.Text) ||
                (projection?.Attachments.Count ?? 0) > 0)
            {
                var userText = ChatContentFormatting.TruncateForChatEntry(
                    projection is { HasMediaEnvelope: true }
                        ? projection.ReconciliationText
                        : ChatMetadataStore.EscapeUntrustedAttachmentMarkerLines(message.Text));
                var reconciled = _state.ReconcileExistingLocalQueuedUser(
                    message,
                    userText,
                    ProjectionContext(),
                    projection?.Attachments,
                    projection?.AttachmentCorrelationSignature ?? "",
                    projection?.HasMediaEnvelope ?? false);
                if (reconciled.Snapshot is not null)
                    Publish(reconciled.Snapshot);
            }
            return;
        }

        if (role is "toolresult" or "tool_result")
        {
            if (string.IsNullOrEmpty(message.Text))
                return;
            var (metadata, runId) = _state.BuildMetadataWithRun(message);
            var capped = ChatContentFormatting.TruncateForChatEntry(message.Text);
            var mapped = ChatEventMapper.MapFlattenedToolOutput(capped, runId);
            _telemetry.ObserveInboundOutput(threadId, runId, ChatResponseOutputKind.Tool);
            ApplyEventAndPublish(threadId, mapped.Start, metadata);
            ApplyEventAndPublish(threadId, mapped.Output, metadata);
            return;
        }

        var assistantContent = role == "assistant"
            ? ChatAssistantContentProjector.Project(message.ContentParts)
            : null;
        if (role != "assistant" ||
            ChatMessageInfo.IsSilentAssistantDirective(role, message.Text) ||
            (string.IsNullOrEmpty(message.Text) && assistantContent is null))
        {
            return;
        }

        var assistantText = ChatContentFormatting.RepairContentBlockSeams(
            ChatContentFormatting.TruncateForChatEntry(message.Text));
        var preparation = _state.PrepareAssistant(
            message,
            assistantText,
            ProjectionContext(),
            assistantContent);
        if (preparation.PromotionSnapshot is not null)
            Publish(preparation.PromotionSnapshot);
        if (preparation.Disposition != AssistantQueueFrameDisposition.Render)
            return;
        if (!message.IsFinal && _state.IsLateNonFinalAssistantFrame(threadId))
        {
            Logger.Warn($"[ChatProvider] Dropping late non-final assistant frame after completed turn for threadId='{threadId}' len={traceText.Length}");
            return;
        }

        _telemetry.ObserveInboundOutput(
            threadId,
            preparation.ActiveRunId,
            ChatResponseOutputKind.Assistant);
        ApplyEventAndPublish(
            threadId,
            new ChatMessageEvent(
                assistantText,
                ReconcilePrevious: true,
                IsStreaming: !message.IsFinal),
            preparation.Metadata);

        var hasUsage = message.InputTokens is not null ||
                       message.OutputTokens is not null ||
                       message.ResponseTokens is not null ||
                       message.ContextPercent is not null;
        if (hasUsage &&
            _state.SnapshotAssistantUsageContribution(
                threadId,
                preparation.Metadata,
                ProjectionContext()) is { } usageSnapshot)
        {
            Publish(usageSnapshot);
        }

        if (!message.IsFinal)
            return;
        var completedRunId = _state.CompleteAssistantFinal(threadId);
        var completion = completedRunId is null
            ? null
            : _telemetry.PrepareFinishByRunId(
                completedRunId,
                ChatTelemetryOutcome.Success,
                ChatTurnTelemetryReason.AssistantFinal);
        _telemetry.CompletePreparedTurn(completion);
        if (_state.SnapshotLatestAssistantUsage(threadId, ProjectionContext()) is { } latestUsage)
            Publish(latestUsage);
        ApplyEventAndPublish(threadId, new ChatTurnEndEvent());
        RaiseNotification(new ChatProviderNotification(
            ChatProviderNotificationKind.TurnComplete,
            threadId,
            LocalizationHelper.GetString("Chat_Notification_AssistantReplied")));
        ScheduleQueuedSendDrain(threadId);
    }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
    {
        if (evt is null || _state.IsDisposed)
            return;
        if (string.IsNullOrEmpty(evt.SessionKey))
        {
            Logger.Warn($"[ChatProvider] Dropping agent event with empty sessionKey (stream={evt.Stream})");
            RaiseKeylessEventDiagnosticOnce();
            return;
        }

        var threadId = evt.SessionKey;
        var terminal = ChatEventMapper.IsTerminalRunEvent(evt);
        var transition = _state.ProcessAgentEvent(
            evt,
            threadId,
            ProjectionContext());
        if (!transition.Process)
        {
            if (transition.DroppedTerminalReason is { } droppedReason)
                RecordDroppedTerminalEvent(droppedReason);
            if (transition.ReloadHistory)
                _ = LoadHistoryAsync(threadId, force: true);
            return;
        }

        HandleOpenedLifecycle(
            threadId,
            transition.OpenedLifecycle,
            transition.RuntimeGeneration);
        foreach (var snapshot in transition.Snapshots)
            Publish(snapshot);
        if (ChatEventMapper.IsLifecycleStart(evt))
        {
            _telemetry.ObserveLifecycleStart(
                threadId,
                evt.RunId,
                transition.AllowRemoteTurn,
                transition.RuntimeGeneration);
            if (!_state.IsRuntimeGenerationCurrent(
                    threadId,
                    transition.RuntimeGeneration))
            {
                _telemetry.FinishByRunId(
                    evt.RunId,
                    ChatTelemetryOutcome.Canceled,
                    ChatTurnTelemetryReason.Superseded);
            }
        }
        if (transition.FetchRemoteUser)
            _ = FetchRemoteUserMessageAsync(threadId);

        ChatTelemetryTracker.PreparedTurnCompletion? completion = null;
        if (transition.CompletionPhase is { } phase)
        {
            completion = _telemetry.PrepareFinishByRunId(
                transition.CompletedRunId,
                phase == "error" ? ChatTelemetryOutcome.Failure : ChatTelemetryOutcome.Success,
                phase == "error"
                    ? ChatTurnTelemetryReason.LifecycleError
                    : ChatTurnTelemetryReason.LifecycleEnd);
            if (completion is null && !transition.WasAborted)
            {
                RecordDroppedTerminalEvent(
                    string.IsNullOrWhiteSpace(transition.CompletedRunId)
                        ? ChatTerminalEventDropReason.MissingRunId
                        : ChatTerminalEventDropReason.MismatchedRunId);
            }
        }
        _telemetry.CompletePreparedTurn(completion);

        ScheduleDeferredAbort(
            threadId,
            transition.DeferredAbortRunId,
            transition.DeferredAbortCount,
            transition.RuntimeGeneration);

        if (transition.Suppressed)
        {
            if (terminal)
                ScheduleQueuedSendDrain(threadId);
            return;
        }

        var mapped = transition.MappedEvent;
        if (mapped is null)
        {
            if (terminal)
                ScheduleQueuedSendDrain(threadId);
            return;
        }

        if (ChatEventMapper.ClassifyInboundOutput(evt, mapped) is { } outputKind)
            _telemetry.ObserveInboundOutput(threadId, evt.RunId, outputKind);
        if (transition.ToolMetadata is { } toolMetadata)
            _metadataStore.CacheTool(toolMetadata);
        if (terminal)
            ScheduleQueuedSendDrain(threadId);
    }

    private void ScheduleDeferredAbort(
        string threadId,
        string? runId,
        int pendingCount,
        ChatRuntimeGeneration runtimeGeneration)
    {
        if (runId is null && pendingCount <= 0)
            return;

        _ = _deferredAbortScheduler(async () =>
        {
            if (!_state.IsRuntimeGenerationCurrent(
                    threadId,
                    runtimeGeneration))
            {
                return;
            }
            if (runId is not null)
            {
                try
                {
                    await _bridge.SendChatAbortAsync(runId, threadId);
                }
                catch (Exception ex)
                {
                    var rollbackSnapshot =
                        _state.RollbackAbortAndEndTurnIfCurrent(
                            threadId,
                            runId,
                            runtimeGeneration,
                            ProjectionContext());
                    if (rollbackSnapshot is not null)
                    {
                        Logger.Warn(
                            $"[ABORT] Deferred chat.abort failed, cleared suppression: {ex.Message}");
                        RaiseNotification(new ChatProviderNotification(
                            ChatProviderNotificationKind.Error,
                            threadId,
                            LocalizationHelper.GetString(
                                "Chat_Notification_AbortFailed"),
                            ex.Message));
                        Publish(rollbackSnapshot);
                        ScheduleQueuedSendDrain(threadId);
                    }
                    return;
                }
            }
            if (!_state.IsRuntimeGenerationCurrent(
                    threadId,
                    runtimeGeneration))
            {
                return;
            }
            await PersistAbortedMessageIdsAsync(
                threadId,
                runtimeGeneration.ResetGeneration);
        });
    }

    private void HandleOpenedLifecycle(
        string threadId,
        ChatOpenedLifecycleTransition? opened,
        ChatRuntimeGeneration runtimeGeneration)
    {
        if (opened is null)
            return;

        _telemetry.ObserveLifecycleStart(
            threadId,
            opened.Event.RunId,
            opened.AllowRemoteTurn,
            runtimeGeneration);
        if (!_state.IsRuntimeGenerationCurrent(
                threadId,
                runtimeGeneration))
        {
            _telemetry.FinishByRunId(
                opened.Event.RunId,
                ChatTelemetryOutcome.Canceled,
                ChatTurnTelemetryReason.Superseded);
            return;
        }
        ScheduleDeferredAbort(
            threadId,
            opened.DeferredAbortRunId,
            opened.DeferredAbortCount,
            runtimeGeneration);
    }

    private void RaiseKeylessEventDiagnosticOnce()
    {
        if (!_state.TryRaiseKeylessDiagnostic())
            return;

        var threadId = _state.ResolveDefaultThreadId(ProjectionContext());
        var title = LocalizationHelper.GetString("Chat_Notification_KeylessEventDropped");
        var message = LocalizationHelper.GetString("Chat_Notification_KeylessEventDroppedMessage");

        RaiseNotification(new ChatProviderNotification(
            ChatProviderNotificationKind.Error,
            threadId ?? string.Empty,
            title,
            message));

        if (!string.IsNullOrWhiteSpace(threadId))
            ApplyEventAndPublish(threadId, new ChatStatusEvent(message, ChatTone.Warning));
    }

    private void RecordDroppedTerminalEvent(ChatTerminalEventDropReason reason)
    {
        _telemetry.RecordDroppedTerminalEvent(reason);
        Logger.Warn(
            $"[ChatTelemetry] Dropped terminal chat event because safe run correlation was unavailable " +
            $"(reason='{ChatTelemetryTracker.ToTelemetryValue(reason)}').");
    }

    private void TryDispatchNextQueuedSend(string threadId)
    {
        var start = _state.TryStartNextQueuedSend(
            threadId,
            requireConnected: true,
            ProjectionContext());
        var dispatch = start.Dispatch;
        var queueCompletion = dispatch is not null &&
                              dispatch.Request.LifecycleCommand is null
            ? _telemetry.PrepareDispatchLocalTurn(
                dispatch.Request.Id,
                dispatch.Request.SendRunId)
            : null;

        if (start.Snapshot is not null)
            Publish(start.Snapshot);
        if (dispatch is not null)
            _ = DispatchQueuedSendAsync(
                dispatch,
                queueCompletion,
                rethrow: false);
        else if (start.DelayedRetry is { } delay)
            ScheduleQueuedSendDrain(threadId, delay);
    }

    private void ScheduleQueuedSendDrain(string threadId)
        => ScheduleQueuedSendDrain(threadId, ChatSendQueuePolicy.DrainDelay);

    private void ScheduleQueuedSendDrain(string threadId, TimeSpan delay)
    {
        if (!_state.TryScheduleQueueDrain(threadId))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            finally
            {
                _state.CompleteQueueDrainSchedule(threadId);
            }

            try
            {
                TryDispatchNextQueuedSend(threadId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Queue] Scheduled queued send drain failed for threadId='{threadId}': {ex.Message}");
            }
        });
    }

    private async Task FetchRemoteUserMessageAsync(string threadId, bool openResetGateOnSuccess = false)
    {
        var telemetryReason = openResetGateOnSuccess
            ? ChatBackfillTelemetryReason.ResetReconciliation
            : ChatBackfillTelemetryReason.RemoteTurn;
        var historyOperation = _telemetry.StartHistoryBackfill(telemetryReason);
        var historyOutcome = ChatTelemetryOutcome.Success;
        Exception? historyException = null;
        var requestResetVersion = _state.GetResetGeneration(threadId);

        try
        {
            var history = await _bridge.RequestChatHistoryAsync(threadId);
            if (history?.Messages is null || history.Messages.Count == 0) return;

            // Find the last user message in history.
            ChatMessageInfo? lastUser = null;
            for (int i = history.Messages.Count - 1; i >= 0; i--)
            {
                var role = (history.Messages[i].Role ?? "").ToLowerInvariant();
                var hText = history.Messages[i].Text;
                if (role == "user"
                    && !NativeToolProjector.LooksLikeSystemControlNote(hText)
                    && !ChatContentFormatting.LooksLikeApprovalSlashCommand(hText))
                {
                    lastUser = history.Messages[i];
                    break;
                }
            }
            if (lastUser is null) return;
            var projection = GatewayMediaMessageProjection.Project(lastUser.Text);
            if (projection.ReconciliationText.Length == 0 &&
                projection.Attachments.Count == 0)
            {
                return;
            }
            var cachedAttachment = _metadataStore
                .CreateAttachmentMatcher(
                    history.SessionId,
                    threadId,
                    requestResetVersion)
                .TryMatch(
                    projection.ReconciliationText,
                    projection.AttachmentCorrelationSignature,
                    lastUser.Ts);
            var projectedAttachments = cachedAttachment is not null
                ? ChatMetadataStore.CreatePersistedLocalPresentations(
                    cachedAttachment.Attachments)
                : projection.Attachments;

            var transition = _state.ApplyRemoteUserBackfill(
                threadId,
                lastUser,
                projection,
                projectedAttachments,
                requestResetVersion,
                openResetGateOnSuccess,
                ProjectionContext());
            if (transition is null)
                return;
            HandleOpenedLifecycle(
                threadId,
                transition.OpenedLifecycle,
                transition.RuntimeGeneration);
            Publish(transition.Snapshot);
            Logger.Info($"[REMOTE] Injected remote user message for threadId='{threadId}' len={lastUser.Text.Length}");
        }
        catch (Exception ex)
        {
            historyOutcome = ex is OperationCanceledException
                ? ChatTelemetryOutcome.Canceled
                : ChatTelemetryOutcome.Failure;
            historyException = ex;
            Logger.Warn($"[REMOTE] Failed to fetch remote user message for threadId='{threadId}': {ex.Message}");
        }
        finally
        {
            if (openResetGateOnSuccess)
                _state.CompleteRemoteBackfill(threadId);
            _telemetry.FinishHistoryBackfill(historyOperation, historyOutcome, historyException);
        }
    }

#if OPENCLAW_TRAY_TESTS
    internal Task FetchRemoteUserMessageForTestsAsync(
        string threadId,
        bool openResetGateOnSuccess) =>
        FetchRemoteUserMessageAsync(threadId, openResetGateOnSuccess);
#endif

    private async Task PersistAbortedMessageIdsAsync(
        string threadId,
        long resetGeneration)
    {
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            var history = await _bridge
                .RequestChatHistoryAsync(threadId)
                .ConfigureAwait(false);
            if (!_state.IsCurrentResetGeneration(threadId, resetGeneration))
                return;
            var ids = _persistence.FindAbortedMessageIds(
                threadId,
                history.Messages,
                resetGeneration);
            if (_persistence.TryAddAbortedIds(
                    threadId,
                    resetGeneration,
                    ids))
            {
                _persistence.SaveAbortedIds();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"[ABORT-PERSIST] Failed to persist abort for thread {threadId}: {ex.Message}");
        }
    }

    internal static string TruncateForChatEntry(string? text) => ChatContentFormatting.TruncateForChatEntry(text);

    internal static bool LooksLikeSystemControlNote(string text) => NativeToolProjector.LooksLikeSystemControlNote(text);

    internal static string RepairContentBlockSeams(string? text) => ChatContentFormatting.RepairContentBlockSeams(text);

    internal static ChatEvent TruncateChatEvent(ChatEvent evt) => ChatContentFormatting.TruncateChatEvent(evt);

    private void ApplyEventAndPublish(string threadId, ChatEvent evt, ChatEntryMetadata? meta = null)
    {
        var snapshot = _state.ApplyEvent(
            threadId,
            evt,
            meta,
            ProjectionContext());
        Publish(snapshot);
    }

    private ChatProjectionContext ProjectionContext() =>
        new(_bridge.MainSessionKey, _bridge.HasHandshakeSnapshot);

    private void Publish(ChatDataSnapshot snapshot)
    {
#if OPENCLAW_TRAY_TESTS
        BeforePublishForTests?.Invoke(snapshot);
#endif

        if (_post is null)
        {
            DeliverSnapshot(snapshot);
        }
        else
        {
            bool shouldSchedule;
            lock (_publishGate)
            {
                if (_publishDisposed)
                    return;

                _pendingPublishSnapshot = snapshot;
                shouldSchedule = !_publishScheduled;
                if (shouldSchedule)
                    _publishScheduled = true;
            }

            if (shouldSchedule)
                PostPublishDrain();
        }
    }

    private void PostPublishDrain()
    {
        try
        {
            _post!(DrainPendingPublish);
        }
        catch
        {
            lock (_publishGate)
                _publishScheduled = false;
            throw;
        }
    }

    private void DrainPendingPublish()
    {
        ChatDataSnapshot? snapshot;
        ChatProviderNotification[] notifications;
        lock (_publishGate)
        {
            if (_publishDisposed)
            {
                _pendingPublishSnapshot = null;
                _pendingPublishNotifications.Clear();
                _publishScheduled = false;
                return;
            }

            snapshot = _pendingPublishSnapshot;
            _pendingPublishSnapshot = null;
            notifications = _pendingPublishNotifications.ToArray();
            _pendingPublishNotifications.Clear();
            if (snapshot is null)
            {
                _publishScheduled = false;
                return;
            }
        }

        // A producer can be delayed after building its snapshot, then reach
        // Publish after a newer state transition. Materialize at drain time so
        // coalescing follows authoritative state order, not caller arrival.
        snapshot = _state.Snapshot(ProjectionContext());

        try
        {
            DeliverSnapshot(snapshot);
            foreach (var notification in notifications)
                DeliverNotification(notification);
        }
        finally
        {
            bool shouldSchedule;
            lock (_publishGate)
            {
                if (_publishDisposed)
                {
                    _pendingPublishSnapshot = null;
                    _pendingPublishNotifications.Clear();
                    _publishScheduled = false;
                    shouldSchedule = false;
                }
                else
                {
                    shouldSchedule = _pendingPublishSnapshot is not null;
                    if (!shouldSchedule)
                        _publishScheduled = false;
                }
            }

            // Keep the current drain scheduled while callbacks run. Any number
            // of callback-time publishes then need exactly one follow-up drain.
            if (shouldSchedule)
                PostPublishDrain();
        }
    }

    private void DebounceSnapshot(ChatDataSnapshot snapshot)
    {
        // Save last-known UI state so the next launch can show meaningful
        // labels while reconnecting instead of "Main session"/"model".
        if (snapshot.Threads.Length > 0 || snapshot.AvailableModels.Length > 0)
            _persistence.DebounceSnapshot(snapshot);
    }

    private void DeliverSnapshot(ChatDataSnapshot snapshot)
    {
        Deliver(
            () =>
            {
                Changed?.Invoke(this, new ChatDataChangedEventArgs(snapshot));
                DebounceSnapshot(snapshot);
            });
    }

    private void DeliverNotification(ChatProviderNotification notification) =>
        Deliver(
            () => NotificationRequested?.Invoke(
                this,
                new ChatProviderNotificationEventArgs(notification)));

    private void Deliver(Action callback)
    {
        var threadId = Environment.CurrentManagedThreadId;
        lock (_publishGate)
        {
            if (_publishDisposed)
                return;
            _activeDeliveries++;
            _deliveryDepthByThread[threadId] =
                _deliveryDepthByThread.GetValueOrDefault(threadId) + 1;
        }

        try
        {
            callback();
        }
        finally
        {
            lock (_publishGate)
            {
                _activeDeliveries--;
                var depth = _deliveryDepthByThread[threadId] - 1;
                if (depth == 0)
                    _deliveryDepthByThread.Remove(threadId);
                else
                    _deliveryDepthByThread[threadId] = depth;
                Monitor.PulseAll(_publishGate);
            }
        }
    }

    private bool IsDeliveringOnCurrentThread()
    {
        lock (_publishGate)
            return _deliveryDepthByThread.ContainsKey(Environment.CurrentManagedThreadId);
    }

    private void WaitForInFlightDeliveries()
    {
        lock (_publishGate)
        {
            var threadId = Environment.CurrentManagedThreadId;
            var ownCallbackDepth = _deliveryDepthByThread.GetValueOrDefault(threadId);
            while (_activeDeliveries > ownCallbackDepth)
            {
                Monitor.Wait(_publishGate);
                ownCallbackDepth = _deliveryDepthByThread.GetValueOrDefault(threadId);
            }
        }
    }

    // ── Last-chat-state cache ──────────────────────────────────────────
    // Persists the last-known thread title, model, and available models so
    // the UI can show them while reconnecting instead of generic placeholders.

    internal sealed class LastChatState
    {
        public string? DefaultThreadId { get; set; }
        public string? ThreadTitle { get; set; }
        public string? Model { get; set; }
        public string? ModelProvider { get; set; }
        public string[]? AvailableModels { get; set; }
    }

    internal static LastChatState? LoadLastChatState(string? pathOverride = null) =>
        ChatStatePersistence.LoadLastChatState(pathOverride);

    private void RaiseNotification(ChatProviderNotification notification)
    {
        if (_post is null)
        {
            DeliverNotification(notification);
            return;
        }

        lock (_publishGate)
        {
            if (_publishDisposed)
                return;
            if (_pendingPublishSnapshot is not null)
            {
                _pendingPublishNotifications.Add(notification);
                return;
            }
        }
        _post(() => DeliverNotification(notification));
    }

}
