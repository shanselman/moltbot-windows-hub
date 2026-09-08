using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

/// <summary>
/// Single lock root for atomic conversation, queue, reset, connection, and
/// history commit state. All exposed operations are closed domain transitions.
/// </summary>
internal sealed class ChatConversationState
{
    private static readonly TimeSpan LocalEchoSuppressionWindow = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly ChatApprovalState _approval = new();
    private readonly ChatHistoryState _history = new();
    private readonly ChatPresentationState _presentation;
    private readonly ChatQueueState _queue = new();
    private readonly ChatLifecycleState _lifecycle = new();
    private readonly ChatResetState _reset = new();
    private readonly Dictionary<string, ChatTimelineState> _timelines = new();
    private readonly Dictionary<string, Dictionary<string, ChatEntryMetadata>> _entryMeta = new();
    private ConnectionStatus _status;
    private bool _disposed;

    internal ChatConversationState(
        ConnectionStatus status,
        OpenClawChatDataProvider.LastChatState? lastChatState,
        ModelsListInfo? seedModels)
    {
        _status = status;
        _presentation = new ChatPresentationState(lastChatState, seedModels);
    }

    internal bool IsResponseSuppressed
    {
        get
        {
            lock (_gate)
                return _lifecycle.IsResponseSuppressed;
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    internal ConnectionStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    internal long HistoryGeneration
    {
        get
        {
            lock (_gate)
                return _history.ConnectionGeneration;
        }
    }

    internal OpenClawChatDataProvider.LastChatState? CachedLastChatState
    {
        get
        {
            lock (_gate)
                return _presentation.CachedLastChatState;
        }
    }

    internal ChatDataSnapshot Load(
        SessionInfo[] sessions,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _presentation.ReplaceSessions(
                sessions,
                receivedFromGateway: false);
            EnsureTimelinesForSessionsLocked();
            _presentation.RememberLastSessionState(context);
            return BuildSnapshotLocked(context);
        }
    }

    internal IReadOnlyDictionary<string, ChatEntryMetadata> GetEntryMetadata(string threadId)
    {
        lock (_gate)
        {
            return _entryMeta.TryGetValue(threadId, out var metadata)
                ? new Dictionary<string, ChatEntryMetadata>(metadata)
                : new Dictionary<string, ChatEntryMetadata>();
        }
    }

    internal OpenClawChatDataProvider.LastChatState? RememberSelectedThread(string threadId)
    {
        lock (_gate)
        {
            return _presentation.RememberSelectedThread(threadId);
        }
    }

    internal ChatDataSnapshot Snapshot(ChatProjectionContext context)
    {
        lock (_gate)
            return BuildSnapshotLocked(context);
    }

    internal string? ResolveDefaultThreadId(ChatProjectionContext context)
    {
        lock (_gate)
        {
            return ChatSnapshotProjector.ResolveDefaultThreadId(
                CaptureProjectionInputLocked(context));
        }
    }

    internal (string CacheKey, long ResetGeneration) ResolveMetadataKey(string threadId)
    {
        lock (_gate)
        {
            var key = _history.ResolveSessionId(threadId) ?? threadId;
            return (key, GetResetVersionLocked(threadId));
        }
    }

    internal long GetResetGeneration(string threadId)
    {
        lock (_gate)
            return GetResetVersionLocked(threadId);
    }

    internal ChatHistoryCommitToken CaptureHistoryToken(string threadId)
    {
        lock (_gate)
        {
            return _history.CreateCommitToken(
                threadId,
                GetResetVersionLocked(threadId));
        }
    }

    internal ChatHistoryReplacementTransition? BeginHistoryReplacement(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return null;

            var token = _history.BeginReplacement(
                threadId,
                GetResetVersionLocked(threadId));
            _timelines[threadId] = ChatTimelineState.Initial();
            _entryMeta.Remove(threadId);
            return new(BuildSnapshotLocked(context), token);
        }
    }

    internal bool TryBeginHistory(
        string threadId,
        bool force,
        ChatHistoryCommitToken? expectedToken,
        out ChatHistoryCommitToken token,
        out string? model,
        out Task? generationActivation)
    {
        lock (_gate)
        {
            var canBegin = _history.TryBegin(
                threadId,
                force,
                expectedToken,
                GetResetVersionLocked(threadId),
                _status,
                _disposed,
                out token,
                out generationActivation);
            model = _presentation.ModelForThread(threadId);
            return canBegin;
        }
    }

    internal bool IsHistoryRequestCurrent(ChatHistoryCommitToken token)
    {
        lock (_gate)
            return _history.IsCurrent(
                token,
                GetResetVersionLocked(token.ThreadId),
                _disposed);
    }

    internal bool CanRetryHistory(
        ChatHistoryCommitToken token,
        bool authoritative)
    {
        lock (_gate)
            return _history.CanRetry(
                token,
                GetResetVersionLocked(token.ThreadId),
                _status,
                authoritative,
                _disposed);
    }

    internal bool CommitHistory(
        ChatHistoryCommitToken token,
        ChatHistoryRebuildPlan plan,
        DateTimeOffset requestStartedAt,
        bool authoritative)
    {
        lock (_gate)
        {
            if (!_history.IsCurrent(
                    token,
                    GetResetVersionLocked(token.ThreadId),
                    _disposed))
            {
                return false;
            }

            var prior = GetOrCreateTimelineLocked(token.ThreadId);
            var priorMetadata = _entryMeta.TryGetValue(
                token.ThreadId,
                out var metadata)
                ? metadata
                : new Dictionary<string, ChatEntryMetadata>();
            var merged = ChatHistoryState.MergeWithLiveEntries(
                plan,
                prior,
                priorMetadata,
                requestStartedAt,
                authoritative);
            _timelines[token.ThreadId] = merged.Timeline;
            _entryMeta[token.ThreadId] = merged.Metadata;
            _history.MarkCommitted(token, plan.SessionId);
            return true;
        }
    }

    internal ChatDataSnapshot? SnapshotIfHistoryTokenCurrent(
        ChatHistoryCommitToken token,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return _history.IsCurrent(
                       token,
                       GetResetVersionLocked(token.ThreadId),
                       _disposed)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    internal bool IsCurrentResetGeneration(string threadId, long generation)
    {
        lock (_gate)
            return GetResetVersionLocked(threadId) == generation;
    }

    internal ChatStatusTransition ApplyStatus(
        ConnectionStatus status,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new(
                    BuildSnapshotLocked(context),
                    false,
                    false,
                    [],
                    _history.ConnectionGeneration);
            }

            var reconnected = status == ConnectionStatus.Connected &&
                              _status != ConnectionStatus.Connected;
            var disconnected = status != ConnectionStatus.Connected &&
                               _status == ConnectionStatus.Connected;
            _status = status;
            if (status != ConnectionStatus.Connected)
                _presentation.LeaveConnected();
            if (disconnected)
                _approval.Reset();

            string[] interruptedThreads = [];
            if (reconnected)
            {
                _history.AdvanceConnectionGeneration(clearLoaded: true);
                _queue.ClearForReconnect();
                _reset.ClearSubmittedEchoesForReconnect();
                _lifecycle.ClearForReconnect();
                _presentation.ResetKeylessDiagnostic();
                foreach (var threadId in _timelines.Keys.ToArray())
                {
                    _timelines[threadId] = ChatTimelineReducer.Apply(
                        _timelines[threadId],
                        new ChatToolReplayResetEvent());
                }
            }
            if (disconnected)
            {
                _history.AdvanceConnectionGeneration(clearLoaded: false);
                _reset.ClearSubmittedEchoesForReconnect();
                interruptedThreads = _timelines
                    .Where(pair => pair.Value.TurnActive)
                    .Select(pair => pair.Key)
                    .ToArray();
                _lifecycle.ClearActiveRuns(interruptedThreads);
            }
            return new(
                BuildSnapshotLocked(context),
                reconnected,
                disconnected,
                interruptedThreads,
                _history.ConnectionGeneration);
        }
    }

    internal void ClearToolReplayState()
    {
        lock (_gate)
        {
            foreach (var threadId in _timelines.Keys.ToArray())
            {
                _timelines[threadId] = ChatTimelineReducer.Apply(
                    _timelines[threadId],
                    new ChatToolReplayResetEvent());
            }
        }
    }

    internal ChatSessionsTransition ApplySessions(
        SessionInfo[] sessions,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var previousUsage = _presentation.SnapshotUsage();
            _presentation.ReplaceSessions(sessions);
            var currentSessions = _presentation.SessionSnapshot();
            _history.SeedSessionIds(currentSessions);
            EnsureTimelinesForSessionsLocked();
            _presentation.RememberLastSessionState(context);
            foreach (var session in currentSessions)
            {
                if (string.IsNullOrEmpty(session.Key))
                    continue;
                var usage = new ChatUsageSnapshot(
                    session.InputTokens,
                    session.OutputTokens,
                    session.TotalTokens,
                    session.ContextTokens);
                if (!previousUsage.TryGetValue(session.Key, out var previous) ||
                    previous != usage)
                {
                    SnapshotLatestAssistantUsageLocked(
                        session,
                        _presentation.ResolveTimelineKey(session, _timelines));
                }
            }
            return new(
                BuildSnapshotLocked(context),
                _status == ConnectionStatus.Connected
                    ? _queue.ThreadsWithMessages()
                    : []);
        }
    }

    internal ChatDataSnapshot ApplyModels(
        ModelsListInfo models,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            _presentation.ApplyModels(models);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatSessionOptionPatchLease BeginSessionOptionPatch(string threadId)
    {
        lock (_gate)
            return _presentation.BeginSessionOptionPatch(threadId);
    }

    internal void CompleteSessionOptionPatch(ChatSessionOptionPatchLease lease, Exception? error)
    {
        lock (_gate)
            _presentation.CompleteSessionOptionPatch(lease, error);
    }

    internal Task? GetPendingSessionOptionPatch(string threadId)
    {
        lock (_gate)
            return _presentation.GetPendingSessionOptionPatch(threadId);
    }

    internal bool TryBeginCommandCatalogFetch(out int epoch)
    {
        lock (_gate)
            return _presentation.TryBeginCommandCatalogFetch(_status, out epoch);
    }

    internal bool CompleteCommandCatalogFetch(int epoch, CommandCatalog catalog)
    {
        lock (_gate)
            return _presentation.CompleteCommandCatalogFetch(
                epoch,
                _status,
                catalog);
    }

    internal bool FailCommandCatalogFetch(int epoch)
    {
        lock (_gate)
            return _presentation.FailCommandCatalogFetch(epoch, _status);
    }

    internal ChatDataSnapshot? SnapshotCommandCatalogIfFresh(
        int epoch,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return !_disposed &&
                   _presentation.IsCommandCatalogEpochCurrent(epoch)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    internal ChatDisposeTransition DisposeState()
    {
        lock (_gate)
        {
            if (_disposed)
                return new(
                    _history.ConnectionGeneration,
                    IsFirstDispose: false);
            _disposed = true;
            _history.AdvanceConnectionGeneration(clearLoaded: false);
            _queue.ClearForDispose();
            _lifecycle.ClearForDispose();
            _reset.ClearSubmittedEchoesForReconnect();
            return new(
                _history.ConnectionGeneration,
                IsFirstDispose: true);
        }
    }

    internal bool TryRaiseKeylessDiagnostic()
    {
        lock (_gate)
            return _presentation.TryRaiseKeylessDiagnostic();
    }

    internal string? PendingPermissionId(string threadId)
    {
        lock (_gate)
            return GetOrCreateTimelineLocked(threadId).PendingPermission?.RequestId;
    }

    internal void ActivateHistoryGeneration(long generation)
    {
        lock (_gate)
            _history.ActivateConnectionGeneration(generation, _disposed);
    }

    private bool SnapshotLatestAssistantUsageLocked(
        SessionInfo session,
        string threadId)
    {
        if (string.IsNullOrEmpty(session.Key))
            return false;
        var usedTokens = session.TotalTokens;
        if (usedTokens <= 0)
            usedTokens = session.InputTokens + session.OutputTokens;
        if (usedTokens <= 0 ||
            string.IsNullOrEmpty(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return false;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            if (timeline.Entries[i].Kind != ChatTimelineItemKind.Assistant)
                continue;
            var metadata = GetOrCreateThreadMetaLocked(threadId);
            metadata.TryGetValue(timeline.Entries[i].Id, out var existing);
            var usageSnapshot = Math.Max(
                usedTokens,
                existing?.ResponseTokens ?? 0);
            var usageTokens = ToIntIfPositive(usageSnapshot);
            var contextTokens = session.ContextTokens > 0
                ? session.ContextTokens
                : existing?.ContextTokens;
            if (existing is not null &&
                existing.ResponseTokens == usageTokens &&
                existing.ContextTokens == contextTokens)
            {
                return false;
            }
            metadata[timeline.Entries[i].Id] =
                (existing ?? BuildLiveMetaLocked(threadId)) with
            {
                InputTokens = ToIntIfPositive(session.InputTokens),
                OutputTokens = ToIntIfPositive(session.OutputTokens),
                ResponseTokens = usageTokens,
                ContextTokens = contextTokens,
                ContextPercent = existing?.ContextPercent,
                UsageContributionTokens = existing?.UsageContributionTokens,
            };
            return true;
        }
        return false;
    }

    private static int? ToIntIfPositive(long value) =>
        value > 0 && value <= int.MaxValue ? (int)value : null;

    internal static bool ShouldPreserveLiveEntryDuringAuthoritativeReload(
        ChatEntryMetadata? metadata,
        int maxHistorySequence,
        DateTimeOffset requestStartedAt) =>
        ChatHistoryState.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            metadata,
            maxHistorySequence,
            requestStartedAt);

    internal ChatQueuedAdmission AdmitMessage(
        string threadId,
        string text,
        string displayText,
        string nonce,
        IReadOnlyList<ChatAttachment>? attachments,
        DateTimeOffset createdAt,
        ChatProjectionContext context,
        string? timelineText = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachmentPresentations = null,
        string attachmentCorrelationSignature = "")
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _queue.NextMessageId();
            if (CanClearAssistantFallbackPromotionLocked(threadId))
                _queue.ClearAssistantFallbackPromotion(threadId);

            _lifecycle.ClearThreadSuppression(threadId);
            _lifecycle.TakePendingAbortCount(threadId);
            var request = new ChatQueuedSendRequest(
                messageId,
                Guid.NewGuid().ToString(),
                threadId,
                text,
                displayText,
                nonce,
                attachments?.ToArray(),
                TimelineText: timelineText ?? displayText,
                AttachmentPresentations: attachmentPresentations,
                AttachmentCorrelationSignature: attachmentCorrelationSignature);

            var sendDirectly = CanSendDirectlyLocked(threadId);
            ChatQueuedSendDispatch? dispatch;
            if (sendDirectly)
            {
                dispatch = StartDirectSendLocked(request);
            }
            else
            {
                _queue.AddMessage(threadId, new ChatQueuedMessage(
                    messageId,
                    displayText,
                    createdAt,
                    nonce));
                _queue.AddRequest(request);
                dispatch = TryStartNextQueuedSendLocked(
                    threadId,
                    requireConnected: false,
                    out _);
            }

            return new ChatQueuedAdmission(
                messageId,
                Queued: !sendDirectly,
                dispatch,
                BuildSnapshotLocked(context),
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatDataSnapshot EnqueueCompact(
        string threadId,
        DateTimeOffset createdAt,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messageId = _queue.NextMessageId();
            var request = new ChatQueuedSendRequest(
                messageId,
                Guid.NewGuid().ToString(),
                threadId,
                "/compact",
                "/compact",
                Guid.NewGuid().ToString(),
                Attachments: null,
                LifecycleCommand: ChatLifecycleCommandKind.Compact);
            _queue.AddMessage(threadId, new ChatQueuedMessage(
                messageId,
                request.DisplayText,
                createdAt,
                request.LocalNonce));
            _queue.AddRequest(request);
            return BuildSnapshotLocked(context);
        }
    }

    internal (bool Canceled, ChatDataSnapshot? Snapshot) CancelQueuedMessage(
        string threadId,
        string messageId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var canceled = _queue.CancelMessage(threadId, messageId);
            if (canceled)
            {
                _queue.ClearLocallyInitiatedIfIdle(
                    threadId,
                    _lifecycle.HasActiveRun(threadId),
                    _timelines.TryGetValue(threadId, out var timeline) &&
                    timeline.TurnActive);
            }
            return (
                canceled,
                canceled ? BuildSnapshotLocked(context) : null);
        }
    }

    internal ChatQueueStart TryStartNextQueuedSend(
        string threadId,
        bool requireConnected,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(null, null, null);
            var dispatch = TryStartNextQueuedSendLocked(
                threadId,
                requireConnected,
                out var delayedRetry);
            return new ChatQueueStart(
                dispatch,
                delayedRetry,
                dispatch is null ? null : BuildSnapshotLocked(context));
        }
    }

    internal bool TryScheduleQueueDrain(string threadId)
    {
        lock (_gate)
        {
            return !_disposed && _queue.TryScheduleDrain(threadId);
        }
    }

    internal void CompleteQueueDrainSchedule(string threadId)
    {
        lock (_gate)
            _queue.CompleteDrainSchedule(threadId);
    }

    internal ChatSendPreparation PrepareSendAttempt(
        ChatQueuedSendDispatch dispatch,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch) ||
                !dispatch.StartedDirectly &&
                _queue.FindRequest(
                    dispatch.Request.ThreadId,
                    dispatch.Request.Id) is null)
            {
                return new(
                    false,
                    CleanupStaleDispatchTurnLocked(dispatch, context));
            }
            _queue.TrackRun(
                dispatch.Request.ThreadId,
                dispatch.Request.SendRunId,
                dispatch.Request.Id);
            return new(true, null);
        }
    }

    internal ChatSendCommit CommitSendResult(
        ChatQueuedSendDispatch dispatch,
        ChatSendResult sendResult,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        var threadId = request.ThreadId;
        var acceptedRunId = string.IsNullOrWhiteSpace(sendResult.RunId)
            ? null
            : sendResult.RunId;
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch))
            {
                _reset.RemovePendingLocalSubmission(
                    threadId,
                    request.Id,
                    dispatch.ResetVersion);
                var staleRunId = acceptedRunId ?? request.SendRunId;
                _reset.AddIgnoredRun(threadId, staleRunId);
                return new(
                    IsCurrent: false,
                    AcceptedSnapshot:
                        CleanupStaleDispatchTurnLocked(dispatch, context),
                    RequeuedSnapshot: null,
                    StaleRunIdToAbort: staleRunId,
                    BindAcceptedRun: false,
                    RequeueRequired: false,
                    RetryDeferredSend: false,
                    DeferredRetryDelay: ChatSendQueuePolicy.DrainDelay,
                    OpenedLifecycle: null,
                    CurrentRuntimeGenerationLocked(threadId));
            }

            ChatDataSnapshot? acceptedSnapshot = null;
            ChatDataSnapshot? requeuedSnapshot = null;
            ChatOpenedLifecycleTransition? openedLifecycle = null;
            var bindAcceptedRun = false;
            var requeueRequired = false;
            var retryDeferredSend = false;
            var deferredRetryDelay = ChatSendQueuePolicy.DrainDelay;
            if (ChatSendQueuePolicy.IsDeferredAdmissionStatus(sendResult.Status))
            {
                _reset.RemovePendingLocalSubmission(
                    threadId,
                    request.Id,
                    dispatch.ResetVersion);
                var runAlreadyStarted = !string.IsNullOrEmpty(acceptedRunId)
                    && _lifecycle.HasRunStartedAfter(
                        threadId,
                        acceptedRunId,
                        dispatch.StartedRunStartSequence);
                if (runAlreadyStarted)
                {
                    bindAcceptedRun = true;
                    _queue.TrackRun(threadId, acceptedRunId!, request.Id);
                    AddResetAcceptedRunIdLocked(threadId, acceptedRunId!);
                    if (PromoteQueuedMessageLocked(threadId, request.Id))
                        acceptedSnapshot = BuildSnapshotLocked(context);
                    else
                        _queue.RemoveRunMappingByMessageId(threadId, request.Id);
                }
                else if (_queue.RequeueDeferredAdmission(
                             threadId,
                             request.Id,
                             _lifecycle.HasActiveRun(threadId)) is
                         { Requeued: true } retry)
                {
                    deferredRetryDelay = retry.Delay;
                    if (retry.ShouldEndTurn)
                    {
                        _timelines[threadId] = ChatTimelineReducer.Apply(
                            GetOrCreateTimelineLocked(threadId),
                            new ChatTurnEndEvent());
                    }
                    requeueRequired = true;
                    if (!string.IsNullOrEmpty(acceptedRunId))
                    {
                        _queue.TrackRun(threadId, acceptedRunId, request.Id);
                        openedLifecycle =
                            AddResetAcceptedRunIdLocked(
                                threadId,
                                acceptedRunId);
                    }
                    requeuedSnapshot = BuildSnapshotLocked(context);
                    retryDeferredSend = true;
                }
                else if (dispatch.StartedDirectly)
                {
                    throw new InvalidOperationException(
                        $"Gateway returned chat.send status {sendResult.Status} before admitting the direct send.");
                }
            }
            else if (!string.IsNullOrEmpty(acceptedRunId))
            {
                bindAcceptedRun = true;
                _queue.TrackRun(threadId, acceptedRunId, request.Id);
                openedLifecycle =
                    AddResetAcceptedRunIdLocked(
                        threadId,
                        acceptedRunId);
                var runAlreadyStarted =
                    _lifecycle.HasRunStartedAfter(
                        threadId,
                        acceptedRunId,
                        dispatch.StartedRunStartSequence);
                if (PromoteQueuedMessageLocked(threadId, request.Id))
                    acceptedSnapshot = BuildSnapshotLocked(context);
                else if (runAlreadyStarted)
                    _queue.RemoveRunMappingByMessageId(threadId, request.Id);
            }
            else if (_reset.IsAwaitingUserMessage(threadId))
            {
                _queue.RemoveRunMappingByRunId(threadId, request.SendRunId);
                openedLifecycle =
                    ApplyBufferedLifecycleOpenLocked(
                        threadId,
                        _reset.RecordLocalSendWithoutRun(
                            threadId,
                            dispatch.ResetVersion,
                            dispatch.StartedLifecycleSequence,
                            request.Id),
                        allowRemoteTurn: false);
                if (PromoteQueuedMessageLocked(threadId, request.Id))
                    acceptedSnapshot = BuildSnapshotLocked(context);
            }
            else if (PromoteQueuedMessageLocked(threadId, request.Id))
            {
                _queue.RemoveRunMappingByRunId(threadId, request.SendRunId);
                acceptedSnapshot = BuildSnapshotLocked(context);
            }

            return new(
                IsCurrent: true,
                acceptedSnapshot,
                requeuedSnapshot,
                StaleRunIdToAbort: null,
                bindAcceptedRun,
                requeueRequired,
                retryDeferredSend,
                deferredRetryDelay,
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatSendFailure FailSend(
        ChatQueuedSendDispatch dispatch,
        string queueError,
        string timelineError,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        lock (_gate)
        {
            _reset.RemovePendingLocalSubmission(
                request.ThreadId,
                request.Id,
                dispatch.ResetVersion);
            if (!IsDispatchGenerationCurrentLocked(dispatch))
            {
                return new(
                    false,
                    CleanupStaleDispatchTurnLocked(dispatch, context));
            }

            _queue.RemovePendingLocalEcho(request.ThreadId, request.Id);
            _queue.MarkFailed(request.ThreadId, request.Id, queueError);
            _queue.RemoveRequest(request.ThreadId, request.Id);
            _queue.RemoveRunMappingByMessageId(request.ThreadId, request.Id);
            if (!_queue.HasSendingMessages(request.ThreadId))
                _queue.ClearLocallyInitiated(request.ThreadId);
            ApplyEventLocked(
                request.ThreadId,
                ChatContentFormatting.TruncateChatEvent(
                    new ChatErrorEvent(timelineError)),
                metadata: null);
            ApplyEventLocked(request.ThreadId, new ChatTurnEndEvent(), metadata: null);
            return new(true, BuildSnapshotLocked(context));
        }
    }

    internal bool IsQueuedDispatchCurrent(ChatQueuedSendDispatch dispatch)
    {
        lock (_gate)
        {
            return IsDispatchGenerationCurrentLocked(dispatch)
                && _queue.FindRequest(
                    dispatch.Request.ThreadId,
                    dispatch.Request.Id) is not null;
        }
    }

    internal (bool Succeeded, ChatDataSnapshot? Snapshot) CompleteQueuedLifecycle(
        ChatQueuedSendDispatch dispatch,
        bool succeeded,
        string? error,
        ChatProjectionContext context)
    {
        var request = dispatch.Request;
        lock (_gate)
        {
            if (!IsDispatchGenerationCurrentLocked(dispatch) ||
                _queue.FindRequest(request.ThreadId, request.Id) is null)
            {
                return (false, null);
            }

            if (succeeded)
            {
                var removed = _queue.RemoveMessage(request.ThreadId, request.Id);
                return (true, removed ? BuildSnapshotLocked(context) : null);
            }

            ApplyEventLocked(
                request.ThreadId,
                new ChatErrorEvent(error ?? "The lifecycle command failed."),
                metadata: null);
            _queue.MarkFailed(
                request.ThreadId,
                request.Id,
                error ?? "The lifecycle command failed.");
            _queue.RemoveRequest(request.ThreadId, request.Id);
            return (true, BuildSnapshotLocked(context));
        }
    }

    private bool IsDispatchGenerationCurrentLocked(
        ChatQueuedSendDispatch dispatch) =>
        !_disposed &&
        _history.ConnectionGeneration == dispatch.ConnectionGeneration &&
        GetResetVersionLocked(dispatch.Request.ThreadId) == dispatch.ResetVersion;

    private ChatDataSnapshot? CleanupStaleDispatchTurnLocked(
        ChatQueuedSendDispatch dispatch,
        ChatProjectionContext context)
    {
        var threadId = dispatch.Request.ThreadId;
        if (_disposed ||
            _lifecycle.HasActiveRun(threadId) ||
            _queue.IsLocallyInitiated(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !timeline.TurnActive)
        {
            return null;
        }
        _timelines[threadId] = ChatTimelineReducer.Apply(
            timeline,
            new ChatTurnEndEvent());
        return BuildSnapshotLocked(context);
    }

    private bool CanSendDirectlyLocked(string threadId) =>
        _queue.CanSendDirectly(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) && timeline.TurnActive);

    private bool CanClearAssistantFallbackPromotionLocked(string threadId) =>
        _queue.CanClearAssistantFallback(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive);

    private ChatQueuedSendDispatch StartDirectSendLocked(
        ChatQueuedSendRequest request)
    {
        var threadId = request.ThreadId;
        var resetVersion = GetResetVersionLocked(threadId);
        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(
            current,
            request.EffectiveTimelineText,
            request.LocalNonce);
        GetOrCreateThreadMetaLocked(threadId)[entryId] = BuildLiveMetaLocked(
            threadId,
            isLocalQueuedSend: true,
            localQueuedMessageId: request.Id,
            attachments: request.AttachmentPresentations);
        var dispatch = _queue.StartDirect(
            request,
            _history.ResolveSessionId(threadId),
            _history.ConnectionGeneration,
            resetVersion,
            _reset.LifecycleStartSequence,
            _lifecycle.LifecycleStartSequence);
        RegisterResetSubmissionLocked(dispatch);
        return dispatch;
    }

    private ChatQueuedSendDispatch? TryStartNextQueuedSendLocked(
        string threadId,
        bool requireConnected,
        out TimeSpan? delayedRetry)
    {
        var turnActive = _timelines.TryGetValue(threadId, out var timeline) &&
                         timeline.TurnActive;
        var dispatch = _queue.TryStartNext(
            threadId,
            requireConnected,
            _status,
            _lifecycle.HasActiveRun(threadId),
            turnActive,
            _history.ResolveSessionId(threadId),
            _history.ConnectionGeneration,
            GetResetVersionLocked(threadId),
            _reset.LifecycleStartSequence,
            _lifecycle.LifecycleStartSequence,
            out delayedRetry);
        if (dispatch?.Request.LifecycleCommand is null && dispatch is not null)
        {
            RegisterResetSubmissionLocked(dispatch);
            _timelines[threadId] = ChatTimelineReducer.BeginLocalUserTurn(
                GetOrCreateTimelineLocked(threadId));
        }
        return dispatch;
    }

    private void RegisterResetSubmissionLocked(
        ChatQueuedSendDispatch dispatch)
    {
        _reset.RegisterPendingLocalSubmission(
            dispatch.Request.ThreadId,
            dispatch.Request.Id,
            dispatch.Request.Text,
            dispatch.ResetVersion,
            dispatch.StartedLifecycleSequence,
            DateTimeOffset.UtcNow,
            requiresEcho:
                !string.IsNullOrWhiteSpace(
                    dispatch.Request.Text));
    }

    private bool RemoveQueuedMessageLocked(string threadId, string messageId)
    {
        var removed = _queue.RemoveMessage(threadId, messageId);
        if (removed)
            ClearLocallyInitiatedIfIdleLocked(threadId);
        return removed;
    }

    private bool CancelQueuedMessageLocked(string threadId, string messageId)
    {
        var canceled = _queue.CancelMessage(threadId, messageId);
        if (canceled)
            ClearLocallyInitiatedIfIdleLocked(threadId);
        return canceled;
    }

    private bool PromoteQueuedMessageLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata? confirmedMeta = null)
    {
        var request = _queue.FindRequest(threadId, messageId);
        if (!_queue.TryTakeForPromotion(threadId, messageId, out var queued))
            return false;

        var current = GetOrCreateTimelineLocked(threadId);
        var entryId = $"e{current.NextId}";
        _timelines[threadId] = ChatTimelineReducer.AddLocalUser(
            current,
            request?.EffectiveTimelineText ?? queued.Text,
            queued.LocalNonce);
        var meta = confirmedMeta is not null && HasGatewayIdentity(confirmedMeta)
            ? confirmedMeta with
            {
                IsLocalQueuedSend = false,
                LocalQueuedMessageId = messageId,
                Attachments = request?.AttachmentPresentations ?? confirmedMeta.Attachments,
            }
            : BuildLiveMetaLocked(
                threadId,
                isLocalQueuedSend: true,
                localQueuedMessageId: messageId,
                attachments: request?.AttachmentPresentations);
        GetOrCreateThreadMetaLocked(threadId)[entryId] = meta;
        return true;
    }

    private void ClearLocallyInitiatedIfIdleLocked(string threadId)
    {
        _queue.ClearLocallyInitiatedIfIdle(
            threadId,
            _lifecycle.HasActiveRun(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive);
    }

    private static bool HasGatewayIdentity(ChatEntryMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.GatewayMessageId) ||
        metadata.OpenClawSeq is not null;

    internal ChatDataSnapshot ApplyEvent(
        string threadId,
        ChatEvent evt,
        ChatEntryMetadata? metadata,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            ApplyEventLocked(
                threadId,
                ChatContentFormatting.TruncateChatEvent(evt),
                metadata);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatDataSnapshot ClearPendingPermission(
        string threadId,
        string? expectedRequestId,
        ChatPermissionDecision decision,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var timeline = GetOrCreateTimelineLocked(threadId);
            if (expectedRequestId is not null &&
                !string.Equals(
                    timeline.PendingPermission?.RequestId,
                    expectedRequestId,
                    StringComparison.Ordinal))
            {
                return BuildSnapshotLocked(context);
            }
            _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
                timeline,
                expectedRequestId,
                decision);
            return BuildSnapshotLocked(context);
        }
    }

    internal ChatEntryMetadata BuildLiveMetadata(
        string threadId,
        long? tsMs = null,
        string? gatewayMessageId = null,
        int? openClawSeq = null,
        bool isLocalQueuedSend = false,
        string? localQueuedMessageId = null,
        string? openClawKind = null,
        long? compactionTokensBefore = null,
        long? compactionTokensAfter = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        ChatAssistantContentPresentation? assistantContent = null)
    {
        lock (_gate)
        {
            return BuildLiveMetaLocked(
                threadId,
                tsMs,
                gatewayMessageId,
                openClawSeq,
                isLocalQueuedSend,
                localQueuedMessageId,
                openClawKind,
                compactionTokensBefore,
                compactionTokensAfter,
                attachments,
                assistantContent);
        }
    }

    internal bool IsLateNonFinalAssistantFrame(string threadId)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(threadId, out var timeline) ||
                timeline.TurnActive)
            {
                return false;
            }
            for (var i = timeline.Entries.Count - 1; i >= 0; i--)
            {
                var entry = timeline.Entries[i];
                if (entry.Kind == ChatTimelineItemKind.User)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return !entry.IsStreaming;
            }
            return false;
        }
    }

    internal ChatAbortStart BeginAbort(string threadId)
    {
        lock (_gate)
        {
            var hadActiveTurn = _timelines.TryGetValue(threadId, out var timeline) &&
                                timeline.TurnActive;
            return _lifecycle.BeginAbort(threadId, hadActiveTurn);
        }
    }

    internal void RollbackAbort(string threadId, string runId)
    {
        lock (_gate)
        {
            _lifecycle.RollbackAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
        }
    }

    internal ChatDataSnapshot? RollbackAbortAndEndTurnIfCurrent(
        string threadId,
        string runId,
        ChatRuntimeGeneration expectedGeneration,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (_disposed ||
                CurrentRuntimeGenerationLocked(threadId) != expectedGeneration ||
                !_lifecycle.TryGetActiveRun(threadId, out var activeRunId) ||
                !string.Equals(activeRunId, runId, StringComparison.Ordinal))
            {
                return null;
            }

            _lifecycle.RollbackAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
            ApplyEventLocked(
                threadId,
                new ChatTurnEndEvent(),
                metadata: null);
            return BuildSnapshotLocked(context);
        }
    }

    internal void CompleteAbort(string threadId, string? runId)
    {
        lock (_gate)
        {
            _lifecycle.CompleteAbort(threadId, runId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
        }
    }

    internal bool ShouldSuppressChatMessage(string threadId)
    {
        lock (_gate)
            return _lifecycle.IsThreadSuppressed(threadId);
    }

    internal ChatResetTransition ResetThread(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var oldSessionId = _history.ClearSessionForReset(threadId);

            var submittedRunIds = new HashSet<string>(StringComparer.Ordinal);
            if (_lifecycle.ActiveRunForReset(threadId) is { Length: > 0 } activeRunId)
            {
                submittedRunIds.Add(activeRunId);
            }

            foreach (var runId in _queue.RunIdsForThread(threadId))
                submittedRunIds.Add(runId);
            foreach (var localEcho in _queue.SnapshotLocalEchoes(threadId))
            {
                _reset.AddSubmittedLocalEcho(
                    threadId,
                    localEcho.Text,
                    localEcho.SentAt);
            }

            var generation = _reset.BeginReset(
                threadId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _timelines[threadId] = ChatTimelineState.Initial() with
            {
                HistoryLoaded = true,
            };
            _entryMeta.Remove(threadId);
            _lifecycle.ClearThreadForReset(threadId);
            _queue.ClearThreadForReset(threadId);
            foreach (var runId in submittedRunIds)
                _reset.AddIgnoredRun(threadId, runId);

            return new(
                BuildSnapshotLocked(context),
                oldSessionId,
                generation,
                threadId,
                submittedRunIds.ToArray());
        }
    }

    internal ChatIncomingMessageGate GateIncomingChatMessage(
        ChatMessageInfo message,
        ChatProjectionContext context,
        GatewayMediaMessageProjectionResult? projection = null)
    {
        var threadId = message.SessionKey!;
        var role = message.Role?.ToLowerInvariant() ?? string.Empty;
        var text = projection?.ReconciliationText ?? message.Text ?? string.Empty;
        var attachmentCorrelationSignature = projection?.AttachmentCorrelationSignature ?? "";
        var hasMediaEnvelope = projection?.HasMediaEnvelope ?? false;
        lock (_gate)
        {
            _lifecycle.TryGetActiveRun(
                threadId,
                out var activeRunId);
            var resetGate = _reset.EvaluateChatMessage(
                    threadId,
                    role,
                    text,
                    message.Ts,
                    _queue.HasPendingLocalEchoText(
                        threadId,
                        text,
                        attachmentCorrelationSignature,
                        hasMediaEnvelope),
                    activeRunId);
            var openedLifecycle =
                ApplyBufferedLifecycleOpenLocked(
                    threadId,
                    resetGate.OpenedLifecycleStart,
                    allowRemoteTurn:
                        resetGate.ConsumeEchoText is null);
            if (resetGate.Drop)
            {
                ChatDataSnapshot? snapshot = null;
                if (resetGate.ConsumeEchoText is not null &&
                    _queue.TryConsumeLocalEcho(
                        threadId,
                        resetGate.ConsumeEchoText,
                        attachmentCorrelationSignature,
                        hasMediaEnvelope,
                        out var queuedMessageId))
                {
                    var confirmed = BuildLiveMetaLocked(
                        threadId,
                        message.Ts,
                        message.OpenClawId,
                        message.OpenClawSeq);
                    if (ReconcileQueuedMessageEchoLocked(
                            threadId,
                            queuedMessageId,
                            confirmed))
                    {
                        snapshot = BuildSnapshotLocked(context);
                    }
                }
                return new(
                    true,
                    false,
                    resetGate.RequestRemoteBackfill,
                    snapshot,
                    openedLifecycle,
                    CurrentRuntimeGenerationLocked(threadId));
            }
            return new(
                Drop: false,
                Suppressed: _lifecycle.IsThreadSuppressed(threadId),
                RequestRemoteBackfill: false,
                Snapshot: null,
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    internal ChatLocalEchoTransition ConsumeLocalEcho(
        ChatMessageInfo message,
        bool removeQueuedMessage,
        ChatProjectionContext context,
        GatewayMediaMessageProjectionResult? projection = null)
    {
        var threadId = message.SessionKey!;
        var text = projection?.ReconciliationText ??
            (message.Text ?? string.Empty).Trim();
        var attachmentCorrelationSignature = projection?.AttachmentCorrelationSignature ?? "";
        var hasMediaEnvelope = projection?.HasMediaEnvelope ?? false;
        lock (_gate)
        {
            if (!_queue.TryConsumeLocalEcho(
                    threadId,
                    text,
                    attachmentCorrelationSignature,
                    hasMediaEnvelope,
                    out var queuedMessageId))
            {
                return new(false, null);
            }
            if (removeQueuedMessage)
                RemoveQueuedMessageLocked(threadId, queuedMessageId);
            var confirmed = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq);
            return new(
                true,
                !removeQueuedMessage &&
                ReconcileQueuedMessageEchoLocked(
                    threadId,
                    queuedMessageId,
                    confirmed)
                    ? BuildSnapshotLocked(context)
                    : null);
        }
    }

    internal ChatLocalEchoTransition ReconcileExistingLocalQueuedUser(
        ChatMessageInfo message,
        string userText,
        ChatProjectionContext context,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        string attachmentCorrelationSignature = "",
        bool hasMediaEnvelope = false)
    {
        var threadId = message.SessionKey!;
        lock (_gate)
        {
            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                attachments: attachments);
            if (TryReconcileExistingLocalQueuedUserEchoLocked(
                    threadId,
                    userText,
                    attachmentCorrelationSignature,
                    hasMediaEnvelope,
                    metadata))
            {
                return new(true, BuildSnapshotLocked(context));
            }

            var remoteSnapshot = ApplyProjectedRemoteUserMessageLocked(
                threadId,
                userText,
                attachmentCorrelationSignature,
                metadata,
                context);
            return new(false, remoteSnapshot);
        }
    }

    // Applies an incoming user message from another client (not a local
    // echo). Gateway retransmits of the same message (e.g. once with a
    // partial media resolve, once final) are merged into the existing
    // timeline row instead of appended as a duplicate, matched first by
    // gateway identity and — for identity-less rows — by same trailing-entry
    // text + attachment signature.
    private ChatDataSnapshot? ApplyProjectedRemoteUserMessageLocked(
        string threadId,
        string projectedText,
        string attachmentCorrelationSignature,
        ChatEntryMetadata incomingMeta,
        ChatProjectionContext context)
    {
        var timeline = GetOrCreateTimelineLocked(threadId);
        var threadMeta = GetOrCreateThreadMetaLocked(threadId);
        ChatTimelineItem? matched = null;
        ChatEntryMetadata? existingMeta = null;

        if (HasGatewayIdentity(incomingMeta))
        {
            for (var i = timeline.Entries.Count - 1; i >= 0; i--)
            {
                var candidate = timeline.Entries[i];
                if (candidate.Kind != ChatTimelineItemKind.User ||
                    !threadMeta.TryGetValue(candidate.Id, out var candidateMeta) ||
                    candidateMeta.IsLocalQueuedSend ||
                    !HasMatchingGatewayIdentity(candidateMeta, incomingMeta))
                {
                    continue;
                }

                matched = candidate;
                existingMeta = candidateMeta;
                break;
            }
        }

        // Identity-less history/live twins are only safe to correlate
        // against the current trailing user row. Crossing an
        // assistant/status boundary would collapse a legitimate later turn
        // that repeats the same prose.
        if (matched is null &&
            timeline.Entries.Count > 0 &&
            timeline.Entries[^1] is { Kind: ChatTimelineItemKind.User } latestUser &&
            string.Equals(latestUser.Text, projectedText, StringComparison.Ordinal) &&
            threadMeta.TryGetValue(latestUser.Id, out var latestMeta) &&
            !latestMeta.IsLocalQueuedSend &&
            !HasConflictingGatewayIdentity(latestMeta, incomingMeta) &&
            string.Equals(
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    latestMeta.Attachments),
                attachmentCorrelationSignature,
                StringComparison.Ordinal))
        {
            matched = latestUser;
            existingMeta = latestMeta;
        }

        if (matched is null || existingMeta is null)
        {
            ApplyEventLocked(
                threadId,
                new ChatUserMessageEvent(projectedText),
                incomingMeta);
            return BuildSnapshotLocked(context);
        }

        var mergedMeta = MergeProjectedUserMetadata(existingMeta, incomingMeta);
        if (mergedMeta == existingMeta)
            return null;

        threadMeta[matched.Id] = mergedMeta;
        return HasRendererVisibleUserMetadataChange(existingMeta, mergedMeta)
            ? BuildSnapshotLocked(context)
            : null;
    }

    private static bool HasMatchingGatewayIdentity(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming) =>
        (!string.IsNullOrEmpty(incoming.GatewayMessageId) &&
         string.Equals(
             existing.GatewayMessageId,
             incoming.GatewayMessageId,
             StringComparison.Ordinal)) ||
        (incoming.OpenClawSeq is not null && existing.OpenClawSeq == incoming.OpenClawSeq);

    private static bool HasConflictingGatewayIdentity(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming) =>
        (!string.IsNullOrEmpty(existing.GatewayMessageId) &&
         !string.IsNullOrEmpty(incoming.GatewayMessageId) &&
         !string.Equals(
             existing.GatewayMessageId,
             incoming.GatewayMessageId,
             StringComparison.Ordinal)) ||
        (existing.OpenClawSeq is not null &&
         incoming.OpenClawSeq is not null &&
         existing.OpenClawSeq != incoming.OpenClawSeq);

    private static ChatEntryMetadata MergeProjectedUserMetadata(
        ChatEntryMetadata existing,
        ChatEntryMetadata incoming)
    {
        var mergedAttachments = existing.Attachments is { Count: > 0 }
            ? existing.Attachments
            : incoming.Attachments;
        return existing with
        {
            Timestamp = existing.Timestamp ?? incoming.Timestamp,
            Model = existing.Model ?? incoming.Model,
            GatewayMessageId = string.IsNullOrEmpty(existing.GatewayMessageId)
                ? incoming.GatewayMessageId
                : existing.GatewayMessageId,
            OpenClawSeq = existing.OpenClawSeq ?? incoming.OpenClawSeq,
            Attachments = mergedAttachments,
        };
    }

    private static bool HasRendererVisibleUserMetadataChange(
        ChatEntryMetadata existing,
        ChatEntryMetadata merged) =>
        existing.Timestamp != merged.Timestamp ||
        !AttachmentPresentationsEqual(existing.Attachments, merged.Attachments);

    private static bool AttachmentPresentationsEqual(
        IReadOnlyList<ChatAttachmentPresentation>? left,
        IReadOnlyList<ChatAttachmentPresentation>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
            return false;
        if (leftCount == 0)
            return true;

        for (var i = 0; i < leftCount; i++)
        {
            if (left![i] != right![i])
                return false;
        }
        return true;
    }

    internal (ChatEntryMetadata Metadata, string? ActiveRunId) BuildMetadataWithRun(
        ChatMessageInfo message)
    {
        lock (_gate)
        {
            var metadata = BuildLiveMetaLocked(
                message.SessionKey!,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq);
            _lifecycle.TryGetActiveRun(message.SessionKey!, out var runId);
            return (metadata, runId);
        }
    }

    internal ChatAssistantPreparation PrepareAssistant(
        ChatMessageInfo message,
        string assistantText,
        ChatProjectionContext context,
        ChatAssistantContentPresentation? assistantContent = null)
    {
        var threadId = message.SessionKey!;
        lock (_gate)
        {
            // A frame carrying only structured/legacy media directives (no
            // plain text) has nothing for the identified-duplicate/self-echo
            // classifier to compare against, and it never carries a gateway
            // identity either (media-only frames are synthesized locally
            // from ContentParts, not gateway-sequenced) — so it can't be a
            // resend of an already-rendered turn. Render it directly rather
            // than routing it through text-based classification.
            var disposition = assistantText.Length == 0 &&
                assistantContent is not null &&
                string.IsNullOrEmpty(message.OpenClawId) &&
                message.OpenClawSeq is null
                ? AssistantQueueFrameDisposition.Render
                : ClassifyAssistantQueueFrameLocked(
                    threadId,
                    assistantText,
                    message.OpenClawId,
                    message.OpenClawSeq);
            ChatDataSnapshot? promotionSnapshot = null;
            if (disposition == AssistantQueueFrameDisposition.Render &&
                _queue.IsLocallyInitiated(threadId) &&
                _queue.TryGetSingleSendingMessage(threadId, out var queued) &&
                !_lifecycle.HasActiveRun(threadId) &&
                !_queue.IsAssistantFallbackPromoted(threadId) &&
                PromoteQueuedMessageLocked(threadId, queued.Id))
            {
                promotionSnapshot = BuildSnapshotLocked(context);
            }

            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                assistantContent: assistantContent);
            var hasUsage = message.InputTokens is not null ||
                           message.OutputTokens is not null ||
                           message.ResponseTokens is not null ||
                           message.ContextPercent is not null;
            if (hasUsage)
            {
                var contextTokens = _presentation.ContextTokensForThread(threadId);
                metadata = metadata with
                {
                    InputTokens = message.InputTokens ?? metadata.InputTokens,
                    OutputTokens = message.OutputTokens ?? metadata.OutputTokens,
                    ResponseTokens = message.ResponseTokens ?? metadata.ResponseTokens,
                    ContextPercent = message.ContextPercent ?? metadata.ContextPercent,
                    ContextTokens = contextTokens is > 0
                        ? contextTokens
                        : metadata.ContextTokens,
                };
            }
            _lifecycle.TryGetActiveRun(threadId, out var activeRunId);
            return new(disposition, promotionSnapshot, metadata, activeRunId);
        }
    }

    internal string? CompleteAssistantFinal(string threadId)
    {
        lock (_gate)
        {
            var completedRunId = _lifecycle.CompleteAssistantFinal(threadId);
            _reset.CompleteRun(threadId, completedRunId);
            if (!_queue.HasSendingMessages(threadId))
                _queue.ClearLocallyInitiated(threadId);
            return completedRunId;
        }
    }

    internal ChatAgentEventTransition ProcessAgentEvent(
        AgentEventInfo evt,
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var gate = GateAgentEventLocked(evt, threadId);
            if (!gate.Process)
            {
                return new(
                    Process: false,
                    gate.ReloadHistory,
                    gate.DroppedTerminalReason,
                    DeferredAbortRunId: null,
                    DeferredAbortCount: 0,
                    CompletedRunId: null,
                    CompletionPhase: null,
                    FetchRemoteUser: false,
                    AllowRemoteTurn: false,
                    WasAborted: false,
                    Suppressed: false,
                    MappedEvent: null,
                    ToolMetadata: null,
                    Snapshots: [],
                    gate.OpenedLifecycle,
                    CurrentRuntimeGenerationLocked(threadId));
            }

            var run = UpdateRunTrackingLocked(evt, threadId, context);
            var snapshots = new List<ChatDataSnapshot>();
            if (run.Snapshot is not null)
                snapshots.Add(run.Snapshot);

            var suppressed = _lifecycle.ShouldSuppress(threadId, evt.RunId);
            ChatEvent? mapped = null;
            ChatToolMetadataWrite? toolMetadata = null;
            if (!suppressed)
            {
                var mapping = ChatEventMapper.Map(evt);
                mapped = mapping.Event;
                if (mapping.Approval is { } approval &&
                    !_approval.MarkSeen(approval.RequestId, approval.AlternateId))
                {
                    mapped = null;
                }

                if (mapped is not null)
                {
                    ApplyEventLocked(
                        threadId,
                        ChatContentFormatting.TruncateChatEvent(mapped),
                        BuildLiveMetaLocked(
                            threadId,
                            evt.Ts > 0 ? (long)evt.Ts : 0));
                    toolMetadata = BuildToolMetadataWriteLocked(
                        threadId,
                        mapped,
                        evt.Ts > 0 ? (long)evt.Ts : 0);
                    snapshots.Add(BuildSnapshotLocked(context));
                }
                else if (TryResolveTerminalApprovalLocked(evt, threadId))
                {
                    snapshots.Add(BuildSnapshotLocked(context));
                }
            }

            return new(
                Process: true,
                ReloadHistory: false,
                run.DroppedTerminalReason,
                run.DeferredAbortRunId,
                run.DeferredAbortCount,
                run.CompletedRunId,
                run.CompletionPhase,
                run.FetchRemoteUser,
                run.AllowRemoteTurn,
                run.WasAborted,
                suppressed,
                mapped,
                toolMetadata,
                snapshots.ToArray(),
                gate.OpenedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    private ChatToolMetadataWrite? BuildToolMetadataWriteLocked(
        string threadId,
        ChatEvent mapped,
        long timestampMs)
    {
        string toolName;
        string label;
        string? toolCallId;
        System.Text.Json.Nodes.JsonObject? toolArgs;
        ChatToolIdentityStrength identityStrength;
        string? runId;
        switch (mapped)
        {
            case ChatToolStartEvent start
                when !string.IsNullOrWhiteSpace(start.ToolName):
                toolName = start.ToolName;
                label = start.Text;
                toolCallId = start.ToolCallId;
                toolArgs = start.ToolArgs;
                identityStrength = start.IdentityStrength;
                runId = start.RunId;
                break;
            case ChatToolPresentationEvent presentation:
                toolName = presentation.ToolName;
                label = NativeToolProjector.FirstToolDisplayValue(
                    presentation.ToolArgs);
                toolCallId = presentation.ParentToolCallId;
                toolArgs = presentation.ToolArgs;
                identityStrength = presentation.IdentityStrength;
                runId = presentation.RunId;
                break;
            default:
                return null;
        }

        var legacyTurn = ResolveToolCacheLegacyTurnLocked(
            threadId,
            mapped,
            runId,
            toolCallId);
        return new ChatToolMetadataWrite(
            threadId,
            _history.ResolveSessionId(threadId) ?? threadId,
            GetResetVersionLocked(threadId),
            timestampMs,
            toolName,
            label,
            toolCallId,
            toolArgs,
            identityStrength,
            runId,
            legacyTurn);
    }

    private long ResolveToolCacheLegacyTurnLocked(
        string threadId,
        ChatEvent mapped,
        string? runId,
        string? toolCallId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            return 0;
        if (!_timelines.TryGetValue(threadId, out var timeline))
            return ChatTimelineState.Initial().ToolLegacyTurn;
        if (string.IsNullOrWhiteSpace(toolCallId))
            return timeline.ToolLegacyTurn;

        if (mapped is ChatToolPresentationEvent)
        {
            var pendingKey = timeline.PendingToolPresentations?.Keys
                .Where(key => key.RunId is null &&
                    string.Equals(
                        key.ToolCallId,
                        toolCallId,
                        StringComparison.Ordinal))
                .OrderByDescending(key => key.LegacyTurn)
                .FirstOrDefault();
            if (pendingKey is { ToolCallId.Length: > 0 })
                return pendingKey.Value.LegacyTurn;
        }

        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.ToolCall ||
                entry.ToolRunId is not null)
            {
                continue;
            }
            if (string.Equals(
                    entry.ToolCallId,
                    toolCallId,
                    StringComparison.Ordinal) ||
                entry.ToolCorrelationIds?.Contains(toolCallId) == true)
            {
                return entry.ToolLegacyTurn;
            }
        }
        return timeline.ToolLegacyTurn;
    }

    private ChatAgentEventGate GateAgentEventLocked(
        AgentEventInfo evt,
        string threadId)
    {
        var resetGate = _reset.EvaluateAgentEvent(evt, threadId);
        ChatOpenedLifecycleTransition? openedLifecycle = null;
        if (resetGate.OpenedLifecycleStart is { } openedStart &&
            ChatEventMapper.IsLifecycleStart(openedStart))
        {
            ApplyOpenedResetLifecycleStartLocked(
                threadId,
                openedStart);
        }
        else
        {
            openedLifecycle = ApplyBufferedLifecycleOpenLocked(
                threadId,
                resetGate.OpenedLifecycleStart,
                allowRemoteTurn: false);
        }
        if (resetGate.Drop)
        {
            return new(
                false,
                resetGate.ReloadHistory,
                null,
                openedLifecycle);
        }
        if (ShouldDropTerminalAgentEventLocked(
                evt,
                threadId,
                out var droppedReason))
        {
            return new(
                false,
                false,
                droppedReason,
                openedLifecycle);
        }
        return new(
            true,
            false,
            null,
            openedLifecycle);
    }

    private ChatRunTransition UpdateRunTrackingLocked(
        AgentEventInfo evt,
        string threadId,
        ChatProjectionContext context)
    {
        string? deferredAbortRunId = null;
        var deferredAbortCount = 0;
        ChatTerminalEventDropReason? droppedReason = null;
        string? completionPhase = null;
        var fetchRemoteUser = false;
        var allowRemoteTurn = false;
        var wasAborted = false;
        ChatDataSnapshot? snapshot = null;

        if (string.Equals(
                    evt.Stream,
                    "lifecycle",
                    StringComparison.OrdinalIgnoreCase) &&
                evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                evt.Data.TryGetProperty("phase", out var phaseProperty))
            {
                var phase = phaseProperty.GetString()?.ToLowerInvariant();
                if (phase == "start")
                {
                    allowRemoteTurn =
                        !_queue.IsLocallyInitiated(threadId) &&
                        !_lifecycle.IsThreadSuppressed(threadId) &&
                        !_lifecycle.HasPendingAbort(threadId);
                    if (!string.IsNullOrEmpty(evt.RunId))
                    {
                        _lifecycle.StartRun(threadId, evt.RunId);
                        fetchRemoteUser = !_queue.IsLocallyInitiated(threadId);
                        var pendingCount =
                            _lifecycle.TakePendingAbortCount(threadId);
                        if (pendingCount > 0)
                        {
                            _lifecycle.MarkDeferredAbort(
                                threadId,
                                evt.RunId);
                            deferredAbortRunId = evt.RunId;
                            deferredAbortCount = pendingCount;
                        }
                    }
                    if (TryPromoteQueuedMessageOnLocalTurnStartLocked(evt, threadId))
                        snapshot = BuildSnapshotLocked(context);
                }
                else if (phase is "end" or "error")
                {
                    completionPhase = phase;
                    wasAborted = _lifecycle.IsRunAborted(evt.RunId);
                    _reset.CompleteRun(threadId, evt.RunId);
                    _lifecycle.RemoveAbortedRun(evt.RunId);
                    _lifecycle.RemoveActiveRun(threadId);
                    _lifecycle.ClearThreadSuppression(threadId);
                    _queue.RemoveRunMappingByRunId(threadId, evt.RunId);
                    if (!_queue.HasPendingMessages(threadId))
                        _queue.ClearLocallyInitiated(threadId);
                    var pendingCount =
                        _lifecycle.TakePendingAbortCount(threadId);
                    if (pendingCount > 0)
                    {
                        deferredAbortRunId = evt.RunId;
                        deferredAbortCount = pendingCount;
                    }
                }
            }
            else if (string.Equals(
                         evt.Stream,
                         "job",
                         StringComparison.OrdinalIgnoreCase) &&
                     evt.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     evt.Data.TryGetProperty("state", out var stateProperty))
            {
                var phase = stateProperty.GetString()?.ToLowerInvariant();
                if (phase is "done" or "error")
                {
                    completionPhase = phase == "done" ? "end" : "error";
                    wasAborted = _lifecycle.IsRunAborted(evt.RunId);
                    _reset.CompleteRun(threadId, evt.RunId);
                    if (!string.IsNullOrWhiteSpace(evt.RunId))
                    {
                        _lifecycle.RemoveAbortedRun(evt.RunId);
                        _queue.RemoveRunMappingByRunId(threadId, evt.RunId);
                    }
                    _lifecycle.RemoveActiveRun(threadId);
                }
            }

        return new(
            deferredAbortRunId,
            deferredAbortCount,
            droppedReason,
            evt.RunId,
            completionPhase,
            fetchRemoteUser,
            allowRemoteTurn,
            wasAborted,
            snapshot);
    }

    private bool TryResolveTerminalApprovalLocked(
        AgentEventInfo evt,
        string threadId)
    {
        var terminal = ChatEventMapper.MapTerminalApproval(evt);
        if (terminal is null)
            return false;
        var timeline = GetOrCreateTimelineLocked(threadId);
        var pendingId = timeline.PendingPermission?.RequestId;
        if (pendingId is null ||
            !_approval.Matches(
                pendingId,
                terminal.ApprovalSlug,
                terminal.ApprovalId))
        {
            return false;
        }
        _timelines[threadId] = ChatTimelineReducer.ResolvePermission(
            timeline,
            pendingId,
            ChatEventMapper.MapTerminalApprovalDecision(
                terminal.Phase,
                terminal.Decision));
        return true;
    }

    internal void CompleteRemoteBackfill(string threadId)
    {
        lock (_gate)
            _reset.CompleteRemoteBackfill(threadId);
    }

    internal ChatRemoteUserBackfillTransition? ApplyRemoteUserBackfill(
        string threadId,
        ChatMessageInfo message,
        GatewayMediaMessageProjectionResult projection,
        IReadOnlyList<ChatAttachmentPresentation> attachments,
        long expectedResetGeneration,
        bool openResetGate,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            if (GetResetVersionLocked(threadId) != expectedResetGeneration ||
                _reset.IsPreResetTimestamp(threadId, message.Ts))
            {
                return null;
            }
            var openedLifecycle = openResetGate
                ? ApplyBufferedLifecycleOpenLocked(
                    threadId,
                    _reset.RecordRemoteUser(threadId),
                    allowRemoteTurn: true)
                : null;
            var metadata = BuildLiveMetaLocked(
                threadId,
                message.Ts,
                message.OpenClawId,
                message.OpenClawSeq,
                attachments: attachments);
            var snapshot = ApplyProjectedRemoteUserMessageLocked(
                threadId,
                ChatContentFormatting.TruncateForChatEntry(
                    projection.HasMediaEnvelope
                        ? projection.ReconciliationText
                        : ChatMetadataStore.EscapeUntrustedAttachmentMarkerLines(
                            message.Text)),
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    attachments),
                metadata,
                context);
            if (snapshot is null && openedLifecycle is null)
                return null;
            return new(
                snapshot ?? BuildSnapshotLocked(context),
                openedLifecycle,
                CurrentRuntimeGenerationLocked(threadId));
        }
    }

    private bool TryPromoteQueuedMessageOnLocalTurnStartLocked(
        AgentEventInfo evt,
        string threadId)
    {
        if (!_queue.IsLocallyInitiated(threadId))
            return false;
        if (!string.IsNullOrEmpty(evt.RunId) &&
            _queue.TryResolveMessageForRun(
                threadId,
                evt.RunId,
                out var messageId))
        {
            return PromoteQueuedMessageLocked(threadId, messageId);
        }
        return string.IsNullOrEmpty(evt.RunId) &&
               _queue.TryGetSingleSendingMessage(threadId, out var queued) &&
               PromoteQueuedMessageLocked(threadId, queued.Id);
    }

    private bool ShouldDropTerminalAgentEventLocked(
        AgentEventInfo evt,
        string threadId,
        out ChatTerminalEventDropReason? droppedReason)
    {
        droppedReason = null;
        if (!TryGetTerminalAgentRunId(evt, out var runId))
            return false;
        if (string.IsNullOrWhiteSpace(runId))
        {
            droppedReason = ChatTerminalEventDropReason.MissingRunId;
            return true;
        }
        return _lifecycle.ShouldDropTerminal(
            threadId,
            runId,
            _queue.RunIdsForThread(threadId),
            _timelines.TryGetValue(threadId, out var timeline) &&
            timeline.TurnActive,
            out droppedReason);
    }

    private static bool TryGetTerminalAgentRunId(
        AgentEventInfo evt,
        out string runId)
    {
        runId = evt.RunId ?? string.Empty;
        if (evt.Data.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;
        if (string.Equals(evt.Stream, "lifecycle", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("phase", out var phase))
        {
            var value = phase.GetString();
            return string.Equals(value, "end", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(evt.Stream, "job", StringComparison.OrdinalIgnoreCase) &&
            evt.Data.TryGetProperty("state", out var state))
        {
            var value = state.GetString();
            return string.Equals(value, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "error", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal ChatDataSnapshot? SnapshotLatestAssistantUsage(
        string threadId,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            var session = _presentation.ResolveSessionForThread(
                threadId,
                context.MainSessionKey);
            return session is not null &&
                   SnapshotLatestAssistantUsageLocked(session, threadId)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    internal ChatDataSnapshot? SnapshotAssistantUsageContribution(
        string threadId,
        ChatEntryMetadata metadata,
        ChatProjectionContext context)
    {
        lock (_gate)
        {
            return SnapshotAssistantUsageContributionLocked(threadId, metadata)
                ? BuildSnapshotLocked(context)
                : null;
        }
    }

    private bool SnapshotAssistantUsageContributionLocked(
        string threadId,
        ChatEntryMetadata metadata)
    {
        var currentUsage = UsageValue(metadata);
        if (currentUsage is null || currentUsage <= 0 ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return false;
        }
        var contextTokens = metadata.ContextTokens;
        if (contextTokens is null || contextTokens <= 0)
            contextTokens = _presentation.ContextTokensForThread(threadId);
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            var threadMetadata = GetOrCreateThreadMetaLocked(threadId);
            threadMetadata.TryGetValue(entry.Id, out var existing);
            var previousUsage = LatestAssistantUsageBeforeLocked(
                timeline,
                threadMetadata,
                i);
            var cumulative = Math.Max(
                (previousUsage ?? 0) + currentUsage.Value,
                existing?.ResponseTokens ?? 0);
            if (existing?.ResponseTokens == cumulative &&
                existing.UsageContributionTokens == currentUsage &&
                existing.ContextTokens == contextTokens)
            {
                return false;
            }
            threadMetadata[entry.Id] = (existing ?? BuildLiveMetaLocked(threadId)) with
            {
                InputTokens = metadata.InputTokens ?? existing?.InputTokens,
                OutputTokens = metadata.OutputTokens ?? existing?.OutputTokens,
                ResponseTokens = cumulative,
                ContextPercent = metadata.ContextPercent ?? existing?.ContextPercent,
                ContextTokens = contextTokens ?? existing?.ContextTokens,
                UsageContributionTokens = currentUsage,
            };
            return true;
        }
        return false;
    }

    private static int? LatestAssistantUsageBeforeLocked(
        ChatTimelineState timeline,
        IReadOnlyDictionary<string, ChatEntryMetadata> metadata,
        int beforeIndex)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant ||
                !metadata.TryGetValue(entry.Id, out var entryMetadata))
            {
                continue;
            }
            var value = UsageValue(entryMetadata);
            if (value is > 0)
                return value;
        }
        return null;
    }

    private static int? UsageValue(ChatEntryMetadata metadata) =>
        metadata.ResponseTokens ??
        (metadata.InputTokens is { } input &&
         metadata.OutputTokens is { } output
            ? input + output
            : null);

    private bool TryReconcileExistingLocalQueuedUserEchoLocked(
        string threadId,
        string text,
        string attachmentCorrelationSignature,
        bool hasMediaEnvelope,
        ChatEntryMetadata confirmed)
    {
        if (!HasGatewayIdentity(confirmed) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }

        var candidates = new List<ChatTimelineItem>();
        var echoCandidates = new List<ChatPendingEchoCandidate>();
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.User ||
                !metadata.TryGetValue(entry.Id, out var existing) ||
                !existing.IsLocalQueuedSend ||
                !IsFreshLocalQueuedPromotion(existing, confirmed))
            {
                continue;
            }
            candidates.Add(entry);
            echoCandidates.Add(new ChatPendingEchoCandidate(
                entry.Id,
                entry.Text,
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                    existing.Attachments)));
        }

        var matchedMessageId = ChatAttachmentEchoCorrelation.SelectMatchingMessageId(
            echoCandidates,
            text,
            attachmentCorrelationSignature,
            hasMediaEnvelope);
        if (matchedMessageId is null)
            return false;

        var matched = candidates.First(candidate =>
            string.Equals(candidate.Id, matchedMessageId, StringComparison.Ordinal));
        var matchedMeta = metadata[matched.Id];
        metadata[matched.Id] = confirmed with
        {
            IsLocalQueuedSend = false,
            LocalQueuedMessageId = matchedMeta.LocalQueuedMessageId,
            Attachments = matchedMeta.Attachments,
        };
        return true;
    }

    private static bool IsFreshLocalQueuedPromotion(
        ChatEntryMetadata existing,
        ChatEntryMetadata confirmed)
    {
        if (existing.Timestamp is not { } existingTimestamp)
            return false;
        return confirmed.Timestamp is { } confirmedTimestamp
            ? (confirmedTimestamp - existingTimestamp).Duration() <=
              LocalEchoSuppressionWindow
            : DateTimeOffset.Now - existingTimestamp <= LocalEchoSuppressionWindow;
    }

    private bool ReconcileQueuedMessageEchoLocked(
        string threadId,
        string messageId,
        ChatEntryMetadata confirmed)
    {
        if (PromoteQueuedMessageLocked(threadId, messageId, confirmed))
            return true;
        if (!HasGatewayIdentity(confirmed) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        var match = metadata.FirstOrDefault(pair =>
            string.Equals(
                pair.Value.LocalQueuedMessageId,
                messageId,
                StringComparison.Ordinal));
        if (string.IsNullOrEmpty(match.Key))
            return false;
        metadata[match.Key] = confirmed with
        {
            IsLocalQueuedSend = false,
            LocalQueuedMessageId = messageId,
            Attachments = match.Value.Attachments,
        };
        return true;
    }

    private AssistantQueueFrameDisposition ClassifyAssistantQueueFrameLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if ((!string.IsNullOrEmpty(gatewayMessageId) || openClawSeq is not null) &&
            IsIdentifiedCompletedAssistantDuplicateLocked(
                threadId,
                assistantText,
                gatewayMessageId,
                openClawSeq))
        {
            return AssistantQueueFrameDisposition.Drop;
        }
        if (string.IsNullOrEmpty(gatewayMessageId) &&
            openClawSeq is null &&
            IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(
                threadId,
                assistantText))
        {
            return AssistantQueueFrameDisposition.Drop;
        }
        if (!_queue.IsLocallyInitiated(threadId) ||
            !_queue.TryGetSingleSendingMessage(threadId, out _) ||
            _lifecycle.HasActiveRun(threadId) ||
            _queue.IsAssistantFallbackPromoted(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline))
        {
            return AssistantQueueFrameDisposition.Render;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant)
                continue;
            if (entry.IsStreaming ||
                !string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
            {
                return AssistantQueueFrameDisposition.Render;
            }
            if (string.IsNullOrEmpty(gatewayMessageId) && openClawSeq is null)
                return AssistantQueueFrameDisposition.Drop;
            if (!_entryMeta.TryGetValue(threadId, out var metadata) ||
                !metadata.TryGetValue(entry.Id, out var existing))
            {
                return AssistantQueueFrameDisposition.Render;
            }
            var sameIdentity =
                !string.IsNullOrEmpty(gatewayMessageId) &&
                string.Equals(
                    existing.GatewayMessageId,
                    gatewayMessageId,
                    StringComparison.Ordinal) ||
                openClawSeq is not null && existing.OpenClawSeq == openClawSeq;
            return sameIdentity
                ? AssistantQueueFrameDisposition.Drop
                : AssistantQueueFrameDisposition.Render;
        }
        return AssistantQueueFrameDisposition.Render;
    }

    private bool IsIdentitylessAssistantRetransmitAcrossLocalUserBoundaryLocked(
        string threadId,
        string assistantText)
    {
        if (!_queue.IsLocallyInitiated(threadId) ||
            _lifecycle.HasActiveRun(threadId) ||
            !_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        var sawBoundary = false;
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (!sawBoundary)
            {
                if (entry.Kind == ChatTimelineItemKind.Assistant)
                    return false;
                if (entry.Kind == ChatTimelineItemKind.User &&
                    metadata.TryGetValue(entry.Id, out var entryMetadata) &&
                    entryMetadata.IsLocalQueuedSend)
                {
                    sawBoundary = true;
                }
                continue;
            }
            if (entry.Kind == ChatTimelineItemKind.Assistant)
            {
                return !entry.IsStreaming &&
                       string.Equals(
                           entry.Text,
                           assistantText,
                           StringComparison.Ordinal);
            }
            if (entry.Kind == ChatTimelineItemKind.User)
                return false;
        }
        return false;
    }

    private bool IsIdentifiedCompletedAssistantDuplicateLocked(
        string threadId,
        string assistantText,
        string? gatewayMessageId,
        int? openClawSeq)
    {
        if (!_timelines.TryGetValue(threadId, out var timeline) ||
            !_entryMeta.TryGetValue(threadId, out var metadata))
        {
            return false;
        }
        for (var i = timeline.Entries.Count - 1; i >= 0; i--)
        {
            var entry = timeline.Entries[i];
            if (entry.Kind != ChatTimelineItemKind.Assistant ||
                entry.IsStreaming ||
                !metadata.TryGetValue(entry.Id, out var existing))
            {
                continue;
            }
            var bothHaveIds = !string.IsNullOrEmpty(gatewayMessageId) &&
                              !string.IsNullOrEmpty(existing.GatewayMessageId);
            if (bothHaveIds &&
                string.Equals(
                    existing.GatewayMessageId,
                    gatewayMessageId,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (!bothHaveIds &&
                openClawSeq is not null &&
                existing.OpenClawSeq == openClawSeq &&
                string.Equals(entry.Text, assistantText, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(gatewayMessageId) &&
                    string.IsNullOrEmpty(existing.GatewayMessageId))
                {
                    metadata[entry.Id] = existing with
                    {
                        GatewayMessageId = gatewayMessageId,
                    };
                }
                return true;
            }
        }
        return false;
    }

    private void ApplyEventLocked(
        string threadId,
        ChatEvent evt,
        ChatEntryMetadata? metadata)
    {
        var current = GetOrCreateTimelineLocked(threadId);
        var beforeIds = current.Entries.Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var next = ChatTimelineReducer.Apply(current, evt);
        _timelines[threadId] = next;
        if (metadata is null)
            return;
        var threadMetadata = GetOrCreateThreadMetaLocked(threadId);
        foreach (var entry in next.Entries)
        {
            if (!beforeIds.Contains(entry.Id) && !threadMetadata.ContainsKey(entry.Id))
                threadMetadata[entry.Id] = metadata;
        }

        // Streaming assistant frames reconcile into the SAME entry id
        // (see ChatTimelineReducer.UpsertAssistant), so the new-entry-only
        // assignment above never touches it again after creation. Assistant
        // structured media content can still refine across frames (e.g. a
        // legacy directive resolved to a structured reference on a later
        // frame), so merge it into the already-existing reconciled entry's
        // metadata explicitly.
        if (metadata.AssistantContent is not null)
        {
            for (var i = next.Entries.Count - 1; i >= 0; i--)
            {
                var entry = next.Entries[i];
                if (entry.Kind == ChatTimelineItemKind.User)
                    break;
                if (entry.Kind != ChatTimelineItemKind.Assistant)
                    continue;

                if (beforeIds.Contains(entry.Id) &&
                    threadMetadata.TryGetValue(entry.Id, out var existingEntryMeta))
                {
                    var mergedContent = ChatAssistantContentProjector.MergeLiveUpdate(
                        existingEntryMeta.AssistantContent,
                        metadata.AssistantContent);
                    if (!ReferenceEquals(mergedContent, existingEntryMeta.AssistantContent))
                    {
                        threadMetadata[entry.Id] = existingEntryMeta with
                        {
                            AssistantContent = mergedContent,
                        };
                    }
                }
                break;
            }
        }
    }

    private Dictionary<string, ChatEntryMetadata> GetOrCreateThreadMetaLocked(
        string threadId)
    {
        if (!_entryMeta.TryGetValue(threadId, out var metadata))
        {
            metadata = new Dictionary<string, ChatEntryMetadata>(StringComparer.Ordinal);
            _entryMeta[threadId] = metadata;
        }
        return metadata;
    }

    private ChatEntryMetadata BuildLiveMetaLocked(
        string threadId,
        long? tsMs = null,
        string? gatewayMessageId = null,
        int? openClawSeq = null,
        bool isLocalQueuedSend = false,
        string? localQueuedMessageId = null,
        string? openClawKind = null,
        long? compactionTokensBefore = null,
        long? compactionTokensAfter = null,
        IReadOnlyList<ChatAttachmentPresentation>? attachments = null,
        ChatAssistantContentPresentation? assistantContent = null)
    {
        var timestamp = tsMs is { } value && value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime()
            : DateTimeOffset.Now;
        return new ChatEntryMetadata(
            timestamp,
            _presentation.ModelForThread(threadId),
            GatewayMessageId: gatewayMessageId,
            OpenClawSeq: openClawSeq,
            OpenClawKind: openClawKind,
            CompactionTokensBefore: compactionTokensBefore,
            CompactionTokensAfter: compactionTokensAfter,
            IsLocalQueuedSend: isLocalQueuedSend,
            LocalQueuedMessageId: localQueuedMessageId,
            Attachments: attachments,
            AssistantContent: assistantContent);
    }

    private ChatOpenedLifecycleTransition? AddResetAcceptedRunIdLocked(
        string threadId,
        string runId)
    {
        return ApplyBufferedLifecycleOpenLocked(
            threadId,
            _reset.AddAcceptedRun(threadId, runId),
            allowRemoteTurn: false);
    }

    private ChatOpenedLifecycleTransition? ApplyBufferedLifecycleOpenLocked(
        string threadId,
        AgentEventInfo? lifecycleStart,
        bool allowRemoteTurn)
    {
        if (string.IsNullOrEmpty(lifecycleStart?.RunId))
            return null;

        _lifecycle.StartRun(threadId, lifecycleStart.RunId);
        var deferredAbortCount =
            _lifecycle.TakePendingAbortCount(threadId);
        string? deferredAbortRunId = null;
        if (deferredAbortCount > 0)
        {
            deferredAbortRunId = lifecycleStart.RunId;
            _lifecycle.MarkDeferredAbort(
                threadId,
                deferredAbortRunId);
        }
        return new(
            lifecycleStart,
            allowRemoteTurn && deferredAbortCount == 0,
            deferredAbortRunId,
            deferredAbortCount);
    }

    private void ApplyOpenedResetLifecycleStartLocked(
        string threadId,
        AgentEventInfo? lifecycleStart)
    {
        if (!string.IsNullOrEmpty(lifecycleStart?.RunId))
            _lifecycle.StartRun(threadId, lifecycleStart.RunId);
    }

    private ChatDataSnapshot BuildSnapshotLocked(ChatProjectionContext context) =>
        ChatSnapshotProjector.Project(CaptureProjectionInputLocked(context));

    private ChatSnapshotProjectionInput CaptureProjectionInputLocked(
        ChatProjectionContext context) =>
        _presentation.CaptureProjectionInput(
            timelines: new Dictionary<string, ChatTimelineState>(_timelines),
            timelineGenerations: _reset.SnapshotVersions(),
            historyRevisions: _history.SnapshotRevisions(),
            queuedMessages: _queue.SnapshotMessages(),
            status: _status,
            context);

    private ChatTimelineState GetOrCreateTimelineLocked(string threadId)
    {
        if (!_timelines.TryGetValue(threadId, out var current))
        {
            current = ChatTimelineState.Initial();
            _timelines[threadId] = current;
        }
        return current;
    }

    private void EnsureTimelinesForSessionsLocked()
    {
        foreach (var session in _presentation.SessionSnapshot())
        {
            if (!string.IsNullOrEmpty(session.Key) &&
                !_timelines.ContainsKey(session.Key))
            {
                _timelines[session.Key] = ChatTimelineState.Initial();
            }
        }
    }

    private long GetResetVersionLocked(string threadId) =>
        _reset.GetVersion(threadId);

    internal bool IsRuntimeGenerationCurrent(
        string threadId,
        ChatRuntimeGeneration generation)
    {
        lock (_gate)
        {
            return !_disposed &&
                   _history.ConnectionGeneration == generation.ConnectionGeneration &&
                   GetResetVersionLocked(threadId) == generation.ResetGeneration;
        }
    }

    private ChatRuntimeGeneration CurrentRuntimeGenerationLocked(string threadId) =>
        new(
            _history.ConnectionGeneration,
            GetResetVersionLocked(threadId));
}
