using OpenClaw.Chat;
using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Helpers;
#endif
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed record ChatHistoryLoadResult(
    ChatHistoryCommitToken Token,
    bool PublishSnapshot,
    ChatProviderNotification? Notification = null);

/// <summary>
/// Owns history request lifetime, in-flight coalescing, cancellation, retries,
/// and immutable transcript rebuild plans. Conversation state alone accepts
/// or rejects plans against its authoritative generation/reset token.
/// </summary>
internal sealed class ChatHistoryLoader : IDisposable
{
    private readonly record struct PendingReload(
        ChatHistoryCommitToken Token,
        bool Replacement);

    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IChatGatewayBridge _bridge;
    private readonly ChatConversationState _state;
    private readonly ChatMetadataStore _metadata;
    private readonly ChatStatePersistence _persistence;
    private readonly ChatTelemetryTracker _telemetry;
    private readonly Func<TimeSpan, CancellationToken, Func<Task>, Task> _retryScheduler;
    private readonly Action? _failureReservedForTesting;
    private readonly Dictionary<string, long> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatHistoryCommitToken> _authoritativePending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatHistoryCommitToken> _replacementPending =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ChatHistoryCommitToken, int> _retryCounts = new();
    private CancellationTokenSource _generationCancellation = new();
    private long _appliedStateGeneration;
    private long _requestSequence;
    private bool _disposed;

    internal ChatHistoryLoader(
        IChatGatewayBridge bridge,
        ChatConversationState state,
        ChatMetadataStore metadata,
        ChatStatePersistence persistence,
        ChatTelemetryTracker telemetry,
        Func<TimeSpan, CancellationToken, Func<Task>, Task>? retryScheduler = null,
        Action? failureReservedForTesting = null)
    {
        _bridge = bridge;
        _state = state;
        _metadata = metadata;
        _persistence = persistence;
        _telemetry = telemetry;
        _retryScheduler = retryScheduler ?? (static async (delay, token, retry) =>
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            await retry().ConfigureAwait(false);
        });
        _failureReservedForTesting = failureReservedForTesting;
    }

    internal event EventHandler<ChatHistoryLoadResult>? Completed;

    internal Task LoadAsync(
        string threadId,
        bool force = false,
        CancellationToken cancellationToken = default,
        bool authoritative = false,
        ChatHistoryCommitToken? expectedToken = null) =>
        LoadCoreAsync(
            threadId,
            force,
            cancellationToken,
            authoritative,
            expectedToken,
            replacement: false,
            supersedeReplacement: false);

    internal Task LoadReplacementAsync(
        string threadId,
        ChatHistoryCommitToken token,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            _authoritativePending.Remove(threadId);
            _replacementPending.Remove(threadId);
            foreach (var retryToken in _retryCounts.Keys
                         .Where(candidate => string.Equals(
                             candidate.ThreadId,
                             threadId,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                _retryCounts.Remove(retryToken);
            }
        }
        return LoadCoreAsync(
            threadId,
            force: true,
            cancellationToken,
            authoritative: false,
            expectedToken: token,
            replacement: true,
            supersedeReplacement: true);
    }

    internal ChatStatusTransition ApplyStatusAndAdvanceGeneration(
        ConnectionStatus status,
        ChatProjectionContext context)
    {
        CancellationTokenSource? previous = null;
        ChatStatusTransition transition;
        lock (_gate)
        {
            transition = _state.ApplyStatus(status, context);
            if ((transition.Reconnected || transition.Disconnected) &&
                transition.HistoryGeneration > _appliedStateGeneration)
            {
                _appliedStateGeneration = transition.HistoryGeneration;
                previous = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                _inFlight.Clear();
                _authoritativePending.Clear();
                _replacementPending.Clear();
                _retryCounts.Clear();
                _state.ActivateHistoryGeneration(transition.HistoryGeneration);
            }
        }
        previous?.Cancel();
        previous?.Dispose();
        return transition;
    }

    internal void ApplyReset(string threadId, long resetGeneration)
    {
        lock (_gate)
        {
            RemoveOlderPendingReload(
                _authoritativePending,
                threadId,
                resetGeneration);
            RemoveOlderPendingReload(
                _replacementPending,
                threadId,
                resetGeneration);
            foreach (var token in _retryCounts.Keys
                         .Where(candidate => string.Equals(
                             candidate.ThreadId,
                             threadId,
                             StringComparison.Ordinal) &&
                             candidate.ResetGeneration < resetGeneration)
                         .ToArray())
            {
                _retryCounts.Remove(token);
            }
        }
    }

    private static void RemoveOlderPendingReload(
        Dictionary<string, ChatHistoryCommitToken> pending,
        string threadId,
        long resetGeneration)
    {
        if (pending.TryGetValue(threadId, out var token) &&
            token.ResetGeneration < resetGeneration)
        {
            pending.Remove(threadId);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            cancellation = _generationCancellation;
            _inFlight.Clear();
            _authoritativePending.Clear();
            _replacementPending.Clear();
            _retryCounts.Clear();
        }
        cancellation.Cancel();
        cancellation.Dispose();
        Completed = null;
    }

    private async Task LoadCoreAsync(
        string threadId,
        bool force,
        CancellationToken cancellationToken,
        bool authoritative,
        ChatHistoryCommitToken? expectedToken,
        bool replacement,
        bool supersedeReplacement)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(threadId))
            return;

        CancellationToken generationToken;
        long requestId;
        ChatHistoryCommitToken commitToken;
        string? model;
        Task? generationActivation;
        bool canBegin;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (expectedToken is { } expected &&
                !_state.IsHistoryRequestCurrent(expected))
            {
                return;
            }
            if (_inFlight.ContainsKey(threadId))
            {
                if (replacement)
                {
                    var replacementToken = expectedToken ??
                        _state.CaptureHistoryToken(threadId);
                    if (supersedeReplacement)
                        _replacementPending[threadId] = replacementToken;
                    else
                        _replacementPending.TryAdd(threadId, replacementToken);
                }
                else if (authoritative)
                {
                    if (expectedToken is { } retryToken)
                    {
                        _authoritativePending.TryAdd(threadId, retryToken);
                    }
                    else
                    {
                        _authoritativePending[threadId] =
                            _state.CaptureHistoryToken(threadId);
                    }
                }
                return;
            }
            requestId = ++_requestSequence;
            _inFlight[threadId] = requestId;
            generationToken = _generationCancellation.Token;
            canBegin = _state.TryBeginHistory(
                threadId,
                force,
                expectedToken,
                out commitToken,
                out model,
                out generationActivation);
        }

        if (!canBegin)
        {
            CompleteInFlight(
                threadId,
                requestId,
                out var pendingReload);
            if (pendingReload is { } pending)
            {
                _ = ObserveRetryAsync(RerunAfterActivationAsync(
                    threadId,
                    generationActivation,
                    generationToken,
                    pending));
                return;
            }
            if (generationActivation is not null)
            {
                using var activationCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        generationToken);
                await generationActivation
                    .WaitAsync(activationCancellation.Token)
                    .ConfigureAwait(false);
                await LoadCoreAsync(
                        threadId,
                        force,
                        cancellationToken,
                        authoritative,
                        commitToken,
                        replacement,
                        supersedeReplacement: false)
                    .ConfigureAwait(false);
            }
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);
        var requestStartedAt = DateTimeOffset.Now;
        var operation = _telemetry.StartHistoryLoad(
            force ? ChatHistoryTelemetrySource.Forced : ChatHistoryTelemetrySource.Initial);
        var outcome = ChatTelemetryOutcome.Success;
        Exception? failure = null;
        Task<ChatHistoryInfo>? request = null;
        try
        {
            request = _bridge.RequestChatHistoryAsync(threadId);
            var history = await request
                .WaitAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                return;
            }

            var plan = BuildPlan(
                history,
                threadId,
                model,
                commitToken.ResetGeneration);
            var committed = _state.CommitHistory(
                commitToken,
                plan,
                requestStartedAt,
                authoritative);
            if (!committed)
            {
                outcome = ChatTelemetryOutcome.Canceled;
                return;
            }
            lock (_gate)
                _retryCounts.Remove(commitToken);
            Completed?.Invoke(
                this,
                new ChatHistoryLoadResult(
                    commitToken,
                    PublishSnapshot: true));
        }
        catch (OperationCanceledException)
        {
            outcome = ChatTelemetryOutcome.Canceled;
            if (request is not null)
                _ = ObserveCanceledRequestAsync(request);
        }
        catch (Exception ex)
        {
            _failureReservedForTesting?.Invoke();
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                failure = null;
                return;
            }
            outcome = ChatTelemetryOutcome.Failure;
            failure = ex;
            var shouldRetry = false;
            lock (_gate)
            {
                if (!_disposed &&
                    _state.CanRetryHistory(commitToken, authoritative))
                {
                    _retryCounts.TryGetValue(commitToken, out var retryCount);
                    shouldRetry = retryCount < MaxRetries;
                    if (shouldRetry)
                        _retryCounts[commitToken] = retryCount + 1;
                }
            }
            if (_state.IsHistoryRequestCurrent(commitToken))
            {
                Completed?.Invoke(
                    this,
                    new ChatHistoryLoadResult(
                        commitToken,
                        PublishSnapshot: false,
                        new ChatProviderNotification(
                            ChatProviderNotificationKind.Error,
                            threadId,
                            LocalizationHelper.GetString(
                                "Chat_Notification_LoadHistoryFailed"),
                            ex.Message)));
            }
            if (!_state.IsHistoryRequestCurrent(commitToken))
            {
                outcome = ChatTelemetryOutcome.Canceled;
                failure = null;
                shouldRetry = false;
            }
            if (shouldRetry)
            {
                _ = ObserveRetryAsync(_retryScheduler(
                    RetryDelay,
                    generationToken,
                    () => LoadCoreAsync(
                        threadId,
                        force: true,
                        CancellationToken.None,
                        authoritative,
                        commitToken,
                        replacement,
                        supersedeReplacement: false)));
            }
        }
        finally
        {
            _telemetry.FinishHistoryLoad(operation, outcome, failure);
            CompleteInFlight(threadId, requestId, out var pendingReload);
            if (pendingReload is { } pending)
            {
                _ = LoadCoreAsync(
                    threadId,
                    force: true,
                    CancellationToken.None,
                    authoritative: !pending.Replacement,
                    expectedToken: pending.Token,
                    replacement: pending.Replacement,
                    supersedeReplacement: false);
            }
        }
    }

    private async Task RerunAfterActivationAsync(
        string threadId,
        Task? generationActivation,
        CancellationToken generationToken,
        PendingReload pending)
    {
        if (generationActivation is not null)
        {
            await generationActivation
                .WaitAsync(generationToken)
                .ConfigureAwait(false);
        }
        generationToken.ThrowIfCancellationRequested();
        await LoadCoreAsync(
                threadId,
                force: true,
                CancellationToken.None,
                authoritative: !pending.Replacement,
                expectedToken: pending.Token,
                replacement: pending.Replacement,
                supersedeReplacement: false)
            .ConfigureAwait(false);
    }

    private ChatHistoryRebuildPlan BuildPlan(
        ChatHistoryInfo history,
        string threadId,
        string? model,
        long resetGeneration)
    {
        var timeline = ChatTimelineState.Initial() with { HistoryLoaded = true };
        var metadata = new Dictionary<string, ChatEntryMetadata>(StringComparer.Ordinal);
        var cachedTools = _metadata.GetToolMetadata(
            history.SessionId,
            threadId,
            resetGeneration);
        var positionalToolMetadataTrusted = true;
        var attachmentMatcher = _metadata.CreateAttachmentMatcher(
            history.SessionId,
            threadId,
            resetGeneration);
        var nextAssistantIsAborted = false;
        var pendingUnkeyedToolCalls = new Queue<string>();
        var pendingVerifiedCallLookups =
            new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingVerifiedResultLookups =
            new Dictionary<string, int>(StringComparer.Ordinal);
        var seenVerifiedResults =
            new HashSet<string>(StringComparer.Ordinal);
        var syntheticToolCallSequence = 0;
        ChatMessageInfo? suppressedAbortedAssistant = null;

        ChatTimelineState Apply(
            ChatTimelineState current,
            ChatEvent evt,
            ChatEntryMetadata? entryMetadata)
        {
            var before = current.Entries
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);
            var next = ChatTimelineReducer.Apply(current, evt);
            if (entryMetadata is not null)
            {
                foreach (var entry in next.Entries)
                {
                    if (!before.Contains(entry.Id) && !metadata.ContainsKey(entry.Id))
                        metadata[entry.Id] = entryMetadata;
                }
                if (entryMetadata.AssistantContent is not null &&
                    next.ActiveAssistantId is { } assistantId &&
                    metadata.TryGetValue(assistantId, out var existingMetadata))
                {
                    metadata[assistantId] = existingMetadata with
                    {
                        AssistantContent =
                            ChatAssistantContentProjector.MergeLiveUpdate(
                                existingMetadata.AssistantContent,
                                entryMetadata.AssistantContent),
                    };
                }
            }
            return next;
        }

        var orderedMessages = OrderHistoryMessages(history.Messages);
        var replayParts =
            ChatHistoryReplayProjection.Project(orderedMessages).ToArray();
        var reservedToolCallIds = replayParts
            .SelectMany(static part => part.ToolContent)
            .Select(static tool => tool.CallId)
            .Where(static callId => !string.IsNullOrWhiteSpace(callId))
            .ToHashSet(StringComparer.Ordinal);

        string AllocateSyntheticToolCallId()
        {
            while (true)
            {
                var candidate =
                    $"history-tool-{syntheticToolCallSequence++}";
                if (reservedToolCallIds.Add(candidate))
                    return candidate;
            }
        }

        ChatMetadataStore.CachedToolMeta? MatchPositionalToolMetadata(
            long historyTsMs) =>
            positionalToolMetadataTrusted
                ? ChatMetadataStore.TryMatchCachedTool(
                    cachedTools,
                    historyTsMs)
                : null;

        ChatMetadataStore.CachedToolMeta? MatchVerifiedToolMetadata(
            string toolCallId,
            long historyTsMs,
            bool isCall)
        {
            var counterpartLookups = isCall
                ? pendingVerifiedResultLookups
                : pendingVerifiedCallLookups;
            if (counterpartLookups.TryGetValue(
                    toolCallId,
                    out var counterpartCount))
            {
                if (counterpartCount == 1)
                    counterpartLookups.Remove(toolCallId);
                else
                    counterpartLookups[toolCallId] = counterpartCount - 1;
                if (!isCall)
                    seenVerifiedResults.Add(toolCallId);
                return null;
            }

            if (!isCall && !seenVerifiedResults.Add(toolCallId))
                return null;

            var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
                cachedTools,
                toolCallId,
                historyTsMs);
            var ownLookups = isCall
                ? pendingVerifiedCallLookups
                : pendingVerifiedResultLookups;
            ownLookups.TryGetValue(toolCallId, out var ownCount);
            ownLookups[toolCallId] = ownCount + 1;
            if (lookup.Outcome ==
                ChatMetadataStore.CachedToolLookupOutcome.Unmatched)
            {
                positionalToolMetadataTrusted = false;
            }
            return lookup.Match;
        }

        foreach (var replayPart in replayParts)
        {
            var message = replayPart.Message;
            if (suppressedAbortedAssistant is not null)
            {
                if (ReferenceEquals(suppressedAbortedAssistant, message))
                    continue;
                suppressedAbortedAssistant = null;
            }

            var role = message.Role?.ToLowerInvariant() ?? string.Empty;
            var rawText = replayPart.Text;
            var userProjection = role == "user"
                ? GatewayMediaMessageProjection.Project(rawText)
                : null;
            var entryMetadata = new ChatEntryMetadata(
                message.Ts > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(message.Ts).ToLocalTime()
                    : null,
                model,
                message.InputTokens,
                message.OutputTokens,
                message.ResponseTokens,
                message.ContextPercent,
                GatewayMessageId: message.OpenClawId,
                OpenClawSeq: message.OpenClawSeq,
                OpenClawKind: message.OpenClawKind,
                CompactionTokensBefore: message.CompactionTokensBefore,
                CompactionTokensAfter: message.CompactionTokensAfter,
                AssistantContent: role == "assistant"
                    ? ChatAssistantContentProjector.Project(
                        replayPart.AssistantContentParts)
                    : null);
            var text = ChatContentFormatting.TruncateForChatEntry(
                ChatMetadataStore.EscapeUntrustedAttachmentMarkerLines(
                    userProjection?.HasMediaEnvelope == true
                        ? userProjection.ReconciliationText
                        : rawText));
            if (userProjection is not null)
            {
                var cachedAttachment = attachmentMatcher.TryMatch(
                    userProjection.ReconciliationText,
                    userProjection.AttachmentCorrelationSignature,
                    message.Ts);
                var attachmentPresentations = cachedAttachment is not null
                    ? ChatMetadataStore.CreatePersistedLocalPresentations(
                        cachedAttachment.Attachments)
                    : userProjection.Attachments;
                entryMetadata = entryMetadata with
                {
                    Attachments = attachmentPresentations,
                };
            }
            var hasStructuredToolContent =
                replayPart.ToolContent.Count > 0;
            var hasUserAttachments =
                entryMetadata.Attachments is { Count: > 0 };
            var hasAssistantMedia =
                entryMetadata.AssistantContent is { Media.Count: > 0 };

            if (role == "user" &&
                _persistence.IsMessageAborted(
                    threadId,
                    message.OpenClawId,
                    resetGeneration))
            {
                nextAssistantIsAborted = true;
            }
            var gatewayAborted = role == "assistant" &&
                !string.IsNullOrEmpty(message.StopReason) &&
                !string.Equals(message.StopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.StopReason, "toolUse", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(message.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase);
            var isFirstAssistantPart =
                role == "assistant" && replayPart.IsFirstPart;
            var markAborted = isFirstAssistantPart &&
                (nextAssistantIsAborted || gatewayAborted);
            if (isFirstAssistantPart)
                nextAssistantIsAborted = false;
            if (markAborted)
            {
                timeline = Apply(
                    timeline,
                    new ChatStatusEvent(
                        "Response was stopped",
                        ChatTone.Warning),
                    entryMetadata);
                timeline = ChatTimelineReducer.Apply(
                    timeline,
                    new ChatTurnEndEvent());
                suppressedAbortedAssistant = message;
                continue;
            }

            if (string.IsNullOrEmpty(text) &&
                !hasStructuredToolContent &&
                !hasUserAttachments &&
                !hasAssistantMedia)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(text) ||
                (role == "user" && hasUserAttachments) ||
                (role == "assistant" && hasAssistantMedia))
            {
                switch (role)
                {
                    case "user":
                        if (ChatContentFormatting.LooksLikeApprovalSlashCommand(text) ||
                            NativeToolProjector.LooksLikeSystemControlNote(text))
                        {
                            timeline = Apply(
                                timeline,
                                new ChatStatusEvent(text, ChatTone.Dim),
                                entryMetadata);
                        }
                        else
                        {
                            timeline = timeline with
                            {
                                ActiveAssistantId = null,
                                ActiveReasoningId = null,
                            };
                            timeline = Apply(
                                timeline,
                                new ChatUserMessageEvent(text),
                                entryMetadata);
                        }
                        break;
                    case "assistant":
                        if (ChatMessageInfo.IsSilentAssistantDirective(
                                role,
                                text))
                        {
                            break;
                        }
                        if (NativeToolProjector.LooksLikeSystemControlNote(text))
                        {
                            timeline = Apply(
                                timeline,
                                new ChatStatusEvent(text, ChatTone.Dim),
                                entryMetadata);
                        }
                        else if (NativeToolProjector.LooksLikeFlattenedToolOutput(text))
                        {
                            var cached =
                                MatchPositionalToolMetadata(message.Ts);
                            var assistantHistoryCallId =
                                AllocateSyntheticToolCallId();
                            var kind = cached?.ToolName ??
                                NativeToolProjector.ClassifyFlattenedToolOutput(text);
                            var label = cached?.Label ??
                                NativeToolProjector.ExtractFlattenedToolSummary(text);
                            timeline = Apply(
                                timeline,
                                new ChatToolStartEvent(
                                    label,
                                    kind,
                                    ToolArgs: cached?.ToolArgs,
                                    ToolCallId: assistantHistoryCallId,
                                    IdentityStrength: cached?.IdentityStrength ??
                                        NativeToolProjector.ClassifyHistoryIdentityStrength(
                                            kind)),
                                entryMetadata);
                            timeline = Apply(
                                timeline,
                                new ChatToolOutputEvent(
                                    text,
                                    ToolCallId: assistantHistoryCallId),
                                entryMetadata);
                        }
                        else
                        {
                            timeline = Apply(
                                timeline,
                                new ChatMessageEvent(
                                    ChatContentFormatting.RepairContentBlockSeams(
                                        text)),
                                entryMetadata);
                            if (timeline.ActiveToolCalls.Count > 0 ||
                                timeline.ActiveToolCallId is not null)
                            {
                                timeline = timeline with
                                {
                                    ActiveAssistantId = null,
                                    ActiveReasoningId = null,
                                };
                            }
                            else
                            {
                                timeline = ChatTimelineReducer.Apply(
                                    timeline,
                                    new ChatTurnEndEvent());
                            }
                        }
                        break;
                    case "toolresult":
                    case "tool_result":
                        if (hasStructuredToolContent)
                            break;
                        var cachedTool =
                            MatchPositionalToolMetadata(message.Ts);
                        var toolResultHistoryCallId =
                            AllocateSyntheticToolCallId();
                        var toolKind = cachedTool?.ToolName ??
                            NativeToolProjector.ClassifyFlattenedToolOutput(text);
                        var toolLabel = cachedTool?.Label ??
                            NativeToolProjector.ExtractFlattenedToolSummary(text);
                        timeline = Apply(
                            timeline,
                            new ChatToolStartEvent(
                                toolLabel,
                                toolKind,
                                ToolArgs: cachedTool?.ToolArgs,
                                ToolCallId: toolResultHistoryCallId,
                                IdentityStrength: cachedTool?.IdentityStrength ??
                                    NativeToolProjector.ClassifyHistoryIdentityStrength(
                                        toolKind)),
                            entryMetadata);
                        timeline = Apply(
                            timeline,
                            new ChatToolOutputEvent(
                                text,
                                ToolCallId: toolResultHistoryCallId),
                            entryMetadata);
                        break;
                    case "system":
                    case "tool":
                        timeline = Apply(
                            timeline,
                            new ChatStatusEvent(text, ChatTone.Dim),
                            entryMetadata);
                        break;
                    default:
                        timeline = Apply(
                            timeline,
                            new ChatMessageEvent(
                                ChatContentFormatting.RepairContentBlockSeams(
                                    text)),
                            entryMetadata);
                        timeline = ChatTimelineReducer.Apply(
                            timeline,
                            new ChatTurnEndEvent());
                        break;
                }
            }

            foreach (var toolBlock in replayPart.ToolContent)
            {
                if (toolBlock.Kind == ChatToolContentKind.Call)
                {
                    var args =
                        ChatHistoryReplayProjection.ProjectToolArgs(
                            toolBlock.Args);
                    var callId = toolBlock.CallId;
                    if (string.IsNullOrWhiteSpace(callId))
                    {
                        _ = MatchPositionalToolMetadata(message.Ts);
                        callId = AllocateSyntheticToolCallId();
                        pendingUnkeyedToolCalls.Enqueue(callId);
                    }
                    else
                    {
                        _ = MatchVerifiedToolMetadata(
                            callId,
                            message.Ts,
                            isCall: true);
                    }
                    timeline = Apply(
                        timeline,
                        new ChatToolStartEvent(
                            ChatHistoryReplayProjection.ToolLabel(
                                toolBlock.ToolName,
                                args),
                            toolBlock.ToolName,
                            args,
                            callId),
                        entryMetadata);
                    continue;
                }

                var resultCallId = toolBlock.CallId;
                var hasVerifiedCallId =
                    !string.IsNullOrWhiteSpace(resultCallId);
                if (!hasVerifiedCallId)
                {
                    resultCallId =
                        pendingUnkeyedToolCalls.Count > 0
                            ? pendingUnkeyedToolCalls.Dequeue()
                            : AllocateSyntheticToolCallId();
                }
                var resolvedCallId = resultCallId!;
                var correlationKey = new ChatToolCorrelationKey(
                    RunId: null,
                    LegacyTurn: timeline.ToolLegacyTurn,
                    ToolCallId: resolvedCallId);
                var verifiedCached = hasVerifiedCallId
                    ? MatchVerifiedToolMetadata(
                        resolvedCallId,
                        message.Ts,
                        isCall: false)
                    : null;
                if (!timeline.ActiveToolCalls.ContainsKey(correlationKey))
                {
                    var cached = hasVerifiedCallId
                        ? verifiedCached
                        : MatchPositionalToolMetadata(message.Ts);
                    var toolName =
                        cached?.ToolName ?? toolBlock.ToolName;
                    timeline = Apply(
                        timeline,
                        new ChatToolStartEvent(
                            cached?.Label ?? toolName,
                            toolName,
                            ToolArgs: cached?.ToolArgs,
                            ToolCallId: resolvedCallId,
                            IdentityStrength:
                                cached?.IdentityStrength ??
                                NativeToolProjector.ClassifyHistoryIdentityStrength(
                                    toolName)),
                        entryMetadata);
                }
                var output = NativeToolProjector.TruncateToolOutput(
                    toolBlock.Text ?? string.Empty);
                timeline = Apply(
                    timeline,
                    toolBlock.IsError
                        ? new ChatToolErrorEvent(
                            output,
                            resolvedCallId)
                        : new ChatToolOutputEvent(
                            output,
                            resolvedCallId),
                    entryMetadata);
            }
        }

        if (nextAssistantIsAborted)
        {
            timeline = Apply(
                timeline,
                new ChatStatusEvent("Response was stopped", ChatTone.Warning),
                null);
            timeline = ChatTimelineReducer.Apply(timeline, new ChatTurnEndEvent());
        }
        timeline = ChatTimelineReducer.Apply(
            timeline,
            new ChatTurnEndEvent());
        timeline = timeline with
        {
            TurnActive = false,
            ActiveAssistantId = null,
            ActiveReasoningId = null,
        };
        var maxSequence = history.Messages
            .Where(message => message.OpenClawSeq is not null)
            .Select(message => message.OpenClawSeq!.Value)
            .DefaultIfEmpty(int.MinValue)
            .Max();
        return new(history.SessionId, timeline, metadata, maxSequence);
    }

    private void CompleteInFlight(
        string threadId,
        long requestId,
        out PendingReload? pendingReload)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(threadId, out var current) ||
                current != requestId)
            {
                pendingReload = null;
                return;
            }
            _inFlight.Remove(threadId);
            pendingReload = null;
            if (_disposed)
                return;
            if (_replacementPending.Remove(threadId, out var replacementToken))
            {
                pendingReload = new(replacementToken, Replacement: true);
                return;
            }
            if (_authoritativePending.Remove(threadId, out var authoritativeToken))
                pendingReload = new(authoritativeToken, Replacement: false);
        }
    }

    private static List<ChatMessageInfo> OrderHistoryMessages(
        IReadOnlyList<ChatMessageInfo> messages)
    {
        var indexed = messages
            .Select((message, index) => (Message: message, Index: index))
            .ToList();
        var sequencedCount = indexed.Count(item =>
            item.Message.OpenClawSeq is not null);
        if (sequencedCount == indexed.Count)
        {
            return indexed
                .OrderBy(item => item.Message.OpenClawSeq)
                .ThenBy(item => item.Index)
                .Select(item => item.Message)
                .ToList();
        }
        if (sequencedCount == 0)
        {
            return indexed
                .OrderBy(item => item.Message.Ts)
                .ThenBy(item => item.Index)
                .Select(item => item.Message)
                .ToList();
        }
        return indexed
            .OrderBy(item => item.Index)
            .Select(item => item.Message)
            .ToList();
    }

    private static async Task ObserveRetryAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"[ChatHistory] Retry scheduler failed: {ex.GetType().Name}");
        }
    }

    private static async Task ObserveCanceledRequestAsync(Task request)
    {
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug(
                $"[ChatHistory] Canceled request completed with {ex.GetType().Name}");
        }
    }
}
