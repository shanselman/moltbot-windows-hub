using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Telemetry;
using OpenClawTray.Chat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

[Collection("Chat telemetry")]
public class OpenClawChatDataProviderTests
{
    private sealed class ChatActivityCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public ChatActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == OpenClawActivitySourceName.OpenClaw.ToTelemetryName(),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Stopped.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ChatMetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)> _measurements = [];

        public ChatMetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OpenClawMeterName.OpenClaw.ToTelemetryName() &&
                    instrument.Name.StartsWith("openclaw.chat.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
                _measurements.Enqueue((instrument.Name, tags.ToArray())));
            _listener.Start();
        }

        public string[] TagsFor(string metricName, string tagName) =>
            _measurements
                .Where(measurement => measurement.Name == metricName)
                .Select(measurement =>
                    measurement.Tags.First(tag => tag.Key == tagName).Value?.ToString() ?? string.Empty)
                .ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class FakeBridge : IChatGatewayBridge
    {
        public bool IsConnected { get; set; }
        public ConnectionStatus CurrentStatus { get; set; }
        public string? MainSessionKey { get; set; }
        public bool HasHandshakeSnapshot { get; set; }
        public List<string> SentMessages { get; } = new();
        public List<string?> SentSessionKeys { get; } = new();
        public List<string?> SentSessionIds { get; } = new();
        public List<IReadOnlyList<ChatAttachment>?> SentAttachments { get; } = new();
        public List<string?> SentIdempotencyKeys { get; } = new();
        public Queue<ChatSendResult> SendResults { get; } = new();
        public List<string> AbortedRunIds { get; } = new();
        public Func<string, string?, string?, Task>? SendBehavior { get; set; }
        public Func<string, string, Task>? PatchSessionModelBehavior { get; set; }
        public Func<string, Task>? ClearSessionModelBehavior { get; set; }
        public Func<string, string, Task>? PatchSessionThinkingLevelBehavior { get; set; }
        public Func<string, Task>? ClearSessionThinkingLevelBehavior { get; set; }
        public Func<string?, Task<ChatHistoryInfo>>? HistoryBehavior { get; set; }
        public Func<string, Task>? AbortBehavior { get; set; }
        public SessionInfo[] Sessions { get; set; } = Array.Empty<SessionInfo>();
        public ModelsListInfo? CurrentModels { get; set; }
        // Configurable commands.list result + a call counter for the
        // request/response protocol API.
        public CommandCatalog CommandCatalogResult { get; set; } = new CommandCatalog { IsSupported = true };
        public Func<CommandCatalogQuery?, Task<CommandCatalog>>? ListCommandsBehavior { get; set; }
        public int ListCommandsCallCount { get; private set; }
        public CommandCatalogQuery? LastListCommandsQuery { get; private set; }
        public SessionCreateResult CreateSessionResult { get; set; } = new()
        {
            Ok = true,
            Key = "agent:main:new-session"
        };
        public List<SessionCreateRequest> CreateSessionRequests { get; } = new();
        public List<string> ResetSessionKeys { get; } = new();
        public SessionResetResult ResetSessionResult { get; set; } = new()
        {
            Ok = true,
            Key = "main"
        };
        public List<string> CompactSessionKeys { get; } = new();
        public List<string> ModelCompactSessionKeys { get; } = new();
        public SessionCompactResult CompactSessionResult { get; set; } = new()
        {
            Ok = true,
            Key = "main",
            Compacted = true
        };
        public Func<string, Task<SessionCompactResult>>? CompactSessionBehavior { get; set; }
        public int RequestSessionsCallCount { get; private set; }
        public List<string?> RequestedHistoryKeys { get; } = new();

        public SessionInfo[] GetSessionList() => Sessions;
        public ModelsListInfo? GetCurrentModelsList() => CurrentModels;
        public void StartProactiveBootstrap() { }

        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null)
        {
            ListCommandsCallCount++;
            LastListCommandsQuery = query;
            return ListCommandsBehavior?.Invoke(query) ?? Task.FromResult(CommandCatalogResult);
        }

        public Task<SessionCreateResult> CreateSessionAsync(SessionCreateRequest request)
        {
            CreateSessionRequests.Add(request);
            return Task.FromResult(CreateSessionResult);
        }

        public Task<bool> ResetSessionAsync(string sessionKey)
        {
            ResetSessionKeys.Add(sessionKey);
            return Task.FromResult(true);
        }

        public Task<SessionResetResult> ResetSessionDetailedAsync(string sessionKey)
        {
            ResetSessionKeys.Add(sessionKey);
            return Task.FromResult(ResetSessionResult);
        }

        public Task<bool> CompactSessionAsync(string sessionKey, int maxLines = 400)
        {
            CompactSessionKeys.Add(sessionKey);
            return Task.FromResult(true);
        }

        public Task<SessionCompactResult> CompactSessionDetailedAsync(string sessionKey)
        {
            ModelCompactSessionKeys.Add(sessionKey);
            return CompactSessionBehavior?.Invoke(sessionKey) ?? Task.FromResult(CompactSessionResult);
        }

        public Task RequestSessionsAsync()
        {
            RequestSessionsCallCount++;
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null)
            => SendChatMessageForRunAsync(message, sessionKey, sessionId, attachments);

        public async Task<ChatSendResult> SendChatMessageForRunAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null,
            string? idempotencyKey = null)
        {
            SentMessages.Add(message);
            SentSessionKeys.Add(sessionKey);
            SentSessionIds.Add(sessionId);
            SentAttachments.Add(attachments?.ToArray());
            SentIdempotencyKeys.Add(idempotencyKey);
            if (SendBehavior is not null)
                await SendBehavior(message, sessionKey, sessionId);

            return SendResults.Count > 0 ? SendResults.Dequeue() : new ChatSendResult();
        }

        public Task PatchSessionModelAsync(string sessionKey, string model)
        {
            PatchedModelKeys.Add(sessionKey);
            PatchedModels.Add(model);
            return PatchSessionModelBehavior?.Invoke(sessionKey, model) ?? Task.CompletedTask;
        }
        public List<string> PatchedModelKeys { get; } = new();
        public List<string> PatchedModels { get; } = new();
        public Task ClearSessionModelAsync(string sessionKey)
        {
            ClearedModelKeys.Add(sessionKey);
            return ClearSessionModelBehavior?.Invoke(sessionKey) ?? Task.CompletedTask;
        }
        public List<string> ClearedModelKeys { get; } = new();
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel)
        {
            PatchedThinkingLevels.Add((sessionKey, thinkingLevel));
            return PatchSessionThinkingLevelBehavior?.Invoke(sessionKey, thinkingLevel) ?? Task.CompletedTask;
        }
        public List<(string SessionKey, string ThinkingLevel)> PatchedThinkingLevels { get; } = new();
        public Task ClearSessionThinkingLevelAsync(string sessionKey)
        {
            ClearedThinkingLevelKeys.Add(sessionKey);
            return ClearSessionThinkingLevelBehavior?.Invoke(sessionKey) ?? Task.CompletedTask;
        }
        public List<string> ClearedThinkingLevelKeys { get; } = new();

        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey)
        {
            RequestedHistoryKeys.Add(sessionKey);
            return HistoryBehavior?.Invoke(sessionKey)
                ?? Task.FromResult(new ChatHistoryInfo { SessionKey = sessionKey ?? "" });
        }

        public Task SendChatAbortAsync(string runId, string? sessionKey = null)
        {
            AbortedRunIds.Add(runId);
            return AbortBehavior?.Invoke(runId) ?? Task.CompletedTask;
        }

        public List<(string Id, string Decision)> ResolvedApprovals { get; } = new();
        public Func<string, string, Task>? ResolveApprovalBehavior { get; set; }

        public Task ResolveExecApprovalAsync(string approvalId, string decision)
        {
            ResolvedApprovals.Add((approvalId, decision));
            return ResolveApprovalBehavior?.Invoke(approvalId, decision) ?? Task.CompletedTask;
        }

        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }

        public EventHandler<ConnectionStatus>? CaptureStatusChangedHandlers() => StatusChanged;
        public void RaiseStatus(ConnectionStatus s) { CurrentStatus = s; StatusChanged?.Invoke(this, s); }
        public void RaiseSessions(SessionInfo[] s) { Sessions = s; SessionsUpdated?.Invoke(this, s); }
        public void RaiseSessionCommandCompleted(SessionCommandResult result) => SessionCommandCompleted?.Invoke(this, result);
        public void RaiseChat(ChatMessageInfo m) => ChatMessageReceived?.Invoke(this, m);
        public void RaiseAgent(AgentEventInfo a) => AgentEventReceived?.Invoke(this, a);
        public void RaiseModels(ModelsListInfo m) { CurrentModels = m; ModelsListUpdated?.Invoke(this, m); }
        public void Dispose()
        {
            IsDisposed = true;
            DisposeCount++;
        }
    }

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots, List<ChatProviderNotification> notifications)
        CreateProvider(
            SessionInfo[]? initial = null,
            string? toolMetaCachePath = null,
            string? attachmentMetaCachePath = null,
            string? lastChatStatePath = null,
            TimeSpan? lastChatStateSaveDelay = null,
            Func<TimeSpan, CancellationToken, Func<Task>, Task>? historyRetryScheduler = null,
            Action? historyFailureReservedForTesting = null,
            Func<Func<Task>, Task>? deferredAbortScheduler = null,
            Action<Action>? post = null)
    {
        var bridge = new FakeBridge { Sessions = initial ?? Array.Empty<SessionInfo>() };
        var provider = toolMetaCachePath is null && attachmentMetaCachePath is null && lastChatStatePath is null &&
            lastChatStateSaveDelay is null && historyRetryScheduler is null && historyFailureReservedForTesting is null &&
            deferredAbortScheduler is null && post is null
            ? new OpenClawChatDataProvider(bridge)
            : new OpenClawChatDataProvider(
                bridge,
                post,
                toolMetaCacheFilePath: toolMetaCachePath ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tool-metadata.json"),
                attachmentMetaCacheFilePath: attachmentMetaCachePath,
                lastChatStateFilePath: lastChatStatePath,
                lastChatStateSaveDelay: lastChatStateSaveDelay,
                historyRetryScheduler: historyRetryScheduler,
                historyFailureReservedForTesting: historyFailureReservedForTesting,
                deferredAbortScheduler: deferredAbortScheduler);
        var snapshots = new List<ChatDataSnapshot>();
        var notifications = new List<ChatProviderNotification>();
        provider.Changed += (_, e) => snapshots.Add(e.Snapshot);
        provider.NotificationRequested += (_, e) => notifications.Add(e.Notification);
        return (bridge, provider, snapshots, notifications);
    }

    private static ChatStatePersistence GetStatePersistence(
        OpenClawChatDataProvider provider) =>
        Assert.IsType<ChatStatePersistence>(
            typeof(OpenClawChatDataProvider)
                .GetField(
                    "_persistence",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(provider));

    private static void InvokeHandleOpenedLifecycle(
        OpenClawChatDataProvider provider,
        string threadId,
        ChatOpenedLifecycleTransition opened,
        ChatRuntimeGeneration generation) =>
        typeof(OpenClawChatDataProvider)
            .GetMethod(
                "HandleOpenedLifecycle",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                provider,
                [threadId, opened, generation]);

    private static SessionInfo MainSession() =>
        new() { Key = "main", IsMain = true, DisplayName = "Main session", Status = "active" };

    private static AgentEventInfo MakeAgentEvent(string stream, string json, string sessionKey = "main", string? runId = null)
    {
        var doc = JsonDocument.Parse(json);
        return new AgentEventInfo
        {
            Stream = stream,
            Data = doc.RootElement.Clone(),
            SessionKey = sessionKey,
            RunId = runId ?? string.Empty
        };
    }

    [Theory]
    [InlineData("/new", "New")]
    [InlineData(" /RESET ", "Reset")]
    [InlineData("/Compact", "Compact")]
    public void LifecycleCommandParser_RecognizesOnlyExactCommands(
        string text,
        string expected)
    {
        Assert.True(ChatLifecycleCommandParser.TryParse(text, hasAttachments: false, out var command));
        Assert.Equal(expected, command.ToString());
    }

    [Fact]
    public void CompactionPresenter_WithTokenCounts_FormatsSavings()
    {
        var presentation = ChatCompactionPresenter.Create(42000, 12000);

        Assert.Equal("Context compacted", presentation.Title);
        Assert.Contains("42", presentation.Detail);
        Assert.Contains("12", presentation.Detail);
        Assert.Contains("30", presentation.Detail);
        Assert.Contains("saved", presentation.Detail);
    }

    [Fact]
    public void CompactionPresenter_WithoutTokenCounts_ExplainsCheckpoint()
    {
        var presentation = ChatCompactionPresenter.Create(null, null);

        Assert.Contains("checkpoint", presentation.Detail);
        Assert.Contains(presentation.Title, presentation.AutomationName);
    }

    [Fact]
    public void CompactionPresenter_CreatesPresentationForStructuredStatusEntry()
    {
        var entry = new ChatTimelineItem("compaction-1", ChatTimelineItemKind.Status, "Compacted");
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            [entry.Id] = new(
                Timestamp: null,
                Model: null,
                OpenClawKind: "compaction",
                CompactionTokensBefore: 42000,
                CompactionTokensAfter: 12000),
        };

        var presentation = ChatCompactionPresenter.TryCreateForEntry(entry, metadata);

        Assert.NotNull(presentation);
        Assert.Equal("COMPACTED HISTORY", presentation.Title);
        Assert.Equal(
            "The compacted transcript is preserved as a checkpoint. " +
            "Open session checkpoints to branch or restore from that compacted view.",
            presentation.Detail);
        Assert.Equal("Open checkpoints", presentation.ActionLabel);
        Assert.DoesNotContain("42", presentation.Detail);
        Assert.DoesNotContain(presentation.ActionLabel, presentation.AutomationName);
    }

    [Theory]
    [InlineData(ChatTimelineItemKind.Status, "status")]
    [InlineData(ChatTimelineItemKind.Assistant, "compaction")]
    public void CompactionPresenter_RejectsEntriesOutsideStructuredCompactionContract(
        ChatTimelineItemKind kind,
        string openClawKind)
    {
        var entry = new ChatTimelineItem("entry-1", kind, "Text");
        var metadata = new Dictionary<string, ChatEntryMetadata>
        {
            [entry.Id] = new(Timestamp: null, Model: null, OpenClawKind: openClawKind),
        };

        Assert.Null(ChatCompactionPresenter.TryCreateForEntry(entry, metadata));
    }

    [Theory]
    [InlineData("/new worktree", false)]
    [InlineData("/reset model", false)]
    [InlineData("/compact now", false)]
    [InlineData("/new", true)]
    public void LifecycleCommandParser_LeavesArgumentsAndAttachmentsForChatSend(
        string text,
        bool hasAttachments)
    {
        Assert.False(ChatLifecycleCommandParser.TryParse(text, hasAttachments, out _));
    }

    [Fact]
    public void LifecycleSelectionPolicy_PreservesPendingCreatedSession()
    {
        Assert.Equal(
            "agent:main:new-session",
            ChatLifecycleSelectionPolicy.RetainPendingForSelection(
                "agent:main:new-session",
                "agent:main:new-session"));
        Assert.Null(ChatLifecycleSelectionPolicy.RetainPendingForSelection(
            "agent:main:new-session",
            "agent:main:other-session"));
        Assert.False(ChatLifecycleSelectionPolicy.ShouldFallback(
            "agent:main:new-session",
            "agent:main:new-session",
            "main"));
        Assert.True(ChatLifecycleSelectionPolicy.ShouldFallback(
            "missing-session",
            pendingSelectedId: null,
            fallbackThreadId: "main"));
    }

    [Fact]
    public void LifecycleSelectionPolicy_PendingSurvivesStaleSnapshotWithoutSession()
    {
        // When /new creates a session and sets pendingSelectedId, a stale
        // snapshot (sessions.list response from before creation) should NOT
        // fall back to the default thread while the pending is active.
        Assert.False(ChatLifecycleSelectionPolicy.ShouldFallback(
            staleSelectedId: "agent:main:new-session",
            pendingSelectedId: "agent:main:new-session",
            fallbackThreadId: "main"));

        // RetainPendingForSelection returns the pending key when the
        // selected state matches, keeping it active until the real session
        // appears in the next snapshot.
        Assert.Equal(
            "agent:main:new-session",
            ChatLifecycleSelectionPolicy.RetainPendingForSelection(
                "agent:main:new-session",
                "agent:main:new-session"));
    }

    [Fact]
    public void AuthoritativeReload_PreservesMetadataLessAndPostRequestEntries()
    {
        var requestStartedAt = DateTimeOffset.UtcNow;

        Assert.True(OpenClawChatDataProvider.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            metadata: null,
            maxHistorySequence: 10,
            requestStartedAt));
        Assert.True(OpenClawChatDataProvider.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            new ChatEntryMetadata(requestStartedAt.AddMilliseconds(1), Model: null),
            maxHistorySequence: 10,
            requestStartedAt));
        Assert.False(OpenClawChatDataProvider.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            new ChatEntryMetadata(
                requestStartedAt.AddSeconds(-1),
                Model: null,
                OpenClawSeq: 10),
            maxHistorySequence: 10,
            requestStartedAt));
    }

    [Fact]
    public void AuthoritativeReload_PreservesNoSeqEntryEvenWithRemoteTimestampSkew()
    {
        var requestStartedAt = DateTimeOffset.UtcNow;

        // Entry with no seq and a remote timestamp before the local request start
        // (gateway clock behind local clock) must be preserved because history
        // coverage cannot be determined without a sequence number.
        Assert.True(OpenClawChatDataProvider.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            new ChatEntryMetadata(
                requestStartedAt.AddSeconds(-5),
                Model: null,
                OpenClawSeq: null),
            maxHistorySequence: 10,
            requestStartedAt));

        // Entry WITH seq at or below max AND remote timestamp before request
        // start is safely covered by history and should be dropped.
        Assert.False(OpenClawChatDataProvider.ShouldPreserveLiveEntryDuringAuthoritativeReload(
            new ChatEntryMetadata(
                requestStartedAt.AddSeconds(-5),
                Model: null,
                OpenClawSeq: 9),
            maxHistorySequence: 10,
            requestStartedAt));
    }

    [Fact]
    public async Task CheckpointRestoreReplacement_DropsArchivedEntriesFromLoadedTimeline()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        var historyCall = 0;
        bridge.HistoryBehavior = key =>
        {
            historyCall++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? "",
                Messages = historyCall == 1
                    ?
                    [
                        new ChatMessageInfo
                        {
                            SessionKey = key ?? "",
                            Role = "user",
                            Text = "Before checkpoint",
                            OpenClawSeq = 1,
                        },
                        new ChatMessageInfo
                        {
                            SessionKey = key ?? "",
                            Role = "assistant",
                            Text = "Archived after restore",
                            OpenClawSeq = 2,
                        },
                    ]
                    :
                    [
                        new ChatMessageInfo
                        {
                            SessionKey = key ?? "",
                            Role = "user",
                            Text = "Before checkpoint",
                            OpenClawSeq = 1,
                        },
                    ],
            });
        };

        await using (provider)
        {
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.LoadHistoryAsync("main", force: true);
            Assert.Contains(
                (await provider.LoadAsync()).Timelines["main"].Entries,
                entry => entry.Text == "Archived after restore");

            await provider.ReplaceHistoryAfterCheckpointRestoreAsync("main");

            var timeline = (await provider.LoadAsync()).Timelines["main"];
            Assert.Contains(timeline.Entries, entry => entry.Text == "Before checkpoint");
            Assert.DoesNotContain(timeline.Entries, entry => entry.Text == "Archived after restore");
            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);
        }
    }

    [Fact]
    public async Task CheckpointRestoreReplacement_InvalidatesInflightHistoryAndPreservesNewLiveEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        var staleHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var restoredHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = _ =>
            bridge.RequestedHistoryKeys.Count == 1
                ? staleHistory.Task
                : restoredHistory.Task;

        await using (provider)
        {
            bridge.RaiseStatus(ConnectionStatus.Connected);
            var staleLoad = provider.LoadHistoryAsync("main", force: true);

            await provider.ReplaceHistoryAfterCheckpointRestoreAsync("main");
            Assert.Single(bridge.RequestedHistoryKeys);

            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "user",
                Text = "New after restore",
                OpenClawSeq = 3,
            });
            staleHistory.SetResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "Before checkpoint",
                        OpenClawSeq = 1,
                    },
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "assistant",
                        Text = "Archived after restore",
                        OpenClawSeq = 2,
                    },
                ],
            });
            await staleLoad;
            await WaitForConditionAsync(() => bridge.RequestedHistoryKeys.Count == 2);

            restoredHistory.SetResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "Before checkpoint",
                        OpenClawSeq = 1,
                    },
                ],
            });
            await WaitForConditionAsync(() =>
                snapshots.Count > 0 &&
                snapshots[^1].Timelines["main"].HistoryLoaded);

            var timeline = snapshots[^1].Timelines["main"];
            Assert.Contains(timeline.Entries, entry => entry.Text == "Before checkpoint");
            Assert.Contains(timeline.Entries, entry => entry.Text == "New after restore");
            Assert.DoesNotContain(timeline.Entries, entry => entry.Text == "Archived after restore");
        }
    }

    [Fact]
    public async Task CheckpointRestoreReplacement_SuppressesStaleFailureNotificationAndRetry()
    {
        var posted = new Queue<Action>();
        Func<Task>? scheduledRetry = null;
        var (bridge, provider, _, notifications) = CreateProvider(
            [MainSession()],
            historyRetryScheduler: (_, _, retry) =>
            {
                scheduledRetry = retry;
                return Task.CompletedTask;
            },
            post: posted.Enqueue);
        bridge.HistoryBehavior = key =>
            bridge.RequestedHistoryKeys.Count == 1
                ? Task.FromException<ChatHistoryInfo>(new IOException("stale failure"))
                : Task.FromResult(new ChatHistoryInfo
                {
                    SessionKey = key ?? "",
                    Messages =
                    [
                        new ChatMessageInfo
                        {
                            SessionKey = key ?? "",
                            Role = "user",
                            Text = "Before checkpoint",
                            OpenClawSeq = 1,
                        },
                    ],
                });

        await using (provider)
        {
            bridge.RaiseStatus(ConnectionStatus.Connected);
            posted.Clear();

            await provider.LoadHistoryAsync("main", force: true);
            var staleNotification = Assert.Single(posted);
            Assert.NotNull(scheduledRetry);

            await provider.ReplaceHistoryAfterCheckpointRestoreAsync("main");
            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);

            staleNotification();
            Assert.Empty(notifications);

            await scheduledRetry!();
            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);
        }
    }

    [Fact]
    public async Task LifecycleCommandDispatcher_NewCreatesChildWithoutFallbackMutation()
    {
        var bridge = new FakeBridge();
        var dispatcher = new ChatLifecycleCommandDispatcher(bridge);

        var result = await dispatcher.ExecuteAsync("agent:main:main", ChatLifecycleCommandKind.New);

        Assert.True(result.Succeeded);
        Assert.Equal("agent:main:new-session", result.NewSessionKey);
        var request = Assert.Single(bridge.CreateSessionRequests);
        Assert.Equal("agent:main:main", request.ParentSessionKey);
        Assert.True(request.EmitCommandHooks);
        Assert.Equal(false, request.SucceedsParent);
    }

    [Fact]
    public void LifecycleCommandExecutionPolicy_OnlyQueuesCompact()
    {
        Assert.False(ChatLifecycleCommandExecutionPolicy.ShouldQueue(ChatLifecycleCommandKind.New));
        Assert.False(ChatLifecycleCommandExecutionPolicy.ShouldQueue(ChatLifecycleCommandKind.Reset));
        Assert.True(ChatLifecycleCommandExecutionPolicy.ShouldQueue(ChatLifecycleCommandKind.Compact));
    }

    [Fact]
    public async Task LifecycleCommandProvider_CompactRefreshesHistoryWithoutChatSend()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "user",
                Text = "Old context",
                State = "final",
                OpenClawSeq = 1
            });
            bridge.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? "",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = key ?? "",
                        Role = "system",
                        Text = "Context compacted",
                        OpenClawKind = "compaction",
                        OpenClawSeq = 2
                    }
                ]
            });

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Compact);

            Assert.True(result.Succeeded);
            Assert.Equal(["main"], bridge.ModelCompactSessionKeys);
            Assert.Empty(bridge.CompactSessionKeys);
            Assert.Equal(["main"], bridge.RequestedHistoryKeys);
            Assert.Empty(bridge.SentMessages);
            var timeline = (await provider.LoadAsync()).Timelines["main"];
            var compactedEntry = Assert.Single(timeline.Entries);
            Assert.Equal(ChatTimelineItemKind.Status, compactedEntry.Kind);
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_NewFailureReconcilesSessions()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        bridge.CreateSessionResult = new SessionCreateResult
        {
            Ok = false,
            Error = "creation timed out"
        };

        await using (provider)
        {
            await provider.LoadAsync();
            var countBefore = bridge.RequestSessionsCallCount;

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.New);

            Assert.False(result.Succeeded);
            Assert.Equal(countBefore + 1, bridge.RequestSessionsCallCount);
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_NewLeavesOriginalRunAndQueueUntouched()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
            await provider.SendMessageAsync("main", "second");

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.New);

            Assert.True(result.Succeeded);
            Assert.Equal("agent:main:new-session", result.NewSessionKey);
            Assert.Empty(bridge.AbortedRunIds);
            Assert.Collection(
                GetQueuedMessages(snapshots[^1], "main"),
                queued => Assert.Equal("second", queued.Text));

            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "first response",
                State = "final"
            });
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

            await WaitForConditionAsync(() => bridge.SentMessages.Count == 2);
            Assert.Equal(["first", "second"], bridge.SentMessages);
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_ResetImmediatelyClearsQueueAndAbortsActiveRun()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
            await provider.SendMessageAsync("main", "second");

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            Assert.True(result.Succeeded);
            Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
            Assert.Empty((await provider.LoadAsync()).Timelines["main"].Entries);
            await WaitForConditionAsync(() => bridge.AbortedRunIds.Contains("run-1"));
            Assert.Equal(["first"], bridge.SentMessages);
        }
    }

    [Fact]
    public async Task EnqueueCompactCommandAsync_WhenIdleStartsWithoutChatSend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        var compactCompletion = new TaskCompletionSource<SessionCompactResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.CompactSessionBehavior = _ => compactCompletion.Task;

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);

            Assert.True(await provider.EnqueueCompactCommandAsync("main"));

            await WaitForConditionAsync(() => bridge.ModelCompactSessionKeys.Count == 1);
            var queued = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
            Assert.Equal("/compact", queued.Text);
            Assert.Equal(ChatQueuedMessageSendState.Sending, queued.SendState);
            Assert.Empty(bridge.SentMessages);

            await provider.SendMessageAsync("main", "after compact");
            Assert.Empty(bridge.SentMessages);

            compactCompletion.SetResult(new SessionCompactResult
            {
                Ok = true,
                Key = "main",
                Compacted = true
            });
            await WaitForConditionAsync(() => bridge.SentMessages.Count == 1);
            Assert.Equal(["after compact"], bridge.SentMessages);
        }
    }

    [Fact]
    public async Task EnqueueCompactCommandAsync_WaitsBehindEarlierMessagesWithoutChatSend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        var compactCompletion = new TaskCompletionSource<SessionCompactResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.CompactSessionBehavior = _ => compactCompletion.Task;

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            snapshots.Clear();

            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
            await provider.SendMessageAsync("main", "second");
            Assert.True(await provider.EnqueueCompactCommandAsync("main"));
            await provider.SendMessageAsync("main", "third");

            Assert.Empty(bridge.ModelCompactSessionKeys);
            Assert.Collection(
                GetQueuedMessages(snapshots[^1], "main"),
                queued => Assert.Equal("second", queued.Text),
                queued => Assert.Equal("/compact", queued.Text),
                queued => Assert.Equal("third", queued.Text));

            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "first response",
                State = "final"
            });
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

            await WaitForConditionAsync(() => bridge.SentMessages.Count == 2);
            Assert.Empty(bridge.ModelCompactSessionKeys);

            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "second response",
                State = "final"
            });
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-2"));

            await WaitForConditionAsync(() => bridge.ModelCompactSessionKeys.Count == 1);
            Assert.Equal(["first", "second"], bridge.SentMessages);
            Assert.Collection(
                GetQueuedMessages(snapshots[^1], "main"),
                queued =>
                {
                    Assert.Equal("/compact", queued.Text);
                    Assert.Equal(ChatQueuedMessageSendState.Sending, queued.SendState);
                },
                queued => Assert.Equal("third", queued.Text));

            compactCompletion.SetResult(new SessionCompactResult
            {
                Ok = true,
                Key = "main",
                Compacted = true
            });

            await WaitForConditionAsync(() => bridge.SentMessages.Count == 3);
            await WaitForConditionAsync(() => GetQueuedMessages(snapshots[^1], "main").Count == 0);
            Assert.Equal(["first", "second", "third"], bridge.SentMessages);
            Assert.Equal(["main"], bridge.ModelCompactSessionKeys);
            Assert.DoesNotContain(
                snapshots[^1].Timelines["main"].Entries,
                entry => entry.Kind == ChatTimelineItemKind.User && entry.Text == "/compact");
        }
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_RemovesPendingCompactCommand()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
            await provider.EnqueueCompactCommandAsync("main");
            var compact = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));

            Assert.True(await provider.CancelQueuedMessageAsync("main", compact.Id));

            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "done",
                State = "final"
            });
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
            await Task.Delay(50);

            Assert.Empty(bridge.ModelCompactSessionKeys);
            Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        }
    }

    [Fact]
    public async Task EnqueueCompactCommandAsync_FailureDoesNotBlockLaterMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.CompactSessionResult = new SessionCompactResult
        {
            Ok = false,
            Key = "main",
            Error = "compaction unavailable"
        };

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
            await provider.EnqueueCompactCommandAsync("main");
            await provider.SendMessageAsync("main", "second");

            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "first response",
                State = "final"
            });
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

            await WaitForConditionAsync(() => bridge.SentMessages.Count == 2);
            Assert.Equal(["first", "second"], bridge.SentMessages);
            Assert.Contains(
                GetQueuedMessages(snapshots[^1], "main"),
                queued =>
                    queued.Text == "/compact" &&
                    queued.SendState == ChatQueuedMessageSendState.Failed &&
                    queued.ErrorText == "compaction unavailable");
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_ResetSupersedesInflightQueuedCompact()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        var compactCompletion = new TaskCompletionSource<SessionCompactResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.CompactSessionBehavior = _ => compactCompletion.Task;

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            bridge.RequestedHistoryKeys.Clear();
            await provider.EnqueueCompactCommandAsync("main");
            await WaitForConditionAsync(() => bridge.ModelCompactSessionKeys.Count == 1);

            var reset = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            Assert.True(reset.Succeeded);
            Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
            compactCompletion.SetResult(new SessionCompactResult
            {
                Ok = true,
                Key = "main",
                Compacted = true
            });
            await Task.Delay(50);

            Assert.Empty(bridge.RequestedHistoryKeys);
            Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        }
    }

    [Fact]
    public async Task CompactCompletion_QueuesAuthoritativeReloadBehindInflightHistory()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        var staleHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = key =>
            bridge.RequestedHistoryKeys.Count == 1
                ? staleHistory.Task
                : Task.FromResult(new ChatHistoryInfo
                {
                    SessionKey = key ?? "",
                    Messages =
                    [
                        new ChatMessageInfo
                        {
                            SessionKey = key ?? "",
                            Role = "system",
                            Text = "Context compacted",
                            OpenClawKind = "compaction",
                            OpenClawSeq = 2
                        }
                    ]
                });

        await using (provider)
        {
            var initialLoad = provider.LoadHistoryAsync("main", force: true);
            await provider.ExecuteLifecycleCommandAsync("main", ChatLifecycleCommandKind.Compact);
            Assert.Single(bridge.RequestedHistoryKeys);

            staleHistory.SetResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "Stale context",
                        OpenClawSeq = 1
                    }
                ]
            });
            await initialLoad;

            for (var attempt = 0; attempt < 20 && bridge.RequestedHistoryKeys.Count < 2; attempt++)
                await Task.Delay(10);

            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);
            var timeline = (await provider.LoadAsync()).Timelines["main"];
            var compactedEntry = Assert.Single(timeline.Entries);
            Assert.Equal(ChatTimelineItemKind.Status, compactedEntry.Kind);
        }
    }

    [Fact]
    public async Task CoalescedAuthoritativeReload_DoesNotCrossResetGeneration()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        var staleHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = _ => staleHistory.Task;

        await using (provider)
        {
            var initialLoad = provider.LoadHistoryAsync("main", force: true);
            await provider.LoadHistoryAsync(
                "main",
                force: true,
                authoritative: true);
            await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            staleHistory.SetResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "must stay cleared",
                    }
                ],
            });
            await initialLoad;
            await Task.Delay(50);

            Assert.Single(bridge.RequestedHistoryKeys);
            Assert.Empty((await provider.LoadAsync()).Timelines["main"].Entries);
        }
    }

    [Fact]
    public async Task CompactAuthoritativeReload_RetriesAfterTransientFailureWhenHistoryWasLoaded()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        var historyCall = 0;
        bridge.HistoryBehavior = key =>
        {
            historyCall++;
            if (historyCall == 2)
                return Task.FromException<ChatHistoryInfo>(new IOException("transient"));

            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? "",
                Messages =
                [
                    new ChatMessageInfo
                    {
                        SessionKey = key ?? "",
                        Role = historyCall == 1 ? "user" : "system",
                        Text = historyCall == 1 ? "Old context" : "Context compacted",
                        OpenClawKind = historyCall == 1 ? null : "compaction",
                        OpenClawSeq = historyCall
                    }
                ]
            });
        };

        await using (provider)
        {
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.LoadHistoryAsync("main", force: true);
            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Compact);

            Assert.True(result.Succeeded);
            for (var attempt = 0; attempt < 40 && bridge.RequestedHistoryKeys.Count < 3; attempt++)
                await Task.Delay(100);

            Assert.Equal(3, bridge.RequestedHistoryKeys.Count);
            var timeline = (await provider.LoadAsync()).Timelines["main"];
            var compactedEntry = Assert.Single(timeline.Entries);
            Assert.Equal(ChatTimelineItemKind.Status, compactedEntry.Kind);
        }
    }

    [Fact]
    public async Task StaleGenerationAuthoritativeReload_DoesNotReloadInNewGeneration()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        var staleHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var freshHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HistoryBehavior = key =>
        {
            return bridge.RequestedHistoryKeys.Count switch
            {
                1 => staleHistory.Task,
                2 => freshHistory.Task,
                _ => Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" }),
            };
        };

        await using (provider)
        {
            bridge.RaiseStatus(ConnectionStatus.Connected);
            var staleLoad = provider.LoadHistoryAsync("main", force: true);
            await provider.LoadHistoryAsync("main", force: true, authoritative: true);
            Assert.Single(bridge.RequestedHistoryKeys);

            bridge.RaiseStatus(ConnectionStatus.Disconnected);
            bridge.RaiseStatus(ConnectionStatus.Connected);
            staleHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
            await staleLoad;

            Assert.Single(bridge.RequestedHistoryKeys);

            var freshLoad = provider.LoadHistoryAsync(
                "main",
                force: true,
                authoritative: true);
            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);
            freshHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
            await freshLoad;

            Assert.Equal(2, bridge.RequestedHistoryKeys.Count);
        }
    }

    [Fact]
    public async Task LiveCompactionMessage_PreservesStructuredMetadata()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "system",
                Text = "Context compacted",
                State = "final",
                OpenClawKind = "compaction",
                CompactionTokensBefore = 42000,
                CompactionTokensAfter = 12000
            });

            var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
            var metadata = provider.GetEntryMetadata("main")[entry.Id];
            Assert.Equal("compaction", metadata.OpenClawKind);
            Assert.Equal(42000, metadata.CompactionTokensBefore);
            Assert.Equal(12000, metadata.CompactionTokensAfter);
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_NewRefreshesSessionsWithoutChatSend()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        await using (provider)
        {
            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.New);

            Assert.True(result.Succeeded);
            Assert.Equal("agent:main:new-session", result.NewSessionKey);
            Assert.Equal(1, bridge.RequestSessionsCallCount);
            Assert.Empty(bridge.SentMessages);
        }
        Assert.Empty(bridge.ResetSessionKeys);
        Assert.Empty(bridge.SentMessages);
    }

    [Fact]
    public async Task LifecycleCommandDispatcher_UnsupportedNewPreservesCurrentSession()
    {
        var bridge = new FakeBridge
        {
            CreateSessionResult = new SessionCreateResult
            {
                Ok = false,
                IsSupported = false,
                Error = "unknown method"
            }
        };
        var dispatcher = new ChatLifecycleCommandDispatcher(bridge);

        var result = await dispatcher.ExecuteAsync("main", ChatLifecycleCommandKind.New);

        Assert.False(result.Succeeded);
        Assert.Null(result.NewSessionKey);
        Assert.Contains("does not support", result.Error);
        Assert.Empty(bridge.ResetSessionKeys);
        Assert.Empty(bridge.SentMessages);
    }

    [Fact]
    public async Task LifecycleCommandDispatcher_NewRejectsCurrentSessionKey()
    {
        var bridge = new FakeBridge
        {
            CreateSessionResult = new SessionCreateResult
            {
                Ok = true,
                Key = " main "
            }
        };
        var dispatcher = new ChatLifecycleCommandDispatcher(bridge);

        var result = await dispatcher.ExecuteAsync("main", ChatLifecycleCommandKind.New);

        Assert.False(result.Succeeded);
        Assert.Null(result.NewSessionKey);
        Assert.Contains("current session", result.Error);
        Assert.Empty(bridge.ResetSessionKeys);
        Assert.Empty(bridge.SentMessages);
    }

    [Fact]
    public async Task LifecycleCommandDispatcher_ResetAndCompactUseDirectRpcs()
    {
        var bridge = new FakeBridge();
        var dispatcher = new ChatLifecycleCommandDispatcher(bridge);

        var reset = await dispatcher.ExecuteAsync("main", ChatLifecycleCommandKind.Reset);
        var compact = await dispatcher.ExecuteAsync("main", ChatLifecycleCommandKind.Compact);

        Assert.True(reset.Succeeded);
        Assert.True(compact.Succeeded);
        Assert.Equal(["main"], bridge.ResetSessionKeys);
        Assert.Equal(["main"], bridge.ModelCompactSessionKeys);
        Assert.Empty(bridge.CompactSessionKeys);
        Assert.Empty(bridge.SentMessages);
    }

    [Fact]
    public async Task LifecycleCommandProvider_ResetFailurePreservesTranscriptAndSurfacesError()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);
        bridge.ResetSessionResult = new SessionResetResult
        {
            Ok = false,
            Key = "main",
            Reason = "active run",
            Error = "active run"
        };

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "user",
                Text = "Keep me",
                State = "final"
            });

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            Assert.False(result.Succeeded);
            Assert.Equal(["main"], bridge.ResetSessionKeys);
            var entries = (await provider.LoadAsync()).Timelines["main"].Entries;
            Assert.Contains(entries, entry => entry.Text == "Keep me");
            Assert.Contains(entries, entry =>
                entry.Kind == ChatTimelineItemKind.Status &&
                entry.Text.Contains("active run", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LifecycleCommandProvider_ResetSuccessClearsTranscriptAfterResponse()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "user",
                Text = "Clear me",
                State = "final"
            });

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            Assert.True(result.Succeeded);
            Assert.Empty((await provider.LoadAsync()).Timelines["main"].Entries);
        }
    }

    [Fact]
    public async Task Telemetry_LocalSendAndLifecycle_EmitCorrelatedAllowlistedSpans()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "private-run", Status = "started" });

        await provider.SendMessageAsync("main", "private prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "private-run"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "private response",
            State = "delta",
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "private-run"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "private response",
            State = "final",
        });

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        var send = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.SendSpanName);
        var wait = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseWaitSpanName);
        var receive = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseReceiveSpanName);
        Assert.Equal(turn.TraceId, send.TraceId);
        Assert.Equal(turn.SpanId, send.ParentSpanId);
        Assert.Equal(turn.TraceId, wait.TraceId);
        Assert.Equal(turn.SpanId, wait.ParentSpanId);
        Assert.Equal(turn.TraceId, receive.TraceId);
        Assert.Equal(turn.SpanId, receive.ParentSpanId);
        Assert.Equal("local", turn.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName()));
        Assert.Equal("success", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("lifecycle_end", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        Assert.Equal("accepted", send.GetTagItem(ChatTelemetryTracker.AdmissionStatusTag));
        Assert.Equal("assistant", wait.GetTagItem(ChatTelemetryTracker.FirstOutputKindTag));
        Assert.Equal("assistant", receive.GetTagItem(ChatTelemetryTracker.FirstOutputKindTag));
        Assert.DoesNotContain(turn.Tags, tag => tag.Value?.Contains("private", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(send.Tags, tag => tag.Value?.Contains("private", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(wait.Tags, tag => tag.Value?.Contains("private", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(receive.Tags, tag => tag.Value?.Contains("private", StringComparison.Ordinal) == true);

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_UnknownAdmissionStatus_MapsToOther()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "future_status" });

        await provider.SendMessageAsync("main", "prompt");

        var send = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.SendSpanName);
        Assert.Equal("other", send.GetTagItem(ChatTelemetryTracker.AdmissionStatusTag));
        await provider.DisposeAsync();
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseWaitSpanName);
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseReceiveSpanName);
    }

    [Fact]
    public async Task Telemetry_RemoteLifecycle_EmitsRemoteTurn()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-run"));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"response"}""", runId: "remote-run"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "remote-run"));

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("remote", turn.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName()));
        Assert.Equal("lifecycle_end", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseWaitSpanName);
        Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.ResponseReceiveSpanName);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_LoadHistory_EmitsBoundedHistorySpan()
    {
        using var activities = new ChatActivityCollector();
        var (_, provider, _, _) = CreateProvider(new[] { MainSession() });

        await provider.LoadHistoryAsync("main", force: true);

        var history = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("forced", history.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName()));
        Assert.Equal("success", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal(
            ["openclaw.outcome", "openclaw.source"],
            history.Tags.Select(tag => tag.Key).Order().ToArray());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_AbortIntent_CompletesTurnAsCanceled()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        await provider.StopResponseAsync("main");

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("canceled", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("abort_requested", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_AbortBeforeLifecycleStart_DoesNotCreateRemoteTurn()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "started" });

        await provider.SendMessageAsync("main", "prompt");
        await provider.StopResponseAsync("main");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run"));

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("local", turn.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName()));
        Assert.Equal("canceled", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("abort_requested", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_Disconnect_CompletesOutstandingTurn()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("canceled", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("disconnected", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_UncorrelatedTerminalEvents_AreDiagnosedWithoutGuessing()
    {
        using var activities = new ChatActivityCollector();
        using var metrics = new ChatMetricCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        var snapshotsBeforeMismatchedTerminal = snapshots.Count;
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "different-run"));

        Assert.Equal(snapshotsBeforeMismatchedTerminal, snapshots.Count);
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal(
            ["mismatched_run_id"],
            metrics.TagsFor(
                ChatTelemetryTracker.DroppedTerminalEventsMetricName,
                ChatTelemetryTracker.DroppedTerminalEventReasonTag));

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run"));
        Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_LifecycleTerminalWithoutRunId_PreservesActiveRunUntilExactTerminal()
    {
        using var activities = new ChatActivityCollector();
        using var metrics = new ChatMetricCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "next-run", Status = "started" });
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        await provider.SendMessageAsync("main", "queued");
        var snapshotsBeforeMalformedTerminal = snapshots.Count;
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: null));

        Assert.Equal(snapshotsBeforeMalformedTerminal, snapshots.Count);
        Assert.Equal(["prompt"], bridge.SentMessages);
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal(
            ["missing_run_id"],
            metrics.TagsFor(
                ChatTelemetryTracker.DroppedTerminalEventsMetricName,
                ChatTelemetryTracker.DroppedTerminalEventReasonTag));

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run"));
        Assert.True(SpinWait.SpinUntil(
            () => bridge.SentMessages.Count == 2,
            TimeSpan.FromSeconds(5)));
        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("success", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("lifecycle_end", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        Assert.Equal(["prompt", "queued"], bridge.SentMessages);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_LegacyJobTerminalWithoutRunId_PreservesActiveRunUntilExactTerminal()
    {
        using var activities = new ChatActivityCollector();
        using var metrics = new ChatMetricCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        var snapshotsBeforeMalformedTerminal = snapshots.Count;
        bridge.RaiseAgent(MakeAgentEvent("job", """{"state":"done"}""", runId: null));

        Assert.Equal(snapshotsBeforeMalformedTerminal, snapshots.Count);
        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal(
            ["missing_run_id"],
            metrics.TagsFor(
                ChatTelemetryTracker.DroppedTerminalEventsMetricName,
                ChatTelemetryTracker.DroppedTerminalEventReasonTag));

        bridge.RaiseAgent(MakeAgentEvent("job", """{"state":"done"}""", runId: "run"));
        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("success", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("lifecycle_end", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_LifecycleTerminalWithoutRunId_RemainsEligibleForDisconnectCleanup()
    {
        using var activities = new ChatActivityCollector();
        using var metrics = new ChatMetricCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: null));

        Assert.DoesNotContain(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        var turn = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        Assert.Equal("canceled", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("disconnected", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        Assert.Equal(
            ["missing_run_id"],
            metrics.TagsFor(
                ChatTelemetryTracker.DroppedTerminalEventsMetricName,
                ChatTelemetryTracker.DroppedTerminalEventReasonTag));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Telemetry_Reset_CompletesQueuedAndActiveTurns()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        await provider.SendMessageAsync("main", "queued");
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main",
        });

        var turns = activities.Stopped
            .Where(activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName)
            .ToArray();
        Assert.Equal(2, turns.Length);
        Assert.All(turns, turn =>
        {
            Assert.Equal("canceled", turn.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
            Assert.Equal("reset", turn.GetTagItem(OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        });
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_ReturnsSeededSessionsAsThreads()
    {
        var (_, provider, _, _) = CreateProvider(new[] { MainSession() });

        var snapshot = await provider.LoadAsync();

        Assert.Single(snapshot.Threads);
        Assert.Equal("main", snapshot.Threads[0].Id);
        Assert.Equal("Main session", snapshot.Threads[0].Title);
        Assert.Equal("main", snapshot.DefaultThreadId);
        Assert.True(snapshot.Timelines.ContainsKey("main"));
    }

    [Fact]
    public async Task LoadAsync_MapsRunLivenessWithoutEndingReusableThreads()
    {
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "done",
                DisplayName = "Done",
                Status = "done",
                HasActiveRun = false,
                CurrentActivity = "stale run detail",
            },
            new SessionInfo { Key = "killed", DisplayName = "Killed", Status = "killed" },
            new SessionInfo
            {
                Key = "aborted",
                DisplayName = "Aborted",
                Status = "done",
                AbortedLastRun = true,
            },
            new SessionInfo { Key = "unknown", DisplayName = "Unknown", Status = "unknown" },
        };
        var (_, provider, _, _) = CreateProvider(sessions);

        var snapshot = await provider.LoadAsync();

        Assert.Equal(
            ChatThreadStatus.Created,
            Assert.Single(snapshot.Threads, thread => thread.Id == "done").Status);
        Assert.Equal(
            ChatThreadStatus.Created,
            Assert.Single(snapshot.Threads, thread => thread.Id == "killed").Status);
        Assert.Equal(
            ChatThreadStatus.Created,
            Assert.Single(snapshot.Threads, thread => thread.Id == "aborted").Status);
        Assert.Equal(
            ChatThreadStatus.Created,
            Assert.Single(snapshot.Threads, thread => thread.Id == "unknown").Status);
        Assert.Equal(
            ChatActivity.Idle,
            Assert.Single(snapshot.Threads, thread => thread.Id == "done").Activity);
    }

    [Fact]
    public async Task LoadAsync_DistinguishesDuplicateMultiSegmentSessionTitles()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:subagent:uuid-b", DisplayName = "Research" },
            new SessionInfo { Key = "agent:main:subagent:uuid-a", DisplayName = "Research" },
        };
        var (_, provider, _, _) = CreateProvider(sessions);

        var snapshot = await provider.LoadAsync();

        Assert.Equal(2, snapshot.Threads.Select(thread => thread.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("agent:main:subagent:uuid-b", snapshot.Threads[0].Id);
        Assert.Equal("agent:main:subagent:uuid-a", snapshot.Threads[1].Id);
        Assert.All(snapshot.Threads, thread => Assert.True(thread.IsBackground));
        Assert.All(snapshot.Threads, thread => Assert.Equal("main", thread.AgentId));
    }

    [Fact]
    public async Task LoadAsync_PreservesRawKeyAsIdWithFlattenedClassification()
    {
        // Gateway keys must round-trip untouched: the resolver only derives display fields.
        var rawKey = "agent:main:tui-847241c7-3f9a-4a2b-b123-abcdef123456";
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = rawKey,
                Classification = "tui",
                AgentId = "main",
            },
        };
        var (_, provider, _, _) = CreateProvider(sessions);
        var snapshot = await provider.LoadAsync();

        Assert.Equal(rawKey, snapshot.Threads[0].Id);
        Assert.Equal("Terminal session", snapshot.Threads[0].Title);
    }

    [Fact]
    public async Task LoadAsync_RunLivenessAndBackgroundFiltering_ComposesCorrectly()
    {
        // Verifies the full filtering pipeline: only live runs get Status=Running,
        // ready sessions stay selectable, and background sessions get IsBackground=true.
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:main", IsMain = true, Status = "active" },
            new SessionInfo { Key = "agent:main:cron:daily", Status = "completed" },
            new SessionInfo { Key = "agent:main:explicit:task", Status = "done" },
            new SessionInfo { Key = "agent:main:hook:pr-check", Status = "active" },
        };
        var (_, provider, _, _) = CreateProvider(sessions);
        var snapshot = await provider.LoadAsync();

        var main = Assert.Single(snapshot.Threads, t => t.Id == "agent:main:main");
        Assert.Equal(ChatThreadStatus.Running, main.Status);
        Assert.False(main.IsBackground);

        var cron = Assert.Single(snapshot.Threads, t => t.Id == "agent:main:cron:daily");
        Assert.Equal(ChatThreadStatus.Created, cron.Status);
        Assert.True(cron.IsBackground);

        var task = Assert.Single(snapshot.Threads, t => t.Id == "agent:main:explicit:task");
        Assert.Equal(ChatThreadStatus.Created, task.Status);
        Assert.False(task.IsBackground);

        var hook = Assert.Single(snapshot.Threads, t => t.Id == "agent:main:hook:pr-check");
        Assert.Equal(ChatThreadStatus.Running, hook.Status);
        Assert.True(hook.IsBackground);
    }

    [Fact]
    public async Task LoadAsync_FlatAgentId_OverridesKeyParsing()
    {
        // A Gateway-provided agent id takes precedence over key parsing.
        var sessions = new[]
        {
            new SessionInfo
            {
                Key = "agent:main:explicit:work",
                Classification = "explicit",
                AgentId = "custom-agent",
            },
        };
        var (_, provider, _, _) = CreateProvider(sessions);
        var snapshot = await provider.LoadAsync();

        Assert.Equal("custom-agent", snapshot.Threads[0].AgentId);
    }

    [Fact]
    public async Task LoadAsync_CarriesSessionModelProviderToThreads()
    {
        var session = MainSession();
        session.Model = "gpt-5.4";
        session.Provider = "openrouter";
        var (_, provider, _, _) = CreateProvider(new[] { session });

        var snapshot = await provider.LoadAsync();

        Assert.Equal("gpt-5.4", snapshot.Threads[0].Model);
        Assert.Equal("openrouter", snapshot.Threads[0].ModelProvider);
    }

    [Fact]
    public async Task SendMessageAsync_WhenIdle_RendersTranscriptEntryBeforeAwaitingGateway()
    {
        var tcs = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => tcs.Task;
        await provider.LoadAsync();
        snapshots.Clear();

        var sendTask = provider.SendMessageAsync("main", "Hello");

        // Snapshot must be emitted before SendChatMessageAsync completes.
        Assert.Single(snapshots);
        var timeline = snapshots[0].Timelines["main"];
        Assert.True(timeline.TurnActive);
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
        Assert.Equal("Hello", entry.Text);
        Assert.Empty(GetQueuedMessages(snapshots[0], "main"));
        Assert.Single(bridge.SentMessages);
        Assert.Equal("Hello", bridge.SentMessages[0]);
        Assert.Equal("main", bridge.SentSessionKeys[0]);

        tcs.SetResult();
        await sendTask;
    }

    [Fact]
    public async Task SendMessageAsync_WhenIdle_DoesNotRenderQueueCardBeforeLifecycleStart()
    {
        var tcs = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => tcs.Task;
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        await provider.LoadAsync();
        snapshots.Clear();

        var sendTask = provider.SendMessageAsync("main", "Hello queue");

        Assert.Empty(GetQueuedMessages(snapshots[0], "main"));
        var pending = Assert.Single(snapshots[0].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.User, pending.Kind);
        Assert.Equal("Hello queue", pending.Text);

        tcs.SetResult();
        await sendTask;

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello queue");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
        Assert.Equal("Hello queue", entry.Text);
        AssertNoQueuedTranscriptDuplicate(snapshots, "main", "Hello queue");
    }

    [Fact]
    public async Task SendMessageAsync_AckDoesNotQueueStaleSnapshotThatCanResurrectClearedQueuedMessage()
    {
        var bridge = new FakeBridge { Sessions = new[] { MainSession() } };
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        var posted = new List<Action>();
        var provider = new OpenClawChatDataProvider(bridge, post: posted.Add);
        var snapshots = new List<ChatDataSnapshot>();
        provider.Changed += (_, e) => snapshots.Add(e.Snapshot);
        await provider.LoadAsync();
        posted.Clear();

        await provider.SendMessageAsync("main", "queued prompt");
        Assert.Single(posted);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));

        foreach (var action in posted)
            action();

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "queued prompt");
    }

    [Fact]
    public async Task SendMessageAsync_DuringActiveTurn_QueuesFollowUpsLocallyUntilTurnEnds()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        Assert.Equal(new[] { "first" }, bridge.SentMessages);
        Assert.Collection(
            GetQueuedMessages(snapshots[^1], "main"),
            queued =>
            {
                Assert.Equal("second", queued.Text);
                Assert.Equal(ChatQueuedMessageSendState.Queued, queued.SendState);
            });
        Assert.DoesNotContain(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task AgentEvent_DuplicateTerminalForCompletedRun_DoesNotDispatchAdditionalQueuedMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "b");
        await provider.SendMessageAsync("main", "c");

        Assert.Equal(new[] { "a" }, bridge.SentMessages);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);

        bridge.RaiseAgent(MakeAgentEvent("job", """{"state":"done"}""", runId: "run-1"));
        await Task.Delay(150);

        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);
        Assert.Collection(
            GetQueuedMessages(snapshots[^1], "main"),
            queued =>
            {
                Assert.Equal("c", queued.Text);
                Assert.Equal(ChatQueuedMessageSendState.Queued, queued.SendState);
            });
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "b");
    }

    [Fact]
    public async Task LifecycleStart_DoesNotClearNextQueuedMessageWhenEarlierEchoAlreadyClearedItsCard()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first queued");
        await provider.SendMessageAsync("main", "second queued");
        Assert.Equal(new[] { "first queued" }, bridge.SentMessages);

        var queuedBeforeEcho = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("second queued", queuedBeforeEcho.Text);
        Assert.Equal(ChatQueuedMessageSendState.Queued, queuedBeforeEcho.SendState);
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "first queued");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "first queued",
            State = "final"
        });

        var queuedAfterEcho = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("second queued", queuedAfterEcho.Text);
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "first queued");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));

        var queuedAfterFirstStart = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("second queued", queuedAfterFirstStart.Text);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "first queued", "second queued" }, bridge.SentMessages);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second queued");
        AssertNoQueuedTranscriptDuplicate(snapshots, "main", "first queued");
        AssertNoQueuedTranscriptDuplicate(snapshots, "main", "second queued");
    }

    [Fact]
    public async Task AssistantFrames_DuringActiveLocalRun_DoNotPromoteLaterQueuedMessages()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        await provider.SendMessageAsync("main", "b");
        await provider.SendMessageAsync("main", "c");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Sounds good — send them through.",
            State = "final"
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Sounds good — send them through.",
            State = "final"
        });

        var latest = snapshots[^1];
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "a");
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text is "b" or "c");
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "Sounds good — send them through.");
        Assert.Collection(
            GetQueuedMessages(latest, "main"),
            queued => Assert.Equal("b", queued.Text),
            queued => Assert.Equal("c", queued.Text));
    }

    [Fact]
    public async Task ChatSendAck_ForAlreadyActiveRun_DoesNotPromoteLaterQueuedMessages()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        await provider.SendMessageAsync("main", "r");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "r");

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        await provider.SendMessageAsync("main", "s");
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        await provider.SendMessageAsync("main", "t");

        var latest = snapshots[^1];
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text is "s" or "t");
        Assert.Collection(
            GetQueuedMessages(latest, "main"),
            queued => Assert.Equal("s", queued.Text),
            queued => Assert.Equal("t", queued.Text));
    }

    [Fact]
    public async Task LifecycleEnd_KeepsLocalInitiatedStateWhenQueuedMessagesRemain()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first");
        await provider.SendMessageAsync("main", "second");
        Assert.Equal(new[] { "first" }, bridge.SentMessages);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first run response",
            State = "final"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first run response",
            State = "final"
        });

        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "first run response");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));

        var latest = snapshots[^1];
        Assert.Empty(GetQueuedMessages(latest, "main"));
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "first");
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task SendMessageAsync_GatewayThrows_AppendsErrorAndRethrows()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => throw new InvalidOperationException("boom");
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "Hi"));

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "Hi");
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("boom"));
        Assert.False(timeline.TurnActive);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);
    }

    [Fact]
    public async Task SendMessageAsync_TerminalAckStatus_AppendsErrorAndKeepsFailedQueuedMessage()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "model unavailable" });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "Hi"));

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "Hi");
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("model unavailable"));
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);
    }

    [Fact]
    public async Task SendMessageAsync_FailedFirstSendDoesNotOrphanLaterQueuedSend()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendCount++;
            return sendCount == 1 ? firstGate.Task : secondGate.Task;
        };
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "first failed" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var firstSend = provider.SendMessageAsync("main", "a");
        var secondSend = provider.SendMessageAsync("main", "b");
        firstGate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstSend);

        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        secondGate.SetResult();
        await secondSend;
        for (var i = 0; i < 20 && bridge.SendResults.Count > 0; i++)
            await Task.Delay(10);
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-b"));

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "a");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "b");
        Assert.Empty(GetQueuedMessages(latest, "main"));
    }

    [Fact]
    public async Task LifecycleEnd_IgnoresFailedQueuedMessagesWhenClearingLocalInitiatedState()
    {
        var historyCalls = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "second failed" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        await provider.SendMessageAsync("main", "b");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-a"));

        for (var i = 0; i < 20; i++)
        {
            if (GetQueuedMessages(snapshots[^1], "main").Any(q =>
                q.Text == "b" &&
                q.SendState == ChatQueuedMessageSendState.Failed))
            {
                break;
            }
            await Task.Delay(10);
        }
        var failed = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("b", failed.Text);
        Assert.Equal(ChatQueuedMessageSendState.Failed, failed.SendState);

        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "remote after failed card",
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        OpenClawSeq = 30
                    }
                }
            });
        };
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-run"));

        for (var i = 0; i < 20 && historyCalls == 0; i++)
            await Task.Delay(10);

        Assert.True(historyCalls > 0);
    }

    [Fact]
    public async Task LifecycleEnd_IgnoresFailedQueuedMessagesAfterLaterAcceptedSend()
    {
        var historyCalls = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "second failed" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-next", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        await provider.SendMessageAsync("main", "failed card");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-a"));
        for (var i = 0; i < 20; i++)
        {
            if (GetQueuedMessages(snapshots[^1], "main").Any(q => q.SendState == ChatQueuedMessageSendState.Failed))
                break;
            await Task.Delay(10);
        }

        await provider.SendMessageAsync("main", "next local");
        Assert.Contains(GetQueuedMessages(snapshots[^1], "main"), q =>
            q.Text == "failed card" &&
            q.SendState == ChatQueuedMessageSendState.Failed);
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "next local");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-next"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-next"));
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo
                    {
                        SessionKey = "main",
                        Role = "user",
                        Text = "remote after completed local",
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        OpenClawSeq = 31
                    }
                }
            });
        };
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-run"));

        for (var i = 0; i < 20 && historyCalls == 0; i++)
            await Task.Delay(10);

        Assert.True(historyCalls > 0);
    }

    [Fact]
    public async Task Status_ReconnectClearsUncorrelatedQueuedMessagesBeforeHistoryReload()
    {
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        var sendTask = provider.SendMessageAsync("main", "active across reconnect");
        await provider.SendMessageAsync("main", "pending across reconnect");
        Assert.Equal("pending across reconnect", Assert.Single(GetQueuedMessages(snapshots[^1], "main")).Text);

        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        sendGate.SetResult();
        await sendTask;
    }

    [Fact]
    public async Task Status_ReconnectAfterActiveRun_DoesNotStrandFutureSendBehindStaleRunId()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        Assert.Equal(new[] { "a" }, bridge.SentMessages);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "b");

        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "b");
    }

    [Fact]
    public async Task SendMessageAsync_RejectsEmptyMessage()
    {
        var (_, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SendMessageAsync("main", "  "));
    }

    [Fact]
    public async Task ChatMessageReceived_FinalAssistant_AppendsAssistantEntry()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello from assistant",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "Hello from assistant");
        Assert.False(timeline.TurnActive);
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantNoReply_IsSuppressed()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "no_reply",
            State = "final"
        });

        var timeline = (await provider.LoadAsync()).Timelines["main"];
        Assert.Empty(snapshots);
        Assert.Empty(timeline.Entries);
        Assert.DoesNotContain(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_DeltaAssistant_AppendsAssistantWithoutEndingTurn()
    {
        // Block-streamed deltas carry cumulative assistant text and should
        // upsert the active assistant entry without ending the turn.
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello",
            State = "delta"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "Hello");
        Assert.True(timeline.TurnActive);
        Assert.DoesNotContain(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_FinalAfterLifecycleEnd_DoesNotDuplicateAssistant()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "partial",
            State = "delta"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final"
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final"
        });

        var timeline = (await provider.LoadAsync()).Timelines["main"];
        var assistant = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("final", assistant.Text);
        Assert.False(assistant.IsStreaming);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task ChatMessageReceived_DeltaAfterFinalAssistant_DoesNotReactivateTurn()
    {
        var (bridge, provider, _, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final answer",
            State = "final"
        });
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "late trailing frame",
            State = "delta"
        });

        var timeline = (await provider.LoadAsync()).Timelines["main"];
        var assistant = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("final answer", assistant.Text);
        Assert.False(assistant.IsStreaming);
        Assert.False(timeline.TurnActive);
        Assert.DoesNotContain(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_DeltaAfterFinalAssistantBeforeLifecycleEnd_DoesNotReactivateTurn()
    {
        var (bridge, provider, _, notifications) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final answer",
            State = "final"
        });
        notifications.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "late trailing frame",
            State = "delta"
        });

        var timeline = (await provider.LoadAsync()).Timelines["main"];
        var assistant = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("final answer", assistant.Text);
        Assert.False(assistant.IsStreaming);
        Assert.False(timeline.TurnActive);
        Assert.DoesNotContain(notifications, n => n.Kind == ChatProviderNotificationKind.TurnComplete);
    }

    [Fact]
    public async Task ChatMessageReceived_UserEcho_AttachesGatewayIdentityToExactLocalRow()
    {
        // After sending a message locally, the SSE echo of that same text
        // should be suppressed as a gateway echo while atomically moving the
        // queued card into the transcript.
        var tcs = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => tcs.Task;
        await provider.LoadAsync();

        _ = provider.SendMessageAsync("main", "hi");
        snapshots.Clear(); // clear the snapshot from SendMessageAsync

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "hi",
            State = "final",
            OpenClawId = "gateway-hi",
            OpenClawSeq = 7,
        });

        var timeline = Assert.Single(snapshots).Timelines["main"];
        Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "hi");
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        var user = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User);
        var meta = provider.GetEntryMetadata("main")[user.Id];
        Assert.Equal("gateway-hi", meta.GatewayMessageId);
        Assert.Equal(7, meta.OpenClawSeq);
        Assert.False(meta.IsLocalQueuedSend);
        Assert.NotNull(meta.LocalQueuedMessageId);

        tcs.SetResult();
    }

    [Fact]
    public async Task ChatMessageReceived_UserEcho_ReconcilesExistingQueuedPromotionAfterPendingEchoExpires()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-d", Status = "started" });
        await provider.SendMessageAsync("main", "d");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-d"));

        var promoted = Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "d");
        var beforeMeta = provider.GetEntryMetadata("main");
        Assert.True(beforeMeta[promoted.Id].IsLocalQueuedSend);
        Assert.Null(beforeMeta[promoted.Id].OpenClawSeq);
        snapshots.Clear();

        // A non-identified local echo can consume the pending echo queue after
        // lifecycle.start has already promoted the queued bubble. The later
        // gateway-confirmed row still needs to reconcile onto that bubble.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "d",
            State = "final"
        });
        Assert.Empty(snapshots);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "d",
            State = "final",
            Ts = DateTimeOffset.UtcNow.AddSeconds(-7).ToUnixTimeMilliseconds(),
            OpenClawId = "f3ed25d3",
            OpenClawSeq = 9
        });

        var timeline = snapshots[^1].Timelines["main"];
        var user = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "d");
        var afterMeta = provider.GetEntryMetadata("main");
        Assert.Equal("f3ed25d3", afterMeta[user.Id].GatewayMessageId);
        Assert.Equal(9, afterMeta[user.Id].OpenClawSeq);
        Assert.False(afterMeta[user.Id].IsLocalQueuedSend);
    }

    [Fact]
    public async Task ChatMessageReceived_UserEcho_DoesNotReconcileStaleIdenticalConfirmedRemoteMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-same", Status = "started" });
        await provider.SendMessageAsync("main", "same");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-same"));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same",
            State = "final"
        });
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same",
            State = "final",
            Ts = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            OpenClawId = "remote-later",
            OpenClawSeq = 50
        });

        var users = snapshots[^1].Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.User && e.Text == "same")
            .ToArray();
        Assert.Equal(2, users.Length);

        var afterMeta = provider.GetEntryMetadata("main");
        Assert.Contains(users, user =>
            afterMeta[user.Id].IsLocalQueuedSend &&
            afterMeta[user.Id].OpenClawSeq is null);
        Assert.Contains(users, user =>
            afterMeta[user.Id].GatewayMessageId == "remote-later" &&
            afterMeta[user.Id].OpenClawSeq == 50 &&
            !afterMeta[user.Id].IsLocalQueuedSend);
    }

    [Fact]
    public async Task ChatMessageReceived_NoEnvelopeEchoDoesNotChooseBetweenPlainAndMediaPromotions()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-media", Status = "started" });
        await provider.SendMessageAsync("main", "same", default,
        [
            new ChatAttachment
            {
               Type = "image",
               MimeType = "image/png",
               FileName = "same.png",
               Content = Convert.ToBase64String([1]),
            },
        ]);
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-media"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-media"));

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-plain", Status = "started" });
        await provider.SendMessageAsync("main", "same");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-plain"));
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same",
            State = "final",
            OpenClawId = "remote-same",
            OpenClawSeq = 99,
        });

        var users = snapshots[^1].Timelines["main"].Entries
            .Where(entry => entry.Kind == ChatTimelineItemKind.User && entry.Text == "same")
            .ToArray();
        Assert.Equal(3, users.Length);
        var metadata = provider.GetEntryMetadata("main");
        Assert.Equal(2, users.Count(user => metadata[user.Id].IsLocalQueuedSend));
        Assert.Single(users, user => metadata[user.Id].GatewayMessageId == "remote-same");
    }

    [Fact]
    public async Task ChatMessageReceived_NoEnvelopeEchoDoesNotConsumeSingleMediaPromotion()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-media", Status = "started" });
        await provider.SendMessageAsync("main", "same", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "same.png",
                Content = Convert.ToBase64String([1]),
            },
        ]);
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same",
            State = "final",
            OpenClawId = "remote-same",
            OpenClawSeq = 99,
        });

        var users = snapshots[^1].Timelines["main"].Entries
            .Where(entry => entry.Kind == ChatTimelineItemKind.User && entry.Text == "same")
            .ToArray();
        Assert.Equal(2, users.Length);
        var metadata = provider.GetEntryMetadata("main");
        Assert.Contains(users, user => metadata[user.Id].IsLocalQueuedSend);
        Assert.Contains(users, user => metadata[user.Id].GatewayMessageId == "remote-same");
    }

    [Fact]
    public async Task SendMessageAsync_WhenGatewayThrows_DoesNotSuppressFutureRemoteUserEcho()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => throw new InvalidOperationException("gateway down");
        await provider.LoadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "same text"));

        bridge.SendBehavior = null;
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same text",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(2, timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "same text"));
    }

    [Fact]
    public async Task AgentEvent_ToolStart_AppendsToolCallEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"ls"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
        Assert.Equal("PowerShell", entry.ToolName);
        Assert.Equal("ls", entry.Text);
        Assert.Equal(ChatToolCallStatus.InProgress, entry.ToolResult);
        Assert.Equal("ls", entry.ToolArgs?["command"]?.GetValue<string>());
    }

    [Fact]
    public async Task AgentEvent_ToolStartThenResult_MarksToolSuccess()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"grep","args":{"pattern":"foo"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"grep","args":{"pattern":"foo"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_JobError_EmitsErrorEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.SendMessageAsync("main", "prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));
        snapshots.Clear();

        var evt = MakeAgentEvent("job", """{"state":"error"}""", runId: "run");
        evt.Summary = "kaboom";
        bridge.RaiseAgent(evt);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("kaboom"));
    }

    [Fact]
    public async Task AgentEvent_JobError_DispatchesNextQueuedMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        var evt = MakeAgentEvent("job", """{"state":"error"}""", runId: "run-1");
        evt.Summary = "kaboom";
        bridge.RaiseAgent(evt);

        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "second") == 1);

        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task AgentEvent_JobDone_ClearsTurnActive()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.SendMessageAsync("main", "hi");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run"));

        bridge.RaiseAgent(MakeAgentEvent("job", """{"state":"done"}""", runId: "run"));

        // Snapshot the timeline directly.
        var snap = await provider.LoadAsync();
        Assert.False(snap.Timelines["main"].TurnActive);
    }

    [Fact]
    public async Task SessionsUpdated_RebuildsThreadsAndSeedsTimelines()
    {
        var (bridge, provider, snapshots, _) = CreateProvider();
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main" },
            new SessionInfo { Key = "sub:abc", IsMain = false, DisplayName = "Sub" }
        });

        var snap = snapshots[^1];
        Assert.Equal(2, snap.Threads.Length);
        Assert.True(snap.Timelines.ContainsKey("main"));
        Assert.True(snap.Timelines.ContainsKey("sub:abc"));
        Assert.Equal("main", snap.DefaultThreadId);
    }

    [Fact]
    public async Task LoadHistoryAsync_OrdersMessagesByOpenClawSequenceBeforeTimestamp()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "c", Ts = 1_000, OpenClawSeq = 3 },
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "a", Ts = 3_000, OpenClawSeq = 1 },
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "b", Ts = 2_000, OpenClawSeq = 2 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entries = snapshots[^1].Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.User)
            .Select(e => e.Text)
            .ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, entries);
    }

    [Fact]
    public async Task LoadHistoryAsync_KeepsGatewayOrderWhenOnlySomeRowsHaveOpenClawSequence()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "r", Ts = 1_000 },
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "s", Ts = 2_000 },
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "t", Ts = 500, OpenClawSeq = 1 },
                new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "u", Ts = 600, OpenClawSeq = 2 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entries = snapshots[^1].Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.User)
            .Select(e => e.Text)
            .ToArray();
        Assert.Equal(new[] { "r", "s", "t", "u" }, entries);
    }

    [Fact]
    public async Task LoadHistoryAsync_DedupesQueuedLocalPromotionsWhenHistoryTimestampDiffers()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "a",
            State = "final",
            OpenClawId = "gateway-a",
            OpenClawSeq = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "a response",
            State = "final"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-a"));

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        await provider.SendMessageAsync("main", "b");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-b"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "b",
            State = "final",
            OpenClawId = "gateway-b",
            OpenClawSeq = 2,
        });

        Assert.Equal(
            new[] { "a", "b" },
            snapshots[^1].Timelines["main"].Entries
                .Where(e => e.Kind == ChatTimelineItemKind.User)
                .Select(e => e.Text)
                .ToArray());

        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "a",
                    Ts = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                    OpenClawSeq = 1
                },
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "b",
                    Ts = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeMilliseconds(),
                    OpenClawSeq = 2
                },
            }
        });
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var userTexts = snapshots[^1].Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.User)
            .Select(e => e.Text)
            .ToArray();
        Assert.Equal(new[] { "a", "b" }, userTexts);
    }

    [Fact]
    public async Task LoadHistoryAsync_KeepsSecondIdenticalQueuedPromptWhenHistoryContainsOneMatch()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-same-1", Status = "started" });
        await provider.SendMessageAsync("main", "same");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-same-1"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first same response",
            State = "final"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-same-1"));

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-same-2", Status = "started" });
        await provider.SendMessageAsync("main", "same");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-same-2"));

        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "same",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    OpenClawSeq = 1
                },
            }
        });
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var userTexts = snapshots[^1].Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.User)
            .Select(e => e.Text)
            .ToArray();
        Assert.Equal(new[] { "same", "same" }, userTexts);
    }

    [Fact]
    public async Task LoadHistoryAsync_DoesNotDropFreshIdenticalLocalPromptForStaleHistoryRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-same", Status = "started" });
        await provider.LoadAsync();

        await provider.SendMessageAsync("main", "same");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-same"));
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "same",
                    Ts = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),
                },
            }
        });
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        Assert.Equal(2, snapshots[^1].Timelines["main"].Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "same"));
    }

    [Fact]
    public async Task SessionResetCompletion_ClearsThreadTimelineAndIgnoresStaleHistory()
    {
        var historyTcs = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => historyTcs.Task;
        await provider.LoadAsync();
        var historyTask = provider.LoadHistoryAsync("main");
        await provider.SendMessageAsync("main", "hi");
        snapshots.Clear();

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var resetSnapshot = snapshots[^1];
        Assert.Empty(resetSnapshot.Timelines["main"].Entries);
        Assert.True(resetSnapshot.Timelines["main"].HistoryLoaded);

        historyTcs.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "assistant",
                    Text = "old history",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }
        });
        await historyTask;

        var latest = snapshots[^1];
        Assert.Empty(latest.Timelines["main"].Entries);
        Assert.True(latest.Timelines["main"].HistoryLoaded);

        await provider.SendMessageAsync("main", "after reset");

        latest = snapshots[^1];
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset");
        Assert.Empty(GetQueuedMessages(latest, "main"));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "after reset",
            State = "final"
        });

        latest = snapshots[^1];
        var entry = Assert.Single(latest.Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
        Assert.Equal("after reset", entry.Text);
    }

    [Fact]
    public async Task SessionResetCompletion_DropsLiveEventsFromPreResetSubmittedRun()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        var historyCalls = 0;
        bridge.HistoryBehavior = _ => { historyCalls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        await provider.LoadAsync();
        historyCalls = 0;
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "old-run"));
        snapshots.Clear();

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"stale agent"}""", runId: "old-run"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "stale chat"
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "old-run"));
        for (var i = 0; i < 20 && historyCalls == 0; i++)
            await Task.Delay(10);

        var latest = snapshots[^1];
        Assert.Empty(latest.Timelines["main"].Entries);
        Assert.True(historyCalls > 0);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "new remote message",
            Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()
        });
        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "new remote message",
            Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "new response"
        });

        latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "new remote message");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "new response");
    }

    [Fact]
    public async Task SessionResetCompletion_AbortsAndSuppressesLiveFramesFromPreResetSubmittedQueuedSend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        var historyCalls = 0;
        bridge.HistoryBehavior = _ => { historyCalls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-fresh", Status = "started" });
        await provider.LoadAsync();
        historyCalls = 0;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        await provider.SendMessageAsync("main", "b");
        await provider.SendMessageAsync("main", "c");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-a"));

        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var resetSnapshot = snapshots[^1];
        Assert.Empty(resetSnapshot.Timelines["main"].Entries);
        Assert.Empty(GetQueuedMessages(resetSnapshot, "main"));

        var staleTs = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "b",
            Ts = staleTs
        });
        var staleStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-b");
        staleStart.Ts = staleTs;
        bridge.RaiseAgent(staleStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "stale response",
            Ts = staleTs
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-b"));

        var latest = snapshots[^1];
        Assert.Empty(latest.Timelines["main"].Entries);
        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);
        for (var i = 0; i < 20 && !bridge.AbortedRunIds.Contains("run-b"); i++)
            await Task.Delay(10);
        Assert.Contains("run-b", bridge.AbortedRunIds);
        Assert.DoesNotContain("run-c", bridge.AbortedRunIds);
        for (var i = 0; i < 20 && historyCalls == 0; i++)
            await Task.Delay(10);
        Assert.True(historyCalls > 0);

        await provider.SendMessageAsync("main", "fresh");
        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-fresh");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response",
            Ts = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds()
        });

        latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_ShowsPersistedSubmittedRunFromForcedHistoryReload()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        var historyCalls = 0;
        var includePersisted = false;
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = includePersisted
                    ? new[]
                    {
                        new ChatMessageInfo
                        {
                            SessionKey = "main",
                            Role = "user",
                            Text = "b",
                            State = "final"
                        },
                        new ChatMessageInfo
                        {
                            SessionKey = "main",
                            Role = "assistant",
                            Text = "persisted response",
                            State = "final"
                        }
                    }
                    : Array.Empty<ChatMessageInfo>()
            });
        };
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        await provider.LoadAsync();
        historyCalls = 0;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        await provider.SendMessageAsync("main", "b");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-a"));
        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);
        Assert.Equal(new[] { "a", "b" }, bridge.SentMessages);

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "b",
            Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "live stale response",
            Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds()
        });

        Assert.Empty(snapshots[^1].Timelines["main"].Entries);

        includePersisted = true;
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-b"));
        for (var i = 0; i < 20; i++)
        {
            if (historyCalls > 0 &&
                snapshots[^1].Timelines["main"].Entries.Any(e =>
                    e.Kind == ChatTimelineItemKind.Assistant &&
                    e.Text == "persisted response"))
            {
                break;
            }
            await Task.Delay(10);
        }

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "b");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "persisted response");
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e => e.Text == "live stale response");
    }

    [Fact]
    public async Task SessionResetCompletion_TimestamplessRemoteUserCanOpenGateViaHistoryBackfill()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "remote no timestamp",
                    Ts = 0
                }
            }
        });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "remote no timestamp",
            Ts = 0
        });

        for (var i = 0; i < 20; i++)
        {
            if (snapshots.Count > 0 &&
                snapshots[^1].Timelines["main"].Entries.Any(e =>
                    e.Kind == ChatTimelineItemKind.User &&
                    e.Text == "remote no timestamp"))
            {
                break;
            }
            await Task.Delay(10);
        }

        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "remote response"
        });

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "remote no timestamp");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "remote response");
    }

    [Fact]
    public async Task SessionResetCompletion_DuplicateBackfillMergesAndOpensBufferedLifecycleGate()
    {
        var remoteTimestamp = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "[media attached: media://inbound/reset.pdf (application/pdf)]\nremote after reset",
                    Ts = remoteTimestamp,
                    OpenClawId = "reset-user",
                    OpenClawSeq = 41,
                },
            ],
        });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main",
        });
        snapshots.Clear();

        // Insert the projected row without reset proof, then buffer the
        // lifecycle start before the reset-aware duplicate backfill arrives.
        await provider.FetchRemoteUserMessageForTestsAsync("main", openResetGateOnSuccess: false);
        var pendingStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "reset-remote-run");
        pendingStart.Ts = remoteTimestamp + 1;
        bridge.RaiseAgent(pendingStart);
        snapshots.Clear();

        await provider.FetchRemoteUserMessageForTestsAsync("main", openResetGateOnSuccess: true);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "reset gate opened",
            State = "final",
            Ts = remoteTimestamp + 2,
        });

        var latest = snapshots[^1].Timelines["main"];
        var user = Assert.Single(latest.Entries, entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal("remote after reset", user.Text);
        Assert.Single(provider.GetEntryMetadata("main")[user.Id].Attachments!);
        Assert.Contains(latest.Entries, entry =>
            entry.Kind == ChatTimelineItemKind.Assistant &&
            entry.Text == "reset gate opened");
    }

    [Fact]
    public async Task SessionResetCompletion_LocalSendDoesNotReopenGateForStaleChatFrames()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "old-run"));
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run" });
        await provider.SendMessageAsync("main", "after reset local");
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "stale user echo"
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "stale assistant"
        });

        var latest = await provider.LoadAsync();
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset local");
        Assert.Empty(GetQueuedMessages(latest, "main"));
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Text.Contains("stale", StringComparison.Ordinal));

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run-2" });
        await provider.SendMessageAsync("main", "second after reset");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "second after reset"
        });

        latest = await provider.LoadAsync();
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset local");
        Assert.Equal("second after reset", Assert.Single(GetQueuedMessages(latest, "main")).Text);

        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh assistant"
        });

        latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh assistant");
    }

    [Fact]
    public async Task SessionResetCompletion_LateUnknownLifecycleStartDoesNotReopenGate()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var staleStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "old-run");
        staleStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(staleStart);
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"stale old run"}""", runId: "old-run"));

        var latest = snapshots[^1];
        Assert.Empty(latest.Timelines["main"].Entries);

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run" });
        await provider.SendMessageAsync("main", "after reset local");
        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response"
        });

        latest = snapshots[^1];
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Text.Contains("stale", StringComparison.Ordinal));
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_DropsLatePreResetAgentEventAfterGateOpens()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var preResetTs = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds();
        var resetTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run" });
        await provider.SendMessageAsync("main", "after reset local");
        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        freshStart.Ts = resetTs + 2_000;
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response",
            Ts = resetTs + 2_000
        });

        var staleAgent = MakeAgentEvent("assistant", """{"delta":"stale agent after gate"}""");
        staleAgent.Ts = preResetTs;
        bridge.RaiseAgent(staleAgent);

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Text.Contains("stale agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionResetCompletion_LifecycleStartBeforeSendResultOpensAfterRunAccepted()
    {
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var sendTask = provider.SendMessageAsync("main", "after reset local");
        var earlyStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        earlyStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(earlyStart);
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run" });
        sendGate.SetResult();
        await sendTask;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response"
        });

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset local");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_PreResetSendResultAfterResetDoesNotReopenGate()
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendStarted.TrySetResult();
            return sendGate.Task;
        };
        await provider.LoadAsync();

        var staleSendTask = provider.SendMessageAsync("main", "before reset");
        await sendStarted.Task;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        snapshots.Clear();

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "old-run" });
        sendGate.SetResult();
        await staleSendTask;

        Assert.Contains("old-run", bridge.AbortedRunIds);

        var staleStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "old-run");
        staleStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(staleStart);
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"stale old run"}""", runId: "old-run"));

        var latest = snapshots.Count > 0 ? snapshots[^1] : await provider.LoadAsync();
        Assert.Empty(latest.Timelines["main"].Entries);
    }

    [Fact]
    public async Task SessionResetCompletion_PreResetSendAckDoesNotClearNewQueuedMessage()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendCount++;
            if (sendCount == 1)
            {
                firstStarted.TrySetResult();
                return firstGate.Task;
            }
            return secondGate.Task;
        };
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "old-run", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run", Status = "started" });
        await provider.LoadAsync();
        snapshots.Clear();

        var firstSend = provider.SendMessageAsync("main", "before reset");
        await firstStarted.Task;
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "before reset");
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));

        var secondSend = provider.SendMessageAsync("main", "after reset");
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset");
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));

        firstGate.SetResult();
        await firstSend;

        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset");
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));

        secondGate.SetResult();
        await secondSend;

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run"));
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
    }

    [Fact]
    public async Task SessionResetCompletion_PostResetSendWithoutRunIdCanOpenOnFreshLifecycleStart()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.SendMessageAsync("main", "after reset local");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "after reset local"
        });

        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "gateway-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response"
        });

        var latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset local");
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_ProductionSubmissionAcceptsSkewedLifecycleAndFinal()
    {
        const string marker = "production skew marker";
        const string response = "production skew terminal";
        using var activities = new ChatActivityCollector();
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = async (_, _, _) =>
        {
            sendStarted.TrySetResult();
            await releaseSend.Task;
        };
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.LoadAsync();
        var skewedStartTs = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var sendTask = provider.SendMessageAsync("main", marker);
        await sendStarted.Task;
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "skewed-run");
        start.Ts = skewedStartTs;
        bridge.RaiseAgent(start);
        var early = MakeAgentEvent(
            "assistant",
            """{"delta":"early skewed assistant"}""",
            runId: "skewed-run");
        early.Ts = skewedStartTs + 1;
        bridge.RaiseAgent(early);
        releaseSend.TrySetResult();
        await sendTask;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = skewedStartTs + 2,
            OpenClawId = "user-skewed",
            OpenClawSeq = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = response,
            Ts = skewedStartTs + 3,
            OpenClawId = "assistant-skewed",
            OpenClawSeq = 2,
        });

        var terminal = snapshots[^1].Timelines["main"];
        Assert.False(terminal.TurnActive);
        Assert.DoesNotContain(
            terminal.Entries,
            entry => entry.Text.Contains(
                "early skewed assistant",
                StringComparison.Ordinal));
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName == ChatTelemetryTracker.TurnSpanName);

        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = marker,
                    Ts = skewedStartTs + 2,
                    OpenClawId = "user-skewed",
                    OpenClawSeq = 1,
                },
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "assistant",
                    State = "final",
                    Text = response,
                    Ts = skewedStartTs + 3,
                    OpenClawId = "assistant-skewed",
                    OpenClawSeq = 2,
                },
            ]
        });
        await provider.LoadHistoryAsync(
            "main",
            force: true,
            authoritative: true);
        var replay = snapshots[^1].Timelines["main"];
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_ExactEchoOverridesMismatchedAcceptedRun()
    {
        const string marker = "accepted mismatch marker";
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult
        {
            RunId = "response-run",
            Status = "started",
        });
        await provider.LoadAsync();
        var skewedTs = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.SendMessageAsync("main", marker);

        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "gateway-run");
        start.Ts = skewedTs;
        bridge.RaiseAgent(start);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = skewedTs + 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "accepted mismatch terminal",
            Ts = skewedTs + 2,
        });

        var latest = snapshots[^1].Timelines["main"];
        Assert.False(latest.TurnActive);
        Assert.Single(
            latest.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            latest.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == "accepted mismatch terminal");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_FallbackStepUsesExactEchoTimestampFloor()
    {
        const string marker = "fallback step marker";
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.LoadAsync();
        var lifecycleTimestamp = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        var echoTimestamp = lifecycleTimestamp - 500;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.SendMessageAsync("main", marker);

        var fallback = MakeAgentEvent(
            "lifecycle",
            """{"phase":"fallback_step"}""",
            runId: "fallback-run");
        fallback.Ts = lifecycleTimestamp;
        bridge.RaiseAgent(fallback);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = echoTimestamp,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fallback terminal",
            Ts = echoTimestamp + 1,
        });

        var latest = snapshots[^1].Timelines["main"];
        Assert.False(latest.TurnActive);
        Assert.Single(
            latest.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            latest.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == "fallback terminal");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_RunFloorDoesNotAdmitPreResetUserFrames()
    {
        const string marker = "user floor marker";
        const string delayedUser = "delayed unrelated user";
        const string freshUser = "fresh remote user";
        const string response = "same-run terminal";
        using var activities = new ChatActivityCollector();
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.LoadAsync();
        var lifecycleTimestamp = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.SendMessageAsync("main", marker);

        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "current-run");
        start.Ts = lifecycleTimestamp;
        bridge.RaiseAgent(start);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = lifecycleTimestamp + 1,
            OpenClawId = "marker-user",
            OpenClawSeq = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = delayedUser,
            Ts = lifecycleTimestamp + 2,
            OpenClawId = "delayed-user",
            OpenClawSeq = 2,
        });
        var sameRunFrame = MakeAgentEvent(
            "assistant",
            """{"delta":"same-run partial"}""",
            runId: "current-run");
        sameRunFrame.Ts = lifecycleTimestamp + 3;
        bridge.RaiseAgent(sameRunFrame);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = freshUser,
            Ts = DateTimeOffset.UtcNow.AddSeconds(1)
                .ToUnixTimeMilliseconds(),
            OpenClawId = "fresh-user",
            OpenClawSeq = 3,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = response,
            Ts = lifecycleTimestamp + 4,
            OpenClawId = "assistant-final",
            OpenClawSeq = 4,
        });

        var terminal = snapshots[^1].Timelines["main"];
        Assert.False(terminal.TurnActive);
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.DoesNotContain(
            terminal.Entries,
            entry => entry.Text == delayedUser);
        Assert.Contains(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == freshUser);
        Assert.Contains(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName ==
                ChatTelemetryTracker.TurnSpanName);

        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = marker,
                    Ts = lifecycleTimestamp + 1,
                    OpenClawId = "marker-user",
                    OpenClawSeq = 1,
                },
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "assistant",
                    State = "final",
                    Text = response,
                    Ts = lifecycleTimestamp + 4,
                    OpenClawId = "assistant-final",
                    OpenClawSeq = 4,
                },
            ]
        });
        await provider.LoadHistoryAsync(
            "main",
            force: true,
            authoritative: true);
        var replay = snapshots[^1].Timelines["main"];
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.DoesNotContain(
            replay.Entries,
            entry => entry.Text == delayedUser);
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName ==
                ChatTelemetryTracker.TurnSpanName);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_ExactEchoSelectsNewestBufferedLifecycle()
    {
        const string marker = "newest lifecycle marker";
        const string currentOutput = "current lifecycle output";
        using var activities = new ChatActivityCollector();
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.LoadAsync();
        var now = DateTimeOffset.UtcNow;
        var staleTimestamp = now.AddSeconds(-6).ToUnixTimeMilliseconds();
        var currentTimestamp = now.AddSeconds(-5).ToUnixTimeMilliseconds();
        var echoTimestamp = now.AddMilliseconds(-5_500)
            .ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.SendMessageAsync("main", marker);

        var staleStart = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "stale-run");
        staleStart.Ts = staleTimestamp;
        bridge.RaiseAgent(staleStart);
        var currentStart = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "current-run");
        currentStart.Ts = currentTimestamp;
        bridge.RaiseAgent(currentStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = echoTimestamp,
        });

        var staleOutput = MakeAgentEvent(
            "assistant",
            """{"delta":"stale lifecycle output"}""",
            runId: "stale-run");
        staleOutput.Ts = echoTimestamp + 1;
        bridge.RaiseAgent(staleOutput);
        var staleTerminal = MakeAgentEvent(
            "lifecycle",
            """{"phase":"error"}""",
            runId: "stale-run");
        staleTerminal.Ts = echoTimestamp + 2;
        bridge.RaiseAgent(staleTerminal);
        Assert.True(snapshots[^1].Timelines["main"].TurnActive);

        var currentFrame = MakeAgentEvent(
            "assistant",
            $$"""{"delta":"{{currentOutput}}"}""",
            runId: "current-run");
        currentFrame.Ts = echoTimestamp + 3;
        bridge.RaiseAgent(currentFrame);
        var currentTerminal = MakeAgentEvent(
            "lifecycle",
            """{"phase":"error"}""",
            runId: "current-run");
        currentTerminal.Ts = echoTimestamp + 4;
        bridge.RaiseAgent(currentTerminal);

        var terminal = snapshots[^1].Timelines["main"];
        Assert.False(terminal.TurnActive);
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.DoesNotContain(
            terminal.Entries,
            entry => entry.Text.Contains(
                "stale lifecycle output",
                StringComparison.Ordinal));
        Assert.Contains(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == currentOutput);
        var turn = Assert.Single(
            activities.Stopped,
            activity =>
                activity.OperationName ==
                ChatTelemetryTracker.TurnSpanName);
        Assert.Equal(
            "lifecycle_error",
            turn.GetTagItem(
                OpenClawTelemetryTagKey.Reason.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_FailedSendRemovesProductionSubmissionProof()
    {
        const string marker = "failed production marker";
        var (bridge, provider, snapshots, _) =
            CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
            throw new InvalidOperationException("send rejected");
        await provider.LoadAsync();
        var skewedTs = DateTimeOffset.UtcNow
            .AddSeconds(-5)
            .ToUnixTimeMilliseconds();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", marker));
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "rejected-run");
        start.Ts = skewedTs;
        bridge.RaiseAgent(start);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = marker,
            Ts = skewedTs + 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "must remain dropped",
            Ts = skewedTs + 2,
        });

        var latest = snapshots[^1].Timelines["main"];
        Assert.DoesNotContain(
            latest.Entries,
            entry => entry.Text == "must remain dropped");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_ReconciledSubmittedEchoOpensBufferedLifecycleAndTerminalizesOnce()
    {
        const string marker = "controlled post-reset marker";
        const string response = "controlled terminal response";
        using var activities = new ChatActivityCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.SendMessageAsync("main", marker);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));

        var state = Assert.IsType<ChatConversationState>(
            typeof(OpenClawChatDataProvider)
                .GetField(
                    "_state",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(provider));
        var queue = Assert.IsType<ChatQueueState>(
            typeof(ChatConversationState)
                .GetField(
                    "_queue",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        var lifecycle = Assert.IsType<ChatLifecycleState>(
            typeof(ChatConversationState)
                .GetField(
                    "_lifecycle",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        Assert.True(queue.TryConsumeLocalEcho("main", marker, out _));
        Assert.False(queue.HasPendingLocalEchoText("main", marker));

        var freshTs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "gateway-run");
        start.Ts = freshTs;
        bridge.RaiseAgent(start);
        var early = MakeAgentEvent(
            "assistant",
            """{"delta":"early assistant"}""",
            runId: "gateway-run");
        early.Ts = freshTs;
        bridge.RaiseAgent(early);
        Assert.DoesNotContain(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.Text.Contains("early assistant", StringComparison.Ordinal));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = marker,
            Ts = freshTs,
            OpenClawId = "user-echo",
            OpenClawSeq = 1,
        });
        Assert.True(lifecycle.TryGetActiveRun("main", out var activeRun));
        Assert.Equal("gateway-run", activeRun);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = marker,
            Ts = freshTs,
            OpenClawId = "duplicate-user-echo",
            OpenClawSeq = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = response,
            Ts = freshTs + 1,
            OpenClawId = "assistant-final",
            OpenClawSeq = 2,
        });

        var terminal = snapshots[^1].Timelines["main"];
        Assert.False(terminal.TurnActive);
        Assert.False(lifecycle.TryGetActiveRun("main", out _));
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            terminal.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);

        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = marker,
                    Ts = freshTs,
                    OpenClawId = "user-echo",
                    OpenClawSeq = 1,
                },
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "assistant",
                    State = "final",
                    Text = response,
                    Ts = freshTs + 1,
                    OpenClawId = "assistant-final",
                    OpenClawSeq = 2,
                },
            ]
        });
        await provider.LoadHistoryAsync(
            "main",
            force: true,
            authoritative: true);

        var replay = snapshots[^1].Timelines["main"];
        Assert.False(replay.TurnActive);
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.User &&
                     entry.Text == marker);
        Assert.Single(
            replay.Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == response);
        Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.TurnSpanName);

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_ReconciledEchoDispatchesPendingAbortForBufferedLifecycle()
    {
        const string marker = "controlled abort marker";
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.SendMessageAsync("main", marker);

        var state = Assert.IsType<ChatConversationState>(
            typeof(OpenClawChatDataProvider)
                .GetField(
                    "_state",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(provider));
        var reset = Assert.IsType<ChatResetState>(
            typeof(ChatConversationState)
                .GetField(
                    "_reset",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        var queue = Assert.IsType<ChatQueueState>(
            typeof(ChatConversationState)
                .GetField(
                    "_queue",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        reset.AddSubmittedLocalEcho("main", marker, DateTimeOffset.UtcNow);
        Assert.True(queue.TryConsumeLocalEcho("main", marker, out _));

        var freshTs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "gateway-run");
        start.Ts = freshTs;
        bridge.RaiseAgent(start);
        await provider.StopResponseAsync("main");
        Assert.Empty(bridge.AbortedRunIds);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = marker,
            Ts = freshTs,
            OpenClawId = "user-echo",
        });
        await WaitForConditionAsync(() =>
            bridge.AbortedRunIds.Contains("gateway-run"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "delta",
            Text = "must stay suppressed",
            Ts = freshTs + 1,
        });

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "gateway-run");
        Assert.DoesNotContain(
            (await provider.LoadAsync()).Timelines["main"].Entries,
            entry => entry.Text.Contains(
                "must stay suppressed",
                StringComparison.Ordinal));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_RemoteUserOpenDispatchesPendingAbort()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var freshTs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "remote-run");
        start.Ts = freshTs;
        bridge.RaiseAgent(start);
        await provider.StopResponseAsync("main");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = "fresh remote user",
            Ts = freshTs,
            OpenClawId = "remote-user",
        });
        await WaitForConditionAsync(() =>
            bridge.AbortedRunIds.Contains("remote-run"));

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "remote-run");
        var end = MakeAgentEvent(
            "lifecycle",
            """{"phase":"end"}""",
            runId: "remote-run");
        end.Ts = freshTs + 1;
        bridge.RaiseAgent(end);
        Assert.DoesNotContain(
            activities.Stopped,
            activity =>
                activity.OperationName == ChatTelemetryTracker.TurnSpanName);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_AcceptedSendOpenDispatchesPendingAbort()
    {
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = async (_, _, _) =>
        {
            sendStarted.TrySetResult();
            await releaseSend.Task;
        };
        bridge.SendResults.Enqueue(new ChatSendResult
        {
            RunId = "accepted-run",
            Status = "started"
        });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var sendTask = provider.SendMessageAsync(
            "main",
            "accepted send marker");
        await sendStarted.Task;
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "accepted-run");
        start.Ts = DateTimeOffset.UtcNow
            .AddSeconds(2)
            .ToUnixTimeMilliseconds();
        bridge.RaiseAgent(start);
        await provider.StopResponseAsync("main");
        releaseSend.TrySetResult();
        await sendTask;
        await WaitForConditionAsync(() =>
            bridge.AbortedRunIds.Contains("accepted-run"));

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "accepted-run");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_RemoteBackfillOpenDispatchesPendingAbort()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "timestamp-less remote user",
                    Ts = 0,
                    OpenClawId = "remote-user",
                },
            ]
        });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "backfill-run");
        start.Ts = DateTimeOffset.UtcNow
            .AddSeconds(2)
            .ToUnixTimeMilliseconds();
        bridge.RaiseAgent(start);
        await provider.StopResponseAsync("main");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = "timestamp-less remote user",
            Ts = 0,
            OpenClawId = "remote-user",
        });
        await WaitForConditionAsync(() =>
            bridge.AbortedRunIds.Contains("backfill-run"),
            attempts: 200);

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "backfill-run");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_FailedDeferredAbortRollsBackBufferedRun()
    {
        const string marker = "controlled failed abort marker";
        const string queued = "queued while abort fails";
        var (bridge, provider, _, notifications) =
            CreateProvider(new[] { MainSession() });
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var abortStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.AbortBehavior = async _ =>
        {
            abortStarted.TrySetResult();
            await releaseAbort.Task;
            throw new InvalidOperationException("deferred abort failed");
        };
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.SendMessageAsync("main", marker);

        var state = Assert.IsType<ChatConversationState>(
            typeof(OpenClawChatDataProvider)
                .GetField(
                    "_state",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(provider));
        var reset = Assert.IsType<ChatResetState>(
            typeof(ChatConversationState)
                .GetField(
                    "_reset",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        var queue = Assert.IsType<ChatQueueState>(
            typeof(ChatConversationState)
                .GetField(
                    "_queue",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        var lifecycle = Assert.IsType<ChatLifecycleState>(
            typeof(ChatConversationState)
                .GetField(
                    "_lifecycle",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                .GetValue(state));
        reset.AddSubmittedLocalEcho("main", marker, DateTimeOffset.UtcNow);
        Assert.True(queue.TryConsumeLocalEcho("main", marker, out _));

        var freshTs = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds();
        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "gateway-run");
        start.Ts = freshTs;
        bridge.RaiseAgent(start);
        await provider.StopResponseAsync("main");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            State = "final",
            Text = marker,
            Ts = freshTs,
            OpenClawId = "user-echo",
        });
        await abortStarted.Task;
        await provider.SendMessageAsync("main", queued);
        Assert.DoesNotContain(queued, bridge.SentMessages);
        releaseAbort.TrySetResult();
        await WaitForConditionAsync(() =>
            notifications.Any(notification =>
                notification.Kind == ChatProviderNotificationKind.Error &&
                notification.Message == "deferred abort failed"));
        await WaitForConditionAsync(() =>
            bridge.SentMessages.Contains(queued),
            attempts: 200);

        Assert.False(provider.IsResponseSuppressed);
        Assert.False(lifecycle.TryGetActiveRun("main", out _));
        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "gateway-run");
        Assert.Single(
            notifications,
            notification =>
                notification.Kind == ChatProviderNotificationKind.Error &&
                notification.Message == "deferred abort failed");
        Assert.Contains(queued, bridge.SentMessages);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DeferredAbort_AgentLifecycleFailureRollsBackRun()
    {
        var (bridge, provider, snapshots, notifications) =
            CreateProvider(new[] { MainSession() });
        bridge.AbortBehavior = _ =>
            throw new InvalidOperationException("agent abort failed");
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.LoadAsync();
        await provider.SendMessageAsync("main", "waiting for lifecycle");
        await provider.StopResponseAsync("main");

        var start = MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "gateway-run");
        start.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bridge.RaiseAgent(start);
        await WaitForConditionAsync(() =>
            notifications.Any(notification =>
                notification.Kind == ChatProviderNotificationKind.Error &&
                notification.Message == "agent abort failed"));

        bridge.RaiseAgent(MakeAgentEvent(
            "assistant",
            """{"delta":"accepted after rollback"}""",
            runId: "gateway-run"));

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "gateway-run");
        Assert.Single(
            notifications,
            notification =>
                notification.Kind == ChatProviderNotificationKind.Error &&
                notification.Message == "agent abort failed");
        Assert.Contains(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.Kind == ChatTimelineItemKind.Assistant &&
                     entry.Text == "accepted after rollback");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DeferredAbort_StaleBeforeHandleDoesNotScheduleWork()
    {
        var scheduleCount = 0;
        var (bridge, provider, _, _) = CreateProvider(
            new[] { MainSession() },
            deferredAbortScheduler: work =>
            {
                scheduleCount++;
                return Task.CompletedTask;
            });
        var state = GetConversationState(provider);
        var token = state.CaptureHistoryToken("main");
        var generation = new ChatRuntimeGeneration(
            token.ConnectionGeneration,
            token.ResetGeneration);
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var opened = new ChatOpenedLifecycleTransition(
            MakeAgentEvent(
                "lifecycle",
                """{"phase":"start"}""",
                runId: "stale-run"),
            AllowRemoteTurn: false,
            DeferredAbortRunId: "stale-run",
            DeferredAbortCount: 1);

        InvokeHandleOpenedLifecycle(
            provider,
            "main",
            opened,
            generation);
        await Task.Delay(25);

        Assert.Equal(0, scheduleCount);
        Assert.Empty(bridge.AbortedRunIds);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DeferredAbort_ResetBeforeScheduledBodyDoesNotSendAbort()
    {
        Func<Task>? scheduledWork = null;
        var (bridge, provider, _, _) = CreateProvider(
            new[] { MainSession() },
            deferredAbortScheduler: work =>
            {
                scheduledWork = work;
                return Task.CompletedTask;
            });
        var state = GetConversationState(provider);
        var token = state.CaptureHistoryToken("main");
        var generation = new ChatRuntimeGeneration(
            token.ConnectionGeneration,
            token.ResetGeneration);
        var opened = new ChatOpenedLifecycleTransition(
            MakeAgentEvent(
                "lifecycle",
                """{"phase":"start"}""",
                runId: "stale-run"),
            AllowRemoteTurn: false,
            DeferredAbortRunId: "stale-run",
            DeferredAbortCount: 1);
        InvokeHandleOpenedLifecycle(
            provider,
            "main",
            opened,
            generation);
        Assert.NotNull(scheduledWork);

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await scheduledWork!();

        Assert.Empty(bridge.AbortedRunIds);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task DeferredAbort_ResetDuringSuccessfulAbortSkipsStalePersistence()
    {
        Func<Task>? scheduledWork = null;
        var abortStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAbort = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, _, _) = CreateProvider(
            new[] { MainSession() },
            deferredAbortScheduler: work =>
            {
                scheduledWork = work;
                return Task.CompletedTask;
            });
        bridge.AbortBehavior = async _ =>
        {
            abortStarted.TrySetResult();
            await releaseAbort.Task;
        };
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "aborted user",
                    OpenClawId = "aborted-user-id",
                },
            ]
        });
        var state = GetConversationState(provider);
        var persistence = GetStatePersistence(provider);
        var token = state.CaptureHistoryToken("main");
        var generation = new ChatRuntimeGeneration(
            token.ConnectionGeneration,
            token.ResetGeneration);
        var opened = new ChatOpenedLifecycleTransition(
            MakeAgentEvent(
                "lifecycle",
                """{"phase":"start"}""",
                runId: "stale-run"),
            AllowRemoteTurn: false,
            DeferredAbortRunId: "stale-run",
            DeferredAbortCount: 1);
        InvokeHandleOpenedLifecycle(
            provider,
            "main",
            opened,
            generation);
        Assert.NotNull(scheduledWork);

        var work = scheduledWork!();
        await abortStarted.Task;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        releaseAbort.TrySetResult();
        await work;

        Assert.Single(
            bridge.AbortedRunIds,
            runId => runId == "stale-run");
        Assert.False(persistence.IsMessageAborted(
            "main",
            "aborted-user-id",
            resetGeneration: 0));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SessionResetCompletion_NoRunFallbackIgnoresBufferedStartBeforeLocalSend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        var staleStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "stale-run");
        staleStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(staleStart);
        bridge.SendResults.Enqueue(new ChatSendResult());
        await provider.SendMessageAsync("main", "after reset local");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "after reset local"
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "stale response"
        });

        var latest = await provider.LoadAsync();
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "after reset local");
        Assert.DoesNotContain(latest.Timelines["main"].Entries, e =>
            e.Text.Contains("stale", StringComparison.Ordinal));

        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "fresh-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response"
        });

        latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_PreResetSendFailureAfterResetDoesNotAppendError()
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendStarted.TrySetResult();
            return sendGate.Task;
        };
        await provider.LoadAsync();

        var staleSendTask = provider.SendMessageAsync("main", "before reset");
        await sendStarted.Task;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        snapshots.Clear();

        sendGate.SetException(new InvalidOperationException("old send failed"));
        await staleSendTask;

        var latest = snapshots.Count > 0 ? snapshots[^1] : await provider.LoadAsync();
        Assert.Empty(latest.Timelines["main"].Entries);
        Assert.DoesNotContain(notifications, n => n.Message == "old send failed");
    }

    [Fact]
    public async Task SessionResetCompletion_StaleSendFailureDoesNotClearCurrentEchoState()
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleSendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) =>
        {
            if (System.Threading.Interlocked.Increment(ref sendCount) == 1)
            {
                sendStarted.TrySetResult();
                return staleSendGate.Task;
            }

            return Task.CompletedTask;
        };
        await provider.LoadAsync();

        var staleSendTask = provider.SendMessageAsync("main", "same text");
        await sendStarted.Task;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "new-run" });
        await provider.SendMessageAsync("main", "same text");
        snapshots.Clear();

        staleSendGate.SetException(new InvalidOperationException("old send failed"));
        await staleSendTask;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "same text"
        });

        var latest = await provider.LoadAsync();
        Assert.Single(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "same text");
        Assert.DoesNotContain(notifications, n => n.Message == "old send failed");

        var freshStart = MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "new-run");
        freshStart.Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
        bridge.RaiseAgent(freshStart);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            State = "final",
            Text = "fresh response"
        });

        latest = snapshots[^1];
        Assert.Contains(latest.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "fresh response");
    }

    [Fact]
    public async Task SessionResetCompletion_IgnoresInFlightRemoteUserBackfill()
    {
        var backfillStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyTcs = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            backfillStarted.TrySetResult();
            return historyTcs.Task;
        };
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-remote"));
        await backfillStarted.Task;
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        snapshots.Clear();

        historyTcs.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "old remote user",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            }
        });
        await Task.Delay(100);

        var latest = snapshots.Count > 0 ? snapshots[^1] : await provider.LoadAsync();
        Assert.Empty(latest.Timelines["main"].Entries);
    }

    [Fact]
    public async Task StatusChanged_IsReflectedInSnapshotConnectionLabel()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseStatus(ConnectionStatus.Connected);
        Assert.Equal("Connected", snapshots[^1].ConnectionStatus);

        bridge.RaiseStatus(ConnectionStatus.Connecting);
        Assert.Equal("Connecting…", snapshots[^1].ConnectionStatus);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        Assert.Equal("Disconnected", snapshots[^1].ConnectionStatus);
    }

    [Fact]
    public async Task PostDelegate_IsUsedForChangedAndNotifications()
    {
        var bridge = new FakeBridge { Sessions = new[] { MainSession() } };
        var queued = new List<Action>();
        var provider = new OpenClawChatDataProvider(bridge, post: a => queued.Add(a));
        var snapshots = new List<ChatDataSnapshot>();
        provider.Changed += (_, e) => snapshots.Add(e.Snapshot);

        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "main", Role = "assistant", Text = "x", State = "final" });

        // Snapshot was queued, not invoked immediately.
        Assert.Empty(snapshots);
        Assert.NotEmpty(queued);
        foreach (var a in queued) a();
        Assert.NotEmpty(snapshots);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndStopsRaisingChanged()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.DisposeAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "main", Role = "assistant", Text = "after dispose", State = "final" });
        bridge.RaiseSessions(new[] { MainSession() });
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        Assert.Empty(snapshots);
        Assert.True(bridge.IsDisposed);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCalls_DisposesBridgeOnce()
    {
        var (bridge, provider, _, _) = CreateProvider([MainSession()]);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => provider.DisposeAsync().AsTask()));

        Assert.Equal(1, bridge.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_WithQueuedFollowUp_DoesNotDrainNextMessage()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "b");

        Assert.Equal(new[] { "a" }, bridge.SentMessages);

        await provider.DisposeAsync();
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        Assert.Equal(new[] { "a" }, bridge.SentMessages);
        Assert.True(bridge.IsDisposed);
    }

    [Fact]
    public async Task LoadAsync_FreshInstall_NoSessions_ExposesNotReadyComposeTarget()
    {
        // Replaces the pre-refactor CreateThreadAsync tests: there is no
        // create-thread RPC on the gateway, so the provider must never
        // synthesize a thread out of thin air. Instead, the snapshot exposes
        // a ChatComposeTarget that tells the UI whether/where to send.
        var (_, provider, _, _) = CreateProvider();
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.False(snap.ComposeTarget.IsReady);
        Assert.Null(snap.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task LoadAsync_HandshakeKnown_ZeroSessions_ExposesReadyComposeTarget()
    {
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // Real gateways always send sessions.list after handshake (even an
        // empty list). The provider waits for that signal before flipping
        // ComposeTarget.IsReady on — otherwise the UI would briefly render
        // the welcome zero-state for returning users mid-handshake.
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.True(snap.ComposeTarget.IsReady);
        Assert.Equal("agent:main:main", snap.ComposeTarget.SessionKey);
        Assert.Equal("main", snap.ComposeTarget.AgentId);
        Assert.Equal("agent:main:main", snap.DefaultThreadId);
    }

    [Fact]
    public async Task LoadAsync_HandshakeComplete_NoSessionKey_SignalsIncompatibleGateway()
    {
        // When the gateway completes the handshake but does not advertise
        // a mainSessionKey (or sessionDefaults.mainKey), the provider must surface
        // an "Incompatible gateway" connection label and a NotReady compose target
        // so the UI can show a clear "gateway update required" message rather than
        // silently blocking send. Relates to issue #459.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = null;   // incompatible gateway: no session key
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var snap = await provider.LoadAsync();

        Assert.Equal("Incompatible gateway", snap.ConnectionStatus);
        Assert.False(snap.ComposeTarget.IsReady);
        Assert.Null(snap.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task StatusChanged_IncompatibleGateway_IsReflectedInSnapshotConnectionLabel()
    {
        // Raise Connected with handshake present but no session key; the snapshot
        // must use "Incompatible gateway" rather than the plain "Connected" label.
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = null;
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.NotEmpty(snapshots);
        Assert.Equal("Incompatible gateway", snapshots[^1].ConnectionStatus);
        Assert.False(snapshots[^1].ComposeTarget.IsReady);
    }

    [Fact]
    public async Task LoadAsync_HandshakeKnownButSessionsNotYetReceived_ComposeTargetNotReady()
    {
        // Regression: in the brief window between hello-ok (HasHandshakeSnapshot
        // becomes true) and the first sessions.list, the provider used to expose
        // a ready ComposeTarget. The chat root would then synthesize a
        // compose-only thread and render the welcome zero-state — even for a
        // returning user whose real sessions were about to be delivered. The
        // sessions-list-received gate keeps ComposeTarget.IsReady=false until
        // the gateway has confirmed the session list for this connection.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // No RaiseSessions yet — simulate the mid-handshake window.
        var snap = await provider.LoadAsync();
        Assert.Empty(snap.Threads);
        Assert.False(snap.ComposeTarget.IsReady);
    }

    [Fact]
    public async Task StatusDisconnected_AfterSessionsReceived_ComposeTargetResetsToNotReady()
    {
        // The sessions-list-received gate must reset on disconnect — otherwise
        // a reconnect would keep ComposeTarget ready against a stale session
        // list from the previous connection.
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        Assert.True((await provider.LoadAsync()).ComposeTarget.IsReady);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        var snap = await provider.LoadAsync();
        Assert.False(snap.ComposeTarget.IsReady);
    }

    // ── Parity additions: streaming, lifecycle, reasoning, history, abort ──

    [Fact]
    public async Task AgentEvent_AssistantDelta_AppendsStreamingAssistantEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"Hel"}"""));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"lo "}"""));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"world"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var assistant = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Assistant, assistant.Kind);
        Assert.Equal("Hello world", assistant.Text);
        Assert.True(assistant.IsStreaming);
        Assert.True(timeline.TurnActive);
    }

    [Fact]
    public async Task AgentEvent_AssistantContent_IsIgnoredBecauseChatMessageCarriesFinalText()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("assistant",
            """{"content":"Final answer."}"""));

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task AgentEvent_LifecycleStart_SetsTurnActive()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle",
            """{"phase":"start"}""", runId: "run-1"));

        Assert.True(snapshots[^1].Timelines["main"].TurnActive);
    }

    [Fact]
    public async Task AgentEvent_LifecycleEnd_ClearsTurnActiveAndAssistant()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"hi"}"""));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        var snap = await provider.LoadAsync();
        var timeline = snap.Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Null(timeline.ActiveAssistantId);
    }

    [Fact]
    public async Task AgentEvent_LifecycleError_AppendsErrorStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        var evt = MakeAgentEvent("lifecycle", """{"phase":"error","message":"model unreachable"}""", runId: "run-1");
        bridge.RaiseAgent(evt);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("model unreachable"));
    }

    [Fact]
    public async Task AgentEvent_LifecycleError_DispatchesNextQueuedMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"error","message":"model unreachable"}""", runId: "run-1"));

        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 2 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Any(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "second"));

        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task AssistantFinal_DispatchesNextQueuedMessageWithoutLifecycleEnd()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 2 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Any(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "second"));

        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task QueuedDuplicateUserMessages_AcceptedAckPromotesEachPromptByIdentity()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-4", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "Hello");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "Hello");
        await provider.SendMessageAsync("main", "Hello");
        await provider.SendMessageAsync("main", "Hello");

        Assert.Equal(new[] { "Hello" }, bridge.SentMessages);
        Assert.Equal(3, GetQueuedMessages(snapshots[^1], "main").Count);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "reply 1",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Count == 2 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "Hello") == 2);

        Assert.Equal(2, bridge.SentMessages.Count);
        Assert.Equal(2, GetQueuedMessages(snapshots[^1], "main").Count);
        Assert.Equal(2, snapshots[^1].Timelines["main"].Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello"));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "reply 2",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 3; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Count == 1 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "Hello") == 3);

        Assert.Equal(3, bridge.SentMessages.Count);
        Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(3, snapshots[^1].Timelines["main"].Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello"));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "reply 3",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 4; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "Hello") == 4);

        Assert.Equal(new[] { "Hello", "Hello", "Hello", "Hello" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.All(bridge.SentIdempotencyKeys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
        Assert.Equal(4, bridge.SentIdempotencyKeys.Distinct(StringComparer.Ordinal).Count());

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "reply 4",
            State = "final",
        });

        Assert.Equal(
            new[] { "Hello", "reply 1", "Hello", "reply 2", "Hello", "reply 3", "Hello", "reply 4" },
            snapshots[^1].Timelines["main"].Entries
                .Where(e => e.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(e => e.Text)
                .ToArray());
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_RemovesOneDuplicateQueuedItemAndPreventsDispatch()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-next-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-next-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await provider.SendMessageAsync("main", "same");
        await provider.SendMessageAsync("main", "same");
        await provider.SendMessageAsync("main", "same");

        var queuedBeforeCancel = GetQueuedMessages(snapshots[^1], "main");
        Assert.Equal(3, queuedBeforeCancel.Count);
        Assert.All(queuedBeforeCancel, message => Assert.Equal("same", message.Text));
        var canceledId = queuedBeforeCancel[1].Id;

        var canceled = await provider.CancelQueuedMessageAsync("main", canceledId);

        var queuedAfterCancel = GetQueuedMessages(snapshots[^1], "main");
        Assert.True(canceled);
        Assert.Equal(2, queuedAfterCancel.Count);
        Assert.DoesNotContain(queuedAfterCancel, message => message.Id == canceledId);
        Assert.All(queuedAfterCancel, message => Assert.Equal("same", message.Text));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "active response",
            State = "final",
        });
        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 2 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 1);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "response 1",
            State = "final",
        });
        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 3 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 0);

        Assert.Equal(new[] { "active", "same", "same" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(2, snapshots[^1].Timelines["main"].Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "same"));
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_RemovesFailedQueuedCard()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "gateway rejected queued send" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await provider.SendMessageAsync("main", "failed queued");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "active response",
            State = "final",
        });
        await WaitForConditionAsync(() => HasFailedQueuedMessage(snapshots[^1], "main", "failed queued"));

        var failed = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(ChatQueuedMessageSendState.Failed, failed.SendState);

        var canceled = await provider.CancelQueuedMessageAsync("main", failed.Id);

        Assert.True(canceled);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.False(snapshots[^1].QueuedMessagesByThread?.ContainsKey("main") == true);
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_RemovingLastQueuedMessageClearsStaleDrainGuard()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await provider.SendMessageAsync("main", "queued");

        var queued = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        var scheduled = GetQueuedDrainScheduledThreads(provider);
        scheduled.Add("main");

        var canceled = await provider.CancelQueuedMessageAsync("main", queued.Id);

        Assert.True(canceled);
        Assert.DoesNotContain("main", scheduled);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_ReturnsFalseForSendingQueuedCard()
    {
        var queuedSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQueuedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-queued", Status = "started" });
        bridge.SendBehavior = async (message, _, _) =>
        {
            if (message == "queued")
            {
                queuedSendStarted.SetResult();
                await releaseQueuedSend.Task;
            }
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await provider.SendMessageAsync("main", "queued");
        var queued = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "active response",
            State = "final",
        });
        await queuedSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Single().SendState == ChatQueuedMessageSendState.Sending);

        var canceled = await provider.CancelQueuedMessageAsync("main", queued.Id);

        Assert.False(canceled);
        Assert.Equal(ChatQueuedMessageSendState.Sending, GetQueuedMessages(snapshots[^1], "main").Single().SendState);
        releaseQueuedSend.SetResult();
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_DoesNotTurnActiveLocalRunIntoRemoteRun()
    {
        var historyCalls = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" });
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "active");
        await provider.SendMessageAsync("main", "queued");

        var queued = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        await provider.CancelQueuedMessageAsync("main", queued.Id);

        historyCalls = 0;
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await Task.Delay(50);

        Assert.Equal(0, historyCalls);
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_LastQueuedAfterLifecycleEndAllowsNextRemoteRunBackfill()
    {
        var historyCalls = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-active", Status = "started" });
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "remote prompt" },
                },
            });
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "active");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-active"));
        await provider.SendMessageAsync("main", "queued");
        var queued = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-active"));
        var canceled = await provider.CancelQueuedMessageAsync("main", queued.Id);
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-remote"));

        Assert.True(canceled);
        await WaitForConditionAsync(() =>
            historyCalls > 0 &&
            snapshots[^1].Timelines["main"].Entries.Any(entry =>
                entry.Kind == ChatTimelineItemKind.User && entry.Text == "remote prompt"));
        Assert.True(historyCalls > 0);
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User && entry.Text == "remote prompt");
        Assert.DoesNotContain(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User && entry.Text == "queued");
    }

    [Fact]
    public async Task QueuedSend_LifecycleStartBeforeAck_PromotesByIdempotencyKey()
    {
        var secondSendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "started" });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendCount++;
            if (sendCount == 2)
            {
                var preAckRunId = Assert.Single(bridge.SentIdempotencyKeys.Skip(1));
                Assert.False(string.IsNullOrWhiteSpace(preAckRunId));
                bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: preAckRunId));
                return secondSendGate.Task;
            }

            return Task.CompletedTask;
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");

        secondSendGate.SetResult();
    }

    [Fact]
    public async Task QueuedSend_InFlightAckWithoutLifecycle_RequeuesAndRetriesSameIdempotencyKey()
    {
        using var activities = new ChatActivityCollector();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "in_flight" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "Hello");

        var postFinalSnapshotStart = snapshots.Count;
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        for (var i = 0; i < 30 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        Assert.Equal(2, bridge.SentMessages.Count);
        var firstAttemptKey = bridge.SentIdempotencyKeys[1];
        ChatDataSnapshot? requeuedSnapshot = null;
        for (var i = 0; i < 30 && requeuedSnapshot is null; i++)
        {
            requeuedSnapshot = snapshots
                .Skip(postFinalSnapshotStart)
                .LastOrDefault(snapshot =>
                {
                    var queued = GetQueuedMessages(snapshot, "main");
                    return queued.Count == 1 &&
                        queued[0].Text == "Hello" &&
                        queued[0].SendState == ChatQueuedMessageSendState.Queued &&
                        snapshot.Timelines["main"].Entries.Count(e =>
                            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello") == 0;
                });
            if (requeuedSnapshot is null)
                await Task.Delay(10);
        }

        Assert.NotNull(requeuedSnapshot);

        bridge.RaiseSessions(new[] { MainSession() });
        for (var i = 0; i < 30 && bridge.SentMessages.Count < 3; i++)
            await Task.Delay(10);

        Assert.Equal(3, bridge.SentMessages.Count);
        Assert.Equal(firstAttemptKey, bridge.SentIdempotencyKeys[2]);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-2"));

        var admissionStatuses = activities.Stopped
            .Where(activity => activity.OperationName == ChatTelemetryTracker.SendSpanName)
            .Select(activity => activity.GetTagItem(ChatTelemetryTracker.AdmissionStatusTag))
            .ToArray();
        Assert.Equal(new object?[] { "accepted", "deferred", "accepted" }, admissionStatuses);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CancelQueuedMessageAsync_DeferredInFlightRetryRemovesQueuedCardWithoutAbortOrResend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "in_flight" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "deferred");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() =>
        {
            var queued = GetQueuedMessages(snapshots[^1], "main");
            return bridge.SentMessages.Count == 2 &&
                queued.Count == 1 &&
                queued[0].Text == "deferred" &&
                queued[0].SendState == ChatQueuedMessageSendState.Queued;
        });

        var deferred = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        var canceled = await provider.CancelQueuedMessageAsync("main", deferred.Id);
        await Task.Delay(250);

        Assert.True(canceled);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(new[] { "first", "deferred" }, bridge.SentMessages);
        Assert.Empty(bridge.AbortedRunIds);
        Assert.DoesNotContain(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User && entry.Text == "deferred");
    }

    [Fact]
    public async Task QueuedSend_InFlightAckThenLifecycleBeforeRetry_PromotesWithoutResend()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "in_flight" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var lifecycleRaisedBeforeRetry = false;
        provider.Changed += (_, e) =>
        {
            if (lifecycleRaisedBeforeRetry || bridge.SentMessages.Count != 2)
                return;

            var queued = GetQueuedMessages(e.Snapshot, "main");
            if (queued.Count != 1 ||
                queued[0].Text != "Hello" ||
                queued[0].SendState != ChatQueuedMessageSendState.Queued)
            {
                return;
            }

            lifecycleRaisedBeforeRetry = true;
            bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));
        };

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "Hello");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() =>
        {
            var queued = GetQueuedMessages(snapshots[^1], "main");
            return bridge.SentMessages.Count == 2 &&
                queued.Count == 1 &&
                queued[0].Text == "Hello" &&
                queued[0].SendState == ChatQueuedMessageSendState.Queued;
        });

        await WaitForConditionAsync(() =>
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "Hello") == 1);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "Hello",
        });
        await Task.Delay(250);

        Assert.True(lifecycleRaisedBeforeRetry);
        Assert.Equal(2, bridge.SentMessages.Count);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "Hello");
    }

    [Fact]
    public async Task QueuedSend_InFlightRetry_DoesNotSuppressLaterSameTextRemoteUserMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "in_flight" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "Hello");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() =>
        {
            var queued = GetQueuedMessages(snapshots[^1], "main");
            return bridge.SentMessages.Count >= 2 &&
                queued.Count == 1 &&
                queued[0].Text == "Hello" &&
                queued[0].SendState == ChatQueuedMessageSendState.Queued;
        });

        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 3 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Count(entry =>
                entry.Kind == ChatTimelineItemKind.User && entry.Text == "Hello") == 1,
            attempts: 200);

        Assert.Equal(bridge.SentIdempotencyKeys[1], bridge.SentIdempotencyKeys[2]);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "Hello",
        });
        Assert.Single(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User && entry.Text == "Hello");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "Hello",
        });
        await WaitForConditionAsync(() =>
            snapshots[^1].Timelines["main"].Entries.Count(entry =>
                entry.Kind == ChatTimelineItemKind.User && entry.Text == "Hello") == 2);
    }

    [Fact]
    public async Task QueuedSend_InFlightAckWhileDifferentRunActive_RequeuesWithoutPromoting()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "in_flight" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        var releaseInFlightAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayFirstStuckAttempt = true;
        bridge.SendBehavior = (message, _, _) =>
        {
            if (message == "stuck" && delayFirstStuckAttempt)
            {
                delayFirstStuckAttempt = false;
                return releaseInFlightAck.Task;
            }

            return Task.CompletedTask;
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "stuck");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-run"));
        releaseInFlightAck.SetResult();
        await WaitForConditionAsync(() =>
        {
            var queued = GetQueuedMessages(snapshots[^1], "main");
            return queued.Count == 1 &&
                queued[0].Text == "stuck" &&
                queued[0].SendState == ChatQueuedMessageSendState.Queued &&
                snapshots[^1].Timelines["main"].Entries.Count(e =>
                    e.Kind == ChatTimelineItemKind.User && e.Text == "stuck") == 0;
        });

        Assert.Equal(2, bridge.SentMessages.Count);
        var firstAttemptKey = bridge.SentIdempotencyKeys[1];

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "remote-run"));
        await WaitForConditionAsync(() =>
            bridge.SentMessages.Count >= 3 &&
            GetQueuedMessages(snapshots[^1], "main").Count == 0 &&
            snapshots[^1].Timelines["main"].Entries.Count(e =>
                e.Kind == ChatTimelineItemKind.User && e.Text == "stuck") == 1,
            attempts: 200);

        Assert.Equal(firstAttemptKey, bridge.SentIdempotencyKeys[2]);
    }

    [Fact]
    public async Task DirectSend_InFlightAckWithoutLifecycle_FailsInsteadOfLeavingTurnActive()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "in_flight" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "direct"));

        Assert.Contains("in_flight", ex.Message, StringComparison.Ordinal);
        var snapshot = snapshots[^1];
        Assert.False(snapshot.Timelines["main"].TurnActive);
        Assert.Contains(snapshot.Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.Status &&
            entry.Text.Contains("in_flight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectSend_TimeoutAckWithRunId_FailsInsteadOfPromoting()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-timeout", Status = "timeout" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendMessageAsync("main", "direct timeout"));

        Assert.Contains("timeout", ex.Message, StringComparison.Ordinal);
        var snapshot = snapshots[^1];
        Assert.False(snapshot.Timelines["main"].TurnActive);
        Assert.Empty(GetQueuedMessages(snapshot, "main"));
        Assert.Contains(snapshot.Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.Status &&
            entry.Text.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueuedSend_TimeoutAckWithRunId_KeepsFailedQueuedMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-timeout", Status = "timeout" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "queued timeout");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() => HasFailedQueuedMessage(snapshots[^1], "main", "queued timeout"));

        var failed = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("queued timeout", failed.Text);
        Assert.Equal(ChatQueuedMessageSendState.Failed, failed.SendState);
        Assert.Contains("timeout", failed.ErrorText, StringComparison.Ordinal);
        Assert.Equal(new[] { "first", "queued timeout" }, bridge.SentMessages);
        Assert.DoesNotContain(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User &&
            entry.Text == "queued timeout");
    }

    [Fact]
    public async Task SendMessageAsync_FailedQueuedCardDoesNotForceNextSendToQueue()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "queued failed" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "failed queued");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() => HasFailedQueuedMessage(snapshots[^1], "main", "failed queued"));

        var beforeNextSendSnapshotCount = snapshots.Count;
        await provider.SendMessageAsync("main", "after failure");

        var queued = GetQueuedMessages(snapshots[^1], "main");
        Assert.Single(queued, message =>
            message.Text == "failed queued" &&
            message.SendState == ChatQueuedMessageSendState.Failed);
        Assert.DoesNotContain(snapshots.Skip(beforeNextSendSnapshotCount), snapshot =>
            GetQueuedMessages(snapshot, "main").Any(message => message.Text == "after failure"));
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, entry =>
            entry.Kind == ChatTimelineItemKind.User &&
            entry.Text == "after failure");
    }

    [Fact]
    public async Task QueuedSend_RepeatedInFlightAckWithoutLifecycle_EventuallyFails()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        for (var i = 0; i < 13; i++)
            bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-stuck", Status = "in_flight" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "stuck");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        await WaitForConditionAsync(() => bridge.SentMessages.Count >= 2);

        for (var i = 0; i < 20 && !HasFailedQueuedMessage(snapshots[^1], "main", "stuck"); i++)
        {
            bridge.RaiseSessions(new[] { MainSession() });
            await Task.Delay(10);
        }
        await WaitForConditionAsync(() => HasFailedQueuedMessage(snapshots[^1], "main", "stuck"), attempts: 1000);

        var failed = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal("stuck", failed.Text);
        Assert.Equal(ChatQueuedMessageSendState.Failed, failed.SendState);
        Assert.Contains("in_flight", failed.ErrorText, StringComparison.Ordinal);
        Assert.Single(bridge.SentIdempotencyKeys.Skip(1).Distinct(StringComparer.Ordinal));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("in_flight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueuedSend_AckPromotionKeepsRunMappingUntilTerminalCleanup()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first response",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() => GetQueuedMessages(snapshots[^1], "main").Count == 0);

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-2"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "second response",
            State = "final",
        });

        Assert.Contains(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "second response");
    }

    [Fact]
    public async Task IdentitylessIdenticalAssistant_DropsBeforeQueuedUserBoundaryButAllowsCurrentRunResponse()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() => GetQueuedMessages(snapshots[^1], "main").Count == 0);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
        });

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(
            new[] { "first", "OK", "second" },
            snapshots[^1].Timelines["main"].Entries
                .Where(e => e.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(e => e.Text)
                .ToArray());

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));

        var entries = snapshots[^1].Timelines["main"].Entries;
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(
            new[] { "first", "OK", "second" },
            entries
                .Where(e => e.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(e => e.Text)
                .ToArray());

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
        });

        Assert.Equal(
            new[] { "first", "OK", "second", "OK" },
            snapshots[^1].Timelines["main"].Entries
                .Where(e => e.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(e => e.Text)
                .ToArray());
    }

    [Fact]
    public async Task QueuedReplies_IgnoreIdentitylessRetransmitsAndStayMatchedToPrompts()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-a", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-b", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-c", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-d", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "a");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-a"));
        await provider.SendMessageAsync("main", "b");
        await provider.SendMessageAsync("main", "c");
        await provider.SendMessageAsync("main", "d");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - a",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - a",
            State = "final",
        });

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-b"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - b",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 3; i++)
            await Task.Delay(10);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - b",
            State = "final",
        });

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-c"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - c",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 4; i++)
            await Task.Delay(10);
        await WaitForConditionAsync(() => GetQueuedMessages(snapshots[^1], "main").Count == 0);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - c",
            State = "final",
        });

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-d"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ack - d",
            State = "final",
        });

        Assert.Equal(new[] { "a", "b", "c", "d" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(
            new[] { "a", "ack - a", "b", "ack - b", "c", "ack - c", "d", "ack - d" },
            snapshots[^1].Timelines["main"].Entries
                .Where(e => e.Kind is ChatTimelineItemKind.User or ChatTimelineItemKind.Assistant)
                .Select(e => e.Text)
                .ToArray());
    }

    [Fact]
    public async Task DroppedIdentitylessAssistant_IsNotReplayedAfterSendingPromptFails()
    {
        var secondSendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { Status = "failed", Error = "boom" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-3", Status = "started" });
        bridge.SendBehavior = (_, _, _) =>
        {
            sendCount++;
            return sendCount == 2 ? secondSendGate.Task : Task.CompletedTask;
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");
        await provider.SendMessageAsync("main", "third");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
        });
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
        });

        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "OK");

        secondSendGate.SetResult();
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 3; i++)
            await Task.Delay(10);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-3"));

        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "OK");
    }

    [Fact]
    public async Task IdentifiedDuplicateAssistant_DoesNotPromoteNextQueuedMessage()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");
        var firstFinal = new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "OK",
            State = "final",
            OpenClawId = "assistant-1",
            OpenClawSeq = 10,
        };
        bridge.RaiseChat(firstFinal);
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        bridge.RaiseChat(firstFinal);

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "OK");
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-2"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "cumulative prefix\nOK",
            State = "final",
            OpenClawId = "assistant-1",
            OpenClawSeq = 10,
        });

        Assert.True(snapshots[^1].Timelines["main"].TurnActive);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "OK");
        Assert.DoesNotContain(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text.Contains("cumulative prefix"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task AssistantFinal_ClearsLocalTurnStateForNextRemoteLifecycleStart()
    {
        var historyCalls = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            historyCalls++;
            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { SessionKey = "main", Role = "user", Text = "remote prompt" },
                },
            });
        };
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-local", Status = "started" });
        await provider.LoadAsync();

        await provider.SendMessageAsync("main", "local prompt");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-local"));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "local response",
            State = "final",
        });
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-remote"));
        for (var i = 0; i < 20 && historyCalls == 0; i++)
            await Task.Delay(10);

        Assert.True(historyCalls > 0);
    }

    [Fact]
    public async Task AssistantRetransmit_WithNewGatewayIdStillMatchesPriorSequenceOnlyFrame()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "stable response",
            State = "final",
            OpenClawSeq = 10,
        });
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "stable response",
            State = "final",
            OpenClawId = "message-10",
            OpenClawSeq = 10,
        });

        Assert.Empty(snapshots);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "cumulative prefix\nstable response",
            State = "final",
            OpenClawId = "message-10",
            OpenClawSeq = 10,
        });

        Assert.Empty(snapshots);
        var current = await provider.LoadAsync();
        Assert.Single(current.Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "stable response");
    }

    [Fact]
    public async Task AgentEvent_ReasoningDelta_AccumulatesReasoningEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"thinking… "}"""));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"step 2."}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Reasoning, entry.Kind);
        Assert.Equal("thinking… step 2.", entry.Text);
    }

    [Fact]
    public async Task AgentEvent_ReasoningItemEnd_StartsFreshReasoningBubble()
    {
        // Regression: when the model reasons → tool → reasons again within
        // a single turn, the second reasoning pass must render as its own
        // bubble. The gateway brackets each pass with
        // stream:"item", kind:"reasoning", phase:"start|end" — without
        // honoring the end marker the second pass concatenates into the
        // first bubble instead of replacing it.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"first pass"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item", """{"kind":"reasoning","phase":"end","itemId":"r1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"second pass"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var reasoningEntries = timeline.Entries.Where(e => e.Kind == ChatTimelineItemKind.Reasoning).ToList();
        Assert.Equal(2, reasoningEntries.Count);
        Assert.Equal("first pass", reasoningEntries[0].Text);
        Assert.Equal("second pass", reasoningEntries[1].Text);
    }

    [Fact]
    public async Task AgentEvent_ReasoningItemStart_IsIgnored()
    {
        // Only phase=end closes the bubble; phase=start is informational
        // and must not produce a stray timeline entry.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item", """{"kind":"reasoning","phase":"start","itemId":"r1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("reasoning", """{"delta":"only pass"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Reasoning, entry.Kind);
        Assert.Equal("only pass", entry.Text);
    }

    [Fact]
    public async Task StopResponseAsync_WithActiveRun_CallsAbortRpc()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-42"));

        await provider.StopResponseAsync("main");

        Assert.Single(bridge.AbortedRunIds);
        Assert.Equal("run-42", bridge.AbortedRunIds[0]);
    }

    [Fact]
    public async Task StopResponseAsync_WithoutActiveRun_DoesNotCallAbort()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.StopResponseAsync("main");

        Assert.Empty(bridge.AbortedRunIds);
    }

    [Fact]
    public async Task StopResponseAsync_AfterLifecycleEnd_NoLongerAborts()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-9"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-9"));

        await provider.StopResponseAsync("main");

        Assert.Empty(bridge.AbortedRunIds);
    }

    [Fact]
    public async Task StopResponseAsync_WithQueuedFollowUp_WaitsForConfirmedTerminalEvent()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");

        await provider.StopResponseAsync("main");

        Assert.Equal(new[] { "first" }, bridge.SentMessages);
        var waiting = Assert.Single(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Equal(ChatQueuedMessageSendState.Queued, waiting.SendState);

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        Assert.Contains("run-1", bridge.AbortedRunIds);
        Assert.Equal(new[] { "first", "second" }, bridge.SentMessages);
        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");
    }

    [Fact]
    public async Task QueuedDispatch_AssistantBeforeLifecycle_PromotesEachPromptOnce()
    {
        var secondSendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-2", Status = "started" });
        bridge.SendBehavior = (_, _, _) => ++sendCount == 2 ? secondSendGate.Task : Task.CompletedTask;
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.SendMessageAsync("main", "first");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        await provider.SendMessageAsync("main", "second");
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        for (var i = 0; i < 20 && bridge.SentMessages.Count < 2; i++)
            await Task.Delay(10);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "second response",
            State = "delta",
        });

        Assert.Empty(GetQueuedMessages(snapshots[^1], "main"));
        Assert.Single(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "second");

        secondSendGate.SetResult();
    }

    [Fact]
    public async Task LoadHistoryAsync_FoldsTranscriptIntoTimeline()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-123",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "Hi", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final" },
                new ChatMessageInfo { Role = "user", Text = "Bye", State = "final" }
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(3, timeline.Entries.Count);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[0].Kind);
        Assert.Equal("Hi", timeline.Entries[0].Text);
        Assert.Equal(ChatTimelineItemKind.Assistant, timeline.Entries[1].Kind);
        Assert.Equal("Hello!", timeline.Entries[1].Text);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[2].Kind);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task LoadHistoryAsync_AssistantNoReply_IsSuppressed()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "Hi", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "no_reply", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "Visible", State = "final" }
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(["Hi", "Visible"], timeline.Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_MultipleAssistantTurns_PreservesEachAsSeparateEntry()
    {
        // Regression test: previously every ChatMessageEvent would upsert the
        // active assistant entry, collapsing N assistant messages into 1. The
        // fix is to bracket each assistant message with ChatTurnEndEvent.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Q1", State = "final", Ts = 1 },
                new ChatMessageInfo { Role = "assistant", Text = "A1", State = "final", Ts = 2 },
                new ChatMessageInfo { Role = "user",      Text = "Q2", State = "final", Ts = 3 },
                new ChatMessageInfo { Role = "assistant", Text = "A2", State = "final", Ts = 4 },
                new ChatMessageInfo { Role = "user",      Text = "Q3", State = "final", Ts = 5 },
                new ChatMessageInfo { Role = "assistant", Text = "A3", State = "final", Ts = 6 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(6, timeline.Entries.Count);
        Assert.Equal(new[] { "Q1", "A1", "Q2", "A2", "Q3", "A3" },
            timeline.Entries.Select(e => e.Text).ToArray());
        Assert.Equal(new[]
        {
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
            ChatTimelineItemKind.User,      ChatTimelineItemKind.Assistant,
        }, timeline.Entries.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_SystemRole_RendersAsDimStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hello",   State = "final", Ts = 1 },
                new ChatMessageInfo { Role = "system",    Text = "ctx",     State = "final", Ts = 2 },
                new ChatMessageInfo { Role = "assistant", Text = "Hi back", State = "final", Ts = 3 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(3, timeline.Entries.Count);
        Assert.Equal(ChatTimelineItemKind.User, timeline.Entries[0].Kind);
        Assert.Equal(ChatTimelineItemKind.Status, timeline.Entries[1].Kind);
        Assert.Equal("ctx", timeline.Entries[1].Text);
        Assert.Equal(ChatTone.Dim, timeline.Entries[1].Tone);
        Assert.Equal(ChatTimelineItemKind.Assistant, timeline.Entries[2].Kind);
        Assert.Equal("Hi back", timeline.Entries[2].Text);
    }

    [Theory]
    [InlineData("toolresult")]
    [InlineData("tool_result")]
    public async Task LoadHistoryAsync_ToolResultRole_RendersAsToolChipEvenWithoutHeuristicMatch(string role)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = role, Text = "(no output)", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("(no output)", entry.ToolOutput);
    }

    [Fact]
    public async Task LoadHistoryAsync_StructuredToolBlocks_CorrelatesInputAndOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = "",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-1",
                            ToolName = "exec",
                            Args = JsonSerializer.Deserialize<JsonElement>(
                                """{"command":"pwd","workdir":"/workspace","yieldMs":1000}"""),
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "toolResult",
                    Text = "",
                    State = "final",
                    Ts = 2,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Result,
                            CallId = "call-1",
                            ToolName = "exec",
                            Text = "/workspace",
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("pwd", entry.ToolArgs?["command"]?.GetValue<string>());
        Assert.Equal("/workspace", entry.ToolOutput);
    }

    [Fact]
    public async Task LoadHistoryAsync_KeyedCallAndFlattenedResult_AvoidIdentityCollision()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(
            tempDir.DirectoryPath,
            "tool-metadata.json");
        File.WriteAllText(
            cachePath,
            JsonSerializer.Serialize(
                new Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>
                {
                    ["session-1"] =
                    [
                        new()
                        {
                            Ts = 100,
                            ToolName = "Bash",
                            Label = "cached flattened command",
                            ToolCallId = "unverified-flat-id",
                            RunId = "unverified-flat-run",
                            ToolArgs = new JsonObject
                            {
                                ["command"] = "echo flattened",
                            },
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                        new()
                        {
                            Ts = 200,
                            ToolName = "Exec",
                            Label = "cached structured command",
                            ToolCallId = "history-tool-0",
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                    ],
                }));
        var (bridge, provider, snapshots, _) = CreateProvider(
            [MainSession()],
            toolMetaCachePath: cachePath);
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "session-1",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Ts = 200,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "history-tool-0",
                            ToolName = "Exec",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Text = "flattened output",
                    Ts = 300,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        Assert.Collection(
            snapshots[^1].Timelines["main"].Entries,
            structured =>
            {
                Assert.Equal("history-tool-0", structured.ToolCallId);
                Assert.Equal("Exec", structured.ToolName);
                Assert.Equal(
                    ChatToolCallStatus.Interrupted,
                    structured.ToolResult);
                Assert.Null(structured.ToolOutput);
            },
            flattened =>
            {
                Assert.Equal("history-tool-1", flattened.ToolCallId);
                Assert.NotEqual(
                    "unverified-flat-id",
                    flattened.ToolCallId);
                Assert.Null(flattened.ToolRunId);
                Assert.Equal("Bash", flattened.ToolName);
                Assert.Equal(
                    "echo flattened",
                    flattened.ToolArgs?["command"]?.GetValue<string>());
                Assert.Equal("flattened output", flattened.ToolOutput);
                Assert.Equal(ChatToolCallStatus.Success, flattened.ToolResult);
            });
    }

    [Fact]
    public async Task LoadHistoryAsync_VerifiedIdMissStopsPositionalEnrichment()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(
            tempDir.DirectoryPath,
            "tool-metadata.json");
        File.WriteAllText(
            cachePath,
            JsonSerializer.Serialize(
                new Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>
                {
                    ["session-1"] =
                    [
                        new()
                        {
                            Ts = 100,
                            ToolName = "Bash",
                            Label = "stale positional command",
                            ToolArgs = new JsonObject
                            {
                                ["command"] = "stale command",
                            },
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                        new()
                        {
                            Ts = 200,
                            ToolName = "Read",
                            ToolCallId = "call-b",
                            IdentityStrength =
                                ChatToolIdentityStrength.Specific,
                        },
                    ],
                }));
        var (bridge, provider, snapshots, _) = CreateProvider(
            [MainSession()],
            toolMetaCachePath: cachePath);
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "session-1",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Ts = 100,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-a",
                            ToolName = "Exec",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Ts = 200,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-b",
                            ToolName = "Read",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Text = "unrelated flattened output",
                    Ts = 300,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var flattened = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.ToolOutput == "unrelated flattened output");
        Assert.NotEqual("Bash", flattened.ToolName);
        Assert.NotEqual(
            "stale positional command",
            flattened.Text);
        Assert.Null(flattened.ToolArgs);
        Assert.Equal("history-tool-0", flattened.ToolCallId);
    }

    [Fact]
    public async Task LoadHistoryAsync_PairedDuplicateResultsKeepPositionalEnrichment()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(
            tempDir.DirectoryPath,
            "tool-metadata.json");
        File.WriteAllText(
            cachePath,
            JsonSerializer.Serialize(
                new Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>
                {
                    ["session-1"] =
                    [
                        new()
                        {
                            Ts = 100,
                            ToolName = "Read",
                            ToolCallId = "call-x",
                        },
                        new()
                        {
                            Ts = 250,
                            ToolName = "Bash",
                            Label = "flattened command",
                            ToolArgs = new JsonObject
                            {
                                ["command"] = "echo flattened",
                            },
                        },
                    ],
                }));
        var (bridge, provider, snapshots, _) = CreateProvider(
            [MainSession()],
            toolMetaCachePath: cachePath);
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "session-1",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Ts = 100,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-x",
                            ToolName = "Read",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "developer",
                    Text = "turn boundary",
                    Ts = 150,
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Ts = 200,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Result,
                            CallId = "call-x",
                            ToolName = "Read",
                            Text = "read output",
                        },
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Result,
                            CallId = "call-x",
                            ToolName = "Read",
                            Text = "more read output",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Text = "flattened output",
                    Ts = 300,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var flattened = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.ToolOutput == "flattened output");
        Assert.Equal("Bash", flattened.ToolName);
        Assert.Equal("flattened command", flattened.Text);
        Assert.Equal(
            "echo flattened",
            flattened.ToolArgs?["command"]?.GetValue<string>());
    }

    [Fact]
    public async Task LoadHistoryAsync_SyntheticToolIdsSkipReservedStructuredIds()
    {
        var (bridge, provider, snapshots, _) =
            CreateProvider([MainSession()]);
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Ts = 100,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "history-tool-0",
                            ToolName = "Exec",
                        },
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "history-tool-2",
                            ToolName = "Read",
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Text = "first flattened",
                    Ts = 200,
                },
                new ChatMessageInfo
                {
                    Role = "toolresult",
                    Text = "second flattened",
                    Ts = 300,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var flattened = snapshots[^1].Timelines["main"].Entries
            .Where(entry => entry.ToolOutput is not null)
            .ToArray();
        Assert.Equal(
            ["history-tool-1", "history-tool-3"],
            flattened.Select(entry => entry.ToolCallId));
        Assert.Equal(
            ["first flattened", "second flattened"],
            flattened.Select(entry => entry.ToolOutput));
    }

    [Fact]
    public async Task AccessibilityHistoryCollisionFixture_UsesCurrentHistoryOwners()
    {
        using var tempDir = new TempDirectory();
        var (provider, bridge) =
            AccessibilityHistoryCollisionFixture.CreateForTesting(
                tempDir.DirectoryPath,
                name => name switch
                {
                    "OPENCLAW_ACCESSIBILITY_TEST_CHAT" => "1",
                    "OPENCLAW_ACCESSIBILITY_TEST_CHAT_FIXTURE" =>
                        AccessibilityHistoryCollisionFixture.FixtureName,
                    "OPENCLAW_TRAY_DATA_DIR" => tempDir.DirectoryPath,
                    _ => null,
                });
        await using var providerScope = provider;

        var snapshot = await provider.LoadAsync();

        Assert.Equal(1, bridge.HistoryRequestCount);
        Assert.Collection(
            snapshot.Timelines[
                AccessibilityHistoryCollisionFixture.ThreadId].Entries,
            structured =>
            {
                Assert.Equal("history-tool-0", structured.ToolCallId);
                Assert.Equal("Exec", structured.ToolName);
                Assert.Equal(
                    ChatToolCallStatus.Interrupted,
                    structured.ToolResult);
            },
            flattened =>
            {
                Assert.Equal("history-tool-1", flattened.ToolCallId);
                Assert.Equal("Bash", flattened.ToolName);
                Assert.Equal(
                    "flattened output owned by history-tool-1",
                    flattened.ToolOutput);
            });
    }

    [Fact]
    public async Task LoadHistoryAsync_StringEncodedToolArguments_PreservesSafeInput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-1",
                            ToolName = "exec",
                            Args = JsonSerializer.SerializeToElement(
                                """{"command":"pwd","workdir":"/workspace"}"""),
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("pwd", entry.ToolArgs?["command"]?.GetValue<string>());
        Assert.False(entry.ToolArgs?.ContainsKey("workdir"));
    }

    [Fact]
    public async Task LoadHistoryAsync_AfterRestart_ReplaysSanitizedArgumentsWithoutLocalPersistence()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        static ChatHistoryInfo History() => new()
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-1",
                            ToolName = "exec",
                            Args = JsonSerializer.SerializeToElement(
                                """{"command":"curl https://example.test --token abcdef1234567890ghij","workdir":"C:\\private"}"""),
                        },
                    ],
                },
            ],
        };

        var first = CreateProvider(
            new[] { MainSession() },
            toolMetaCachePath: cachePath);
        first.bridge.HistoryBehavior = _ => Task.FromResult(History());
        await first.provider.LoadHistoryAsync("main");
        await first.provider.DisposeAsync();

        var second = CreateProvider(
            new[] { MainSession() },
            toolMetaCachePath: cachePath);
        second.bridge.HistoryBehavior = _ => Task.FromResult(History());
        await second.provider.LoadHistoryAsync("main");

        var entry = Assert.Single(second.snapshots[^1].Timelines["main"].Entries);
        var command = entry.ToolArgs?["command"]?.GetValue<string>();
        Assert.NotNull(command);
        Assert.DoesNotContain(
            "abcdef1234567890ghij",
            command,
            StringComparison.Ordinal);
        Assert.False(entry.ToolArgs?.ContainsKey("workdir"));
        Assert.False(File.Exists(cachePath));
        await second.provider.DisposeAsync();
    }

    [Theory]
    [InlineData("toolResult")]
    [InlineData("tool_result")]
    public async Task LoadHistoryAsync_StringToolResult_CompletesOriginalCall(string role)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-1",
                            ToolName = "exec",
                            Args = JsonSerializer.Deserialize<JsonElement>("""{"command":"pwd"}"""),
                        },
                    ],
                },
                new ChatMessageInfo
                {
                    Role = role,
                    Text = "/workspace",
                    State = "final",
                    Ts = 2,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Result,
                            CallId = "call-1",
                            ToolName = "exec",
                            Text = "/workspace",
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("call-1", entry.ToolCallId);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("/workspace", entry.ToolOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadHistoryAsync_AbortedUserBeforeToolFirstAssistant_SuppressesEntireAssistantMessage(
        bool includeTrailingText)
    {
        var tool = new ChatToolContentInfo
        {
            Kind = ChatToolContentKind.Call,
            CallId = "call-1",
            ToolName = "exec",
            Args = JsonSerializer.Deserialize<JsonElement>("""{"command":"pwd"}"""),
        };
        var contentParts = new List<ChatMessageContentPartInfo>
        {
            new()
            {
                Kind = ChatMessageContentPartKind.Tool,
                Tool = tool,
            },
        };
        if (includeTrailingText)
        {
            contentParts.Add(new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Text,
                Text = "partial answer",
            });
        }

        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        MarkPersistedMessageAborted(provider, "main", "aborted-user");
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "run it",
                    State = "final",
                    Ts = 1,
                    OpenClawId = "aborted-user",
                },
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = includeTrailingText ? "partial answer" : string.Empty,
                    State = "final",
                    Ts = 2,
                    ToolContent = [tool],
                    ContentParts = contentParts,
                },
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = "next response",
                    State = "final",
                    Ts = 3,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        Assert.Collection(
            snapshots[^1].Timelines["main"].Entries,
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
                Assert.Equal("run it", entry.Text);
            },
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.Status, entry.Kind);
                Assert.Equal("Response was stopped", entry.Text);
                Assert.Equal(ChatTone.Warning, entry.Tone);
            },
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
                Assert.Equal("next response", entry.Text);
            });
    }

    [Fact]
    public async Task LoadHistoryAsync_InterleavedContentParts_PreserveChronologyAndCorrelation()
    {
        var call = new ChatToolContentInfo
        {
            Kind = ChatToolContentKind.Call,
            CallId = "call-1",
            ToolName = "exec",
            Args = JsonSerializer.Deserialize<JsonElement>("""{"command":"pwd"}"""),
        };
        var result = new ChatToolContentInfo
        {
            Kind = ChatToolContentKind.Result,
            CallId = "call-1",
            ToolName = "exec",
            Text = "/workspace",
        };
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = "Before\nMiddle\nAfter",
                    State = "final",
                    Ts = 1,
                    ToolContent = [call, result],
                    ContentParts =
                    [
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Text,
                            Text = "Before",
                        },
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Tool,
                            Tool = call,
                        },
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Text,
                            Text = "Middle",
                        },
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Tool,
                            Tool = result,
                        },
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Text,
                            Text = "After",
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entries = snapshots[^1].Timelines["main"].Entries;
        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
                Assert.Equal("Before", entry.Text);
            },
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.ToolCall, entry.Kind);
                Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
                Assert.Equal("/workspace", entry.ToolOutput);
            },
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
                Assert.Equal("Middle", entry.Text);
            },
            entry =>
            {
                Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
                Assert.Equal("After", entry.Text);
            });
    }

    [Fact]
    public async Task LoadHistoryAsync_StructuredOrphanResult_SynthesizesCompletedTool()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "toolResult",
                    Text = "",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Result,
                            CallId = "missing-call",
                            ToolName = "exec",
                            Text = "output",
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("output", entry.ToolOutput);
    }

    [Fact]
    public async Task LoadHistoryAsync_StructuredCallWithoutResult_IsInterrupted()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = "",
                    State = "final",
                    Ts = 1,
                    ToolContent =
                    [
                        new ChatToolContentInfo
                        {
                            Kind = ChatToolContentKind.Call,
                            CallId = "call-1",
                            ToolName = "exec",
                            Args = JsonSerializer.Deserialize<JsonElement>("""{"command":"sleep 30"}"""),
                        },
                    ],
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Interrupted, entry.ToolResult);
    }

    [Fact]
    public async Task LoadHistoryAsync_LiveToolStartedDuringRequest_RemainsCorrelated()
    {
        var pendingHistory = new TaskCompletionSource<ChatHistoryInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => pendingHistory.Task;
        await provider.LoadAsync();

        var historyLoad = provider.LoadHistoryAsync("main");
        bridge.RaiseAgent(MakeAgentEvent(
            "tool",
            """{"phase":"start","name":"exec","itemId":"live-call","args":{"command":"pwd"}}"""));

        pendingHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
        await historyLoad;

        var inProgress = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.InProgress, inProgress.ToolResult);

        bridge.RaiseAgent(MakeAgentEvent(
            "tool",
            """{"phase":"result","name":"exec","itemId":"live-call","result":{"content":"/workspace"}}"""));

        var completed = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Success, completed.ToolResult);
        Assert.Equal("/workspace", completed.ToolOutput);
    }

    [Fact]
    public async Task LoadHistoryAsync_ToolRole_RendersAsDimStatusEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "tool", Text = "tool transcript note", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatTimelineItemKind.Status, entry.Kind);
        Assert.Equal(ChatTone.Dim, entry.Tone);
        Assert.Equal("tool transcript note", entry.Text);
    }

    [Fact]
    public async Task LoadHistoryAsync_UnknownRole_FallsBackToVisibleAssistantEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "function", Text = "fallback text", State = "final", Ts = 1 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
        Assert.Equal("fallback text", entry.Text);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task LoadHistoryAsync_OutOfOrderTimestamps_AreSortedAscending()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            // Deliberately scrambled — provider must sort by Ts.
            Messages = new[]
            {
                new ChatMessageInfo { Role = "assistant", Text = "Last",  State = "final", Ts = 30 },
                new ChatMessageInfo { Role = "user",      Text = "First", State = "final", Ts = 10 },
                new ChatMessageInfo { Role = "assistant", Text = "Mid",   State = "final", Ts = 20 },
            }
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(new[] { "First", "Mid", "Last" },
            timeline.Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_IsIdempotent()
    {
        var calls = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => { calls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LoadHistoryAsync_WhenRequestFails_NotifiesAndAllowsRetry()
    {
        var calls = 0;
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("history down");

            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "recovered", State = "final", Ts = 1 },
                }
            });
        };
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        Assert.Contains(notifications, n =>
            n.Kind == ChatProviderNotificationKind.Error &&
            n.Message?.Contains("history down") == true);
        Assert.Empty(snapshots);

        await provider.LoadHistoryAsync("main");

        Assert.Equal(2, calls);
        Assert.Contains(snapshots[^1].Timelines["main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "recovered");
    }

    [Fact]
    public async Task SendMessageAsync_DoesNotForwardSessionIdToGateway()
    {
        // The live gateway rejects `sessionId` at the chat.send root with
        // "unexpected property". The provider tracks sessionId from chat.history
        // for client-side correlation but must not forward it. (Gateway client
        // ignores the sessionId arg; bridge still receives it for future use.)
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-7"
        });
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");

        await provider.SendMessageAsync("main", "Ping");

        // The bridge surface still receives the sessionId for tests / future
        // protocol use, but the production gateway client drops it before
        // serializing the chat.send request.
        Assert.Equal("sess-uuid-7", bridge.SentSessionIds[0]);
    }

    [Fact]
    public async Task LoadHistoryAsync_PersistsSessionIdForFutureSends()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "sess-uuid-7"
        });
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");

        await provider.SendMessageAsync("main", "Ping");

        Assert.Equal("sess-uuid-7", bridge.SentSessionIds[0]);
    }

    [Fact]
    public async Task SendMessageAsync_WithoutHistory_PassesNullSessionId()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.SendMessageAsync("main", "Ping");

        Assert.Null(bridge.SentSessionIds[0]);
    }

    // ── Iteration 3: tool result, abort marker, reconnect history, models ──

    [Fact]
    public async Task AgentEvent_ToolResult_ExtractsResultContent()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"echo hi"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"powershell","result":{"content":"hi\n"}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("hi\n", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResult_FallsBackToOutputField()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"grep","args":{"pattern":"foo"}}"""));
        // Some tools return output at data.output rather than data.result.content
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"grep","output":"line1\nline2"}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal("line1\nline2", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_CommandOutputThenToolResults_PreservesInformativeOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1","args":{"command":"echo hi","workdir":"/workspace","yieldMs":1000}}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"exec","title":"exec run command echo hi","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"itemId":"command:tool-1","phase":"end","title":"exec","toolCallId":"tool-1","command":"echo hi","status":"completed","output":"hi\n","exitCode":0,"durationMs":10,"cwd":"/workspace"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":false,"result":{"details":{"status":"completed","aggregated":"hi\n","exitCode":0,"durationMs":10,"cwd":"/workspace"}}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":false,"result":{"exitCode":0,"durationMs":42}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("hi\n", entry.ToolOutput);
        Assert.Equal("echo hi", entry.ToolArgs?["command"]?.GetValue<string>());
        Assert.False(entry.ToolArgs?.ContainsKey("workdir"));
        Assert.False(entry.ToolArgs?.ContainsKey("yieldMs"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("prefix-only")]
    public async Task AgentEvent_ItemWithoutUsableId_UsesLegacyFallbackAndPreservesOutput(string idCase)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        string ItemPayload(string phase)
        {
            var payload = new Dictionary<string, object?>
            {
                ["phase"] = phase,
                ["kind"] = "tool",
                ["title"] = "Tool"
            };
            if (idCase != "missing")
                payload["itemId"] = idCase switch
                {
                    "empty" => string.Empty,
                    "whitespace" => "   ",
                    _ => "tool:"
                };
            return JsonSerializer.Serialize(payload);
        }

        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item", ItemPayload("start"), runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent(
            "command_output",
            """{"phase":"end","output":"legacy output"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item", ItemPayload("end"), runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            """{"phase":"end"}""",
            runId: "run-1"));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("legacy output", entry.ToolOutput);
        Assert.False(timeline.TurnActive);
    }

    [Fact]
    public async Task AgentEvent_ParentCommandAndOutput_NormalizeToSingleRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","itemId":"tool:tool-1","toolCallId":"tool-1","args":{"command":"powershell -NoProfile -Command Get-ChildItem"}}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","status":"running","itemId":"tool:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","status":"running","itemId":"command:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"system.run","toolCallId":"tool-1","result":"parent done"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"command","name":"system.run","status":"completed","itemId":"command:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","status":"completed","itemId":"command:tool-1","toolCallId":"tool-1","output":"file.txt"}""",
            runId: "run-1"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal("powershell -NoProfile -Command Get-ChildItem",
            entry.ToolArgs!["command"]!.GetValue<string>());
        Assert.Equal("file.txt", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);

        var rows = ChatToolActivityPresentation.Project(snapshots[^1].Timelines["main"].Entries, "main", 1);
        var row = Assert.Single(rows);
        Assert.Same(entry, row.Entry);
    }

    [Fact]
    public async Task AgentEvent_ApplyPatchItem_PreservesCanonicalIdentity()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"apply_patch","title":"Tool","itemId":"tool:patch-1","toolCallId":"patch-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"patch","name":"apply_patch","title":"Tool","itemId":"patch:patch-1","toolCallId":"patch-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("patch",
            """{"itemId":"patch:patch-1","phase":"end","title":"apply patch","toolCallId":"patch-1","name":"apply_patch","added":["a.ts"],"modified":["b.ts"],"deleted":["c.ts"],"summary":"1 added, 1 modified, 1 deleted"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("Apply Patch", entry.ToolName);
        Assert.Equal("patch-1", entry.ToolCallId);
        Assert.Equal("1 added, 1 modified, 1 deleted", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);

        var row = Assert.Single(ChatToolActivityPresentation.Project(
            snapshots[^1].Timelines["main"].Entries,
            "main",
            1));
        Assert.Same(entry, row.Entry);
    }

    [Fact]
    public async Task AgentEvent_CodexBareItemIdWithConcreteName_RendersWebFetch()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","title":"Tool","name":"web_fetch","itemId":"codex-bare-id"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("web_fetch", entry.ToolName);
        Assert.Equal("codex-bare-id", entry.ToolCallId);
        Assert.Equal(ChatToolCallStatus.InProgress, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_BlockedCommandAfterSuccess_UpgradesToError()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","status":"running","itemId":"tool:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Run command","status":"running","itemId":"command:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"system.run","toolCallId":"tool-1","result":"ready"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"command","name":"system.run","status":"blocked","summary":"Awaiting approval before command can run.","itemId":"command:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("Awaiting approval before command can run.", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_SameNameConcurrentCallsWithReversedOutputs_StayIsolated()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","toolCallId":"run-1","args":{"command":"echo one"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","toolCallId":"run-2","args":{"command":"echo two"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","status":"completed","itemId":"command:run-2","toolCallId":"run-2","output":"two"}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","status":"completed","itemId":"command:run-1","toolCallId":"run-1","output":"one"}"""));

        Assert.Collection(
            snapshots[^1].Timelines["main"].Entries,
            first =>
            {
                Assert.Equal("run-1", first.ToolCallId);
                Assert.Equal("one", first.ToolOutput);
                Assert.Equal("echo one", first.ToolArgs!["command"]!.GetValue<string>());
            },
            second =>
            {
                Assert.Equal("run-2", second.ToolCallId);
                Assert.Equal("two", second.ToolOutput);
                Assert.Equal("echo two", second.ToolArgs!["command"]!.GetValue<string>());
            });
    }

    [Theory]
    [InlineData("system.run", "command", "Get-Date")]
    [InlineData("browser.proxy", "url", "https://example.test")]
    [InlineData("canvas.navigate", "path", "/dashboard")]
    public async Task AgentEvent_ToolStream_PreservesKnownIdentityAndAllowlistedPreview(
        string toolName,
        string argName,
        string argValue)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var json = JsonSerializer.Serialize(new
        {
            phase = "start",
            name = toolName,
            itemId = $"tool-{toolName}",
            args = new Dictionary<string, string> { [argName] = argValue }
        });

        bridge.RaiseAgent(MakeAgentEvent("tool", json));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(toolName, entry.ToolName);
        Assert.Equal(argValue, entry.ToolArgs![argName]!.GetValue<string>());
    }

    [Fact]
    public async Task AgentEvent_DisplayArgs_RedactsSecretsOmitsArbitraryJsonAndTruncates()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var longPath = new string('p', 500);
        var json = JsonSerializer.Serialize(new
        {
            phase = "start",
            name = "system.run",
            itemId = "tool-1",
            args = new
            {
                command = "curl https://example.test --token abcdef1234567890ghij",
                path = longPath,
                environment = new { OPENCLAW_TOKEN = "must-not-render" },
                payload = new { arbitrary = "json-must-not-render" }
            }
        });

        bridge.RaiseAgent(MakeAgentEvent("tool", json));

        var args = Assert.Single(snapshots[^1].Timelines["main"].Entries).ToolArgs!;
        var command = args["command"]!.GetValue<string>();
        Assert.DoesNotContain("abcdef1234567890ghij", command, StringComparison.Ordinal);
        Assert.True(args["path"]!.GetValue<string>().Length <= NativeToolProjector.MaxDisplayValueChars);
        Assert.False(args.ContainsKey("environment"));
        Assert.False(args.ContainsKey("payload"));
        Assert.DoesNotContain("must-not-render", args.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("json-must-not-render", args.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentEvent_CommandChildWithBidiTitle_DoesNotOverrideExplicitParentIdentity()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        var child = JsonSerializer.Serialize(new
        {
            phase = "start",
            kind = "command",
            title = "Bash\u202Eevil",
            itemId = "command:tool-1",
            toolCallId = "tool-1"
        });
        bridge.RaiseAgent(MakeAgentEvent("item", child));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal(ChatToolIdentityStrength.Explicit, entry.ToolIdentityStrength);
    }

    [Theory]
    [InlineData("Delete Files")]
    [InlineData("Bash delete files")]
    [InlineData("bAsH")]
    [InlineData("APPLY_PATCH")]
    [InlineData("pwsh")]
    [InlineData("\u0412ash")]
    public async Task AgentEvent_CommandChildTitle_DoesNotOverrideExplicitParentIdentity(string title)
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var child = JsonSerializer.Serialize(new
        {
            phase = "start",
            kind = "command",
            title,
            toolCallId = "tool-1",
            itemId = "command:tool-1"
        });

        bridge.RaiseAgent(MakeAgentEvent("item", child));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal(ChatToolIdentityStrength.Explicit, entry.ToolIdentityStrength);
    }

    [Fact]
    public async Task AgentEvent_LateCompletedCommandAfterTurnEnd_RepairsInterruptedWithoutReactivation()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","toolCallId":"tool-1","args":{"command":"Get-Date"}}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"update","kind":"command","name":"system.run","title":"Bash","meta":"running","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        Assert.Equal(
            ChatToolCallStatus.Interrupted,
            Assert.Single(snapshots[^1].Timelines["main"].Entries).ToolResult);

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"command","name":"system.run","status":"completed","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.False(timeline.TurnActive);
        Assert.Empty(timeline.ActiveToolCalls);
        Assert.Empty(timeline.PendingToolPresentations!);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal("Get-Date", entry.ToolArgs!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task StopResponseAsync_LateToolEndAfterLifecycleRemainsInterrupted()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent(
            "tool",
            """{"phase":"start","name":"system.run","toolCallId":"tool-1","args":{"command":"Get-Date"}}""",
            runId: "run-1"));

        await provider.StopResponseAsync("main");
        Assert.Equal(
            ChatToolCallStatus.Interrupted,
            Assert.Single(
                snapshots[^1].Timelines["main"].Entries,
                entry => entry.Kind == ChatTimelineItemKind.ToolCall).ToolResult);

        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            """{"phase":"end"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent(
            "item",
            """{"phase":"end","kind":"tool","name":"system.run","itemId":"tool:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));

        var entry = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            item => item.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Equal(ChatToolCallStatus.Interrupted, entry.ToolResult);
    }

    [Fact]
    public async Task StopResponseAsync_FailedAbortLateToolEndRepairsInterrupted()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.AbortBehavior = _ => throw new InvalidOperationException("Abort unavailable");
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            """{"phase":"start"}""",
            runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent(
            "tool",
            """{"phase":"start","name":"system.run","toolCallId":"tool-1","args":{"command":"Get-Date"}}""",
            runId: "run-1"));

        await provider.StopResponseAsync("main");
        Assert.Equal(
            ChatToolCallStatus.Interrupted,
            Assert.Single(
                snapshots[^1].Timelines["main"].Entries,
                entry => entry.Kind == ChatTimelineItemKind.ToolCall).ToolResult);

        bridge.RaiseAgent(MakeAgentEvent(
            "item",
            """{"phase":"end","kind":"tool","name":"system.run","itemId":"tool:tool-1","toolCallId":"tool-1"}""",
            runId: "run-1"));

        var entry = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            item => item.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_SameToolCallIdAcrossRunsCreatesDistinctRows()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","itemId":"command:tool-1","toolCallId":"tool-1","output":"first"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"apply_patch","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""", runId: "run-2"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"patch","name":"apply_patch","title":"Apply Patch","itemId":"patch:tool-1","toolCallId":"tool-1"}""", runId: "run-2"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"apply_patch","toolCallId":"tool-1","result":"second"}""", runId: "run-2"));

        Assert.Collection(
            snapshots[^1].Timelines["main"].Entries,
            first =>
            {
                Assert.Equal("system.run", first.ToolName);
                Assert.Equal("first", first.ToolOutput);
                Assert.Equal("run-1", first.ToolRunId);
            },
            second =>
            {
                Assert.Equal("Apply Patch", second.ToolName);
                Assert.Equal("second", second.ToolOutput);
                Assert.Equal("run-2", second.ToolRunId);
            });
    }

    [Fact]
    public async Task AgentEvent_ChildBeforeTurnEndAndLateParentMaterializesTerminalNonActiveRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"update","kind":"command","name":"system.run","title":"Bash","meta":"src","itemId":"command:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","itemId":"command:tool-1","toolCallId":"tool-1","output":"ready"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.False(timeline.TurnActive);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal("ready", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_LateLegacyParentUpsertsResolvedCacheGeneration()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var (bridge, provider, _, _) = CreateProvider(
            new[] { MainSession() },
            toolMetaCachePath: cachePath);
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"end"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));

        await provider.DisposeAsync();

        var cache = JsonSerializer.Deserialize<
            Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
                File.ReadAllText(cachePath));
        var entry = Assert.Single(Assert.Single(cache!).Value);
        Assert.Equal("tool-1", entry.ToolCallId);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal(1, entry.LegacyTurn);
    }

    [Fact]
    public async Task ConnectingTransition_InterruptsAndClearsToolReplayStateOnce()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""", runId: "run-1"));

        bridge.RaiseStatus(ConnectionStatus.Connecting);

        var timeline = snapshots[^1].Timelines["main"];
        var tool = Assert.Single(
            timeline.Entries,
            entry => entry.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Equal(ChatToolCallStatus.Interrupted, tool.ToolResult);
        Assert.False(timeline.TurnActive);
        Assert.Empty(timeline.ActiveToolCalls);
        Assert.Empty(timeline.PendingToolPresentations!);
        Assert.Empty(timeline.PendingToolOutcomes!);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        timeline = snapshots[^1].Timelines["main"];
        Assert.Empty(timeline.TerminalToolCorrelations!);
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var finalTimeline = snapshots[^1].Timelines["main"];
        Assert.Single(
            finalTimeline.Entries,
            entry => entry.Kind == ChatTimelineItemKind.ToolCall);
        Assert.False(finalTimeline.TurnActive);
    }

    [Fact]
    public async Task AgentEvent_ChildErrorTakesPrecedenceOverParentEndAndReplay()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"system.run","toolCallId":"tool-1","isError":true,"result":{"details":{"status":"error","error":"access denied"}}}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("access denied", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ParentEndThenChildError_UpgradesTerminalOutcome()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"system.run","toolCallId":"tool-1","isError":true,"result":{"details":{"status":"error","error":"late failure"}}}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("late failure", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_CommandChildBeforeParent_DrainsIdentityAndPendingOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","itemId":"command:tool-1","toolCallId":"tool-1","output":"12:00"}"""));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal("12:00", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_MultipleDuplicateChildrenAndLateOutput_KeepSingleCorrelatedRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","toolCallId":"tool-1","args":{"command":"Get-Date"}}"""));
        var parent = MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""");
        var bashChild = MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""");
        bridge.RaiseAgent(parent);
        bridge.RaiseAgent(parent);
        bridge.RaiseAgent(bashChild);
        bridge.RaiseAgent(bashChild);
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","itemId":"command:tool-1","toolCallId":"tool-1","output":"done"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("system.run", entry.ToolName);
        Assert.Equal("Get-Date", entry.ToolArgs!["command"]!.GetValue<string>());
        Assert.Equal("done", entry.ToolOutput);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
    }

    [Fact]
    public async Task AgentEvent_ToolCorrelation_IsIsolatedBySession()
    {
        var sessions = new[]
        {
            MainSession(),
            new SessionInfo { Key = "other", DisplayName = "Other", Status = "active" }
        };
        var (bridge, provider, snapshots, _) = CreateProvider(sessions);
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}""",
            sessionKey: "main"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}""",
            sessionKey: "other"));

        var snapshot = snapshots[^1];
        Assert.Equal("system.run", Assert.Single(snapshot.Timelines["other"].Entries).ToolName);
        Assert.Empty(snapshot.Timelines["main"].Entries);
    }

    [Fact]
    public async Task LoadHistoryAsync_CachedSpecificIdentityAndArgs_MatchLiveProjection()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var (liveBridge, liveProvider, liveSnapshots, _) = CreateProvider(
            new[] { MainSession() },
            toolMetaCachePath: cachePath);
        liveBridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "session-1"
        });
        await liveProvider.LoadAsync();
        await liveProvider.LoadHistoryAsync("main");
        liveBridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"system.run","toolCallId":"tool-1","args":{"command":"Get-Date"}}"""));
        liveBridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"tool","name":"system.run","title":"Tool","itemId":"tool:tool-1","toolCallId":"tool-1"}"""));
        liveBridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"start","kind":"command","name":"system.run","title":"Bash","itemId":"command:tool-1","toolCallId":"tool-1"}"""));
        var liveEntry = Assert.Single(liveSnapshots[^1].Timelines["main"].Entries);
        await liveProvider.DisposeAsync();

        var (historyBridge, historyProvider, historySnapshots, _) = CreateProvider(
            new[] { MainSession() },
            toolMetaCachePath: cachePath);
        historyBridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            SessionId = "session-1",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "toolresult",
                    Text = "12:00",
                    Ts = 1000
                }
            ]
        });
        await historyProvider.LoadAsync();
        await historyProvider.LoadHistoryAsync("main");

        var historyEntry = Assert.Single(historySnapshots[^1].Timelines["main"].Entries);
        Assert.Equal(liveEntry.ToolName, historyEntry.ToolName);
        Assert.Equal(
            liveEntry.ToolArgs!["command"]!.GetValue<string>(),
            historyEntry.ToolArgs!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task AgentEvent_ToolResultIsError_ExtractsCoreErrorDetails()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"web_fetch","toolCallId":"tool-1","args":{"url":"https://example"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"web_fetch","toolCallId":"tool-1","isError":true,"result":{"details":{"status":"error","error":"timeout after 30s","gatewayCode":"UNAVAILABLE"}}}"""));

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("timeout after 30s", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResultIsError_PrefersSafeToolErrorSummary()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":true,"toolErrorSummary":"Invalid arguments: command is required.","result":{"details":{"status":"error","error":"raw validator secret sk-test-must-not-render"}}}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("Invalid arguments: command is required.", entry.ToolOutput);
        Assert.DoesNotContain("sk-test-must-not-render", entry.ToolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentEvent_ToolSafeErrorSummary_SurvivesLaterFailedItemAndCommandOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":true,"toolErrorSummary":"Invalid arguments: command is required.","result":{"details":{"status":"error","error":"validator secret result-must-not-render"}}}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"tool","name":"exec","status":"failed","itemId":"tool:tool-1","toolCallId":"tool-1","error":"parent secret must-not-render"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"command","name":"exec","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","stderr":"command secret must-not-render"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","stderr":"output secret must-not-render"}""", runId: "run-1"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("Invalid arguments: command is required.", entry.ToolOutput);
        Assert.DoesNotContain("must-not-render", entry.ToolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentEvent_ToolSafeErrorSummary_ConvergesAcrossReversedAndDuplicateErrors()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}""", runId: "run-1"));
        for (var index = 0; index < 2; index++)
        {
            bridge.RaiseAgent(MakeAgentEvent("command_output",
                """{"phase":"end","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","stderr":"early secret must-not-render"}""", runId: "run-1"));
        }
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":true,"toolErrorSummary":"Invalid arguments.","result":{"details":{"error":"validator secret must-not-render"}}}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":true,"toolErrorSummary":"Invalid arguments: command is required.","result":{"details":{"error":"validator secret must-not-render"}}}""", runId: "run-1"));
        for (var index = 0; index < 2; index++)
        {
            bridge.RaiseAgent(MakeAgentEvent("item",
                """{"phase":"end","kind":"command","name":"exec","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","error":"late secret must-not-render"}""", runId: "run-1"));
        }

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("Invalid arguments: command is required.", entry.ToolOutput);
        Assert.DoesNotContain("must-not-render", entry.ToolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentEvent_ToolErrorWithoutSafeSummary_PreservesLatestErrorFallback()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":true,"result":{"details":{"error":"initial failure"}}}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("item",
            """{"phase":"end","kind":"command","name":"exec","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","error":"later command failure"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            """{"phase":"end","status":"failed","itemId":"command:tool-1","toolCallId":"tool-1","stderr":"latest stderr failure"}""", runId: "run-1"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("latest stderr failure", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResultSuccess_IgnoresStickyToolErrorSummary()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"result","name":"exec","toolCallId":"tool-1","isError":false,"toolErrorSummary":"Stale validation failure","result":"normal output"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("normal output", entry.ToolOutput);
        Assert.DoesNotContain("Stale validation failure", entry.ToolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentEvent_ToolResultContentArray_JoinsOnlyTypedTextBlocks()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"read","toolCallId":"tool-1","args":{"path":"/tmp/output.txt"}}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """
            {"phase":"result","name":"read","toolCallId":"tool-1","isError":false,"result":{"content":[{"type":"text","text":" line one "},{"type":"image","text":"must-not-render"},{"type":"custom","text":"must-not-render"},{"type":"text","text":"line two"}]}}
            """));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Success, entry.ToolResult);
        Assert.Equal("line one\nline two", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResultIsError_ExplicitErrorPrecedesPartialOutputAndIsBounded()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var explicitError = new string('e', 5000);

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"exec","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool", JsonSerializer.Serialize(new
        {
            phase = "result",
            name = "exec",
            toolCallId = "tool-1",
            isError = true,
            result = new { details = new { status = "failed", aggregated = "partial output" } },
            output = "partial output",
            error = explicitError
        })));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.DoesNotContain("partial output", entry.ToolOutput, StringComparison.Ordinal);
        Assert.StartsWith(new string('e', 100), entry.ToolOutput, StringComparison.Ordinal);
        Assert.EndsWith("(truncated)", entry.ToolOutput, StringComparison.Ordinal);
        Assert.True(entry.ToolOutput!.Length < explicitError.Length);
    }

    [Fact]
    public async Task AgentEvent_ToolPhaseError_RemainsCompatible()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"web_fetch","toolCallId":"tool-1"}"""));
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"error","name":"web_fetch","toolCallId":"tool-1","error":"legacy timeout"}"""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(ChatToolCallStatus.Error, entry.ToolResult);
        Assert.Equal("legacy timeout", entry.ToolOutput);
    }

    [Fact]
    public async Task AgentEvent_ToolResult_TruncatesVeryLargeOutput()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var huge = new string('x', 10000);
        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"read","args":{"path":"/big.txt"}}"""));
        var resultJson = "{\"phase\":\"result\",\"name\":\"read\",\"result\":{\"content\":\"" + huge + "\"}}";
        bridge.RaiseAgent(MakeAgentEvent("tool", resultJson));

        var entry = snapshots[^1].Timelines["main"].Entries[0];
        Assert.NotNull(entry.ToolOutput);
        Assert.True(entry.ToolOutput!.Length < huge.Length, "expected truncation");
        Assert.EndsWith("(truncated)", entry.ToolOutput);
    }

    [Fact]
    public async Task StopResponseAsync_DuringActiveTurn_AppendsAbortMarker()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "run-1"));
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"partial answer"}"""));
        snapshots.Clear();

        await provider.StopResponseAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Equals("Aborted", StringComparison.OrdinalIgnoreCase) &&
            e.Tone == ChatTone.Warning);
        Assert.False(timeline.TurnActive);
        Assert.Contains("partial answer", timeline.Entries.Select(e => e.Text));
    }

    [Fact]
    public async Task StopResponseAsync_WithoutActiveTurn_DoesNotAppendAbortMarker()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.StopResponseAsync("main");

        // Either no snapshot or snapshot timeline has no Status="Aborted".
        if (snapshots.Count > 0)
        {
            var timeline = snapshots[^1].Timelines["main"];
            Assert.DoesNotContain(timeline.Entries, e =>
                e.Kind == ChatTimelineItemKind.Status &&
                e.Text.Equals("Aborted", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Reconnect_AfterDisconnect_ReloadsOnlyExplicitlyRequestedThread()
    {
        var historyRequested = new List<string?>();
        var sessions = new[]
        {
            MainSession(),
            new SessionInfo { Key = "agent:main:secondary", DisplayName = "Secondary" },
        };
        var (bridge, provider, _, _) = CreateProvider(sessions);
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };

        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("agent:main:secondary");
        Assert.Equal(new[] { "main", "agent:main:secondary" }, historyRequested);

        historyRequested.Clear();
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.Empty(historyRequested);

        await provider.LoadHistoryAsync("main");

        Assert.Equal(new[] { "main" }, historyRequested);
    }

    [Fact]
    public async Task Reconnect_FromConnectingToConnected_DoesNotReload()
    {
        // The "just reconnected" condition should only fire on a transition
        // from a non-Connected state to Connected — not on the initial
        // Connecting → Connected boot sequence.
        var historyCalls = 0;
        var bridge = new FakeBridge { Sessions = new[] { MainSession() }, CurrentStatus = ConnectionStatus.Connected };
        var provider = new OpenClawChatDataProvider(bridge);
        bridge.HistoryBehavior = _ => { historyCalls++; return Task.FromResult(new ChatHistoryInfo { SessionKey = "main" }); };
        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.Equal(1, historyCalls);

        // Already Connected → setting Connected again is a no-op.
        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.Equal(1, historyCalls);
    }

    [Fact]
    public async Task Reconnect_IgnoresHistoryResponseFromPreviousConnection()
    {
        using var activities = new ChatActivityCollector();
        var staleHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyCalls = 0;
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => ++historyCalls == 1 ? staleHistory.Task : freshHistory.Task;

        await provider.LoadAsync();
        var staleLoad = provider.LoadHistoryAsync("main");
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var freshLoad = provider.LoadHistoryAsync("main");

        staleHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "stale", Ts = 1 } },
        });
        await staleLoad;

        // The stale request's finally block must not clear the newer request's
        // in-flight marker and allow a duplicate request.
        await provider.LoadHistoryAsync("main");
        Assert.Equal(2, historyCalls);

        freshHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "fresh", Ts = 2 } },
        });
        await freshLoad;

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, entry => entry.Text == "fresh");
        Assert.DoesNotContain(timeline.Entries, entry => entry.Text == "stale");

        var historySpans = activities.Stopped
            .Where(activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName)
            .ToArray();
        Assert.Equal(2, historySpans.Length);
        Assert.Single(historySpans, activity =>
            Equals("canceled", activity.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName())));
        Assert.Single(historySpans, activity =>
            Equals("success", activity.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName())));
        Assert.All(historySpans, activity =>
            Assert.Equal("initial", activity.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName())));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadHistoryAsync_ConcurrentCallsIssueOneRequestPerSessionGeneration()
    {
        var firstHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyCalls = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => ++historyCalls == 1 ? firstHistory.Task : secondHistory.Task;

        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var firstLoad = provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");
        Assert.Equal(1, historyCalls);

        firstHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
        await firstLoad;
        await provider.LoadHistoryAsync("main");
        Assert.Equal(1, historyCalls);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var secondLoad = provider.LoadHistoryAsync("main");
        await provider.LoadHistoryAsync("main");
        Assert.Equal(2, historyCalls);

        secondHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
        await secondLoad;
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadHistoryAsync_ConcurrentSessionsCompleteIndependently()
    {
        var mainHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondaryHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string?>();
        var sessions = new[]
        {
            MainSession(),
            new SessionInfo { Key = "agent:main:secondary", DisplayName = "Secondary" },
        };
        var (bridge, provider, snapshots, _) = CreateProvider(sessions);
        bridge.HistoryBehavior = key =>
        {
            calls.Add(key);
            return key == "main" ? mainHistory.Task : secondaryHistory.Task;
        };

        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        var mainLoad = provider.LoadHistoryAsync("main");
        var secondaryLoad = provider.LoadHistoryAsync("agent:main:secondary");
        secondaryHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "agent:main:secondary",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "secondary", Ts = 2 } },
        });
        await secondaryLoad;
        mainHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "main", Ts = 1 } },
        });
        await mainLoad;

        Assert.Equal(new[] { "main", "agent:main:secondary" }, calls);
        var snapshot = snapshots[^1];
        Assert.Contains(snapshot.Timelines["main"].Entries, entry => entry.Text == "main");
        Assert.Contains(snapshot.Timelines["agent:main:secondary"].Entries, entry => entry.Text == "secondary");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_CancelsInFlightHistoryWithoutPublishingStaleContent()
    {
        using var activities = new ChatActivityCollector();
        var pendingHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => pendingHistory.Task;

        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        var load = provider.LoadHistoryAsync("main");
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        await load.WaitAsync(TimeSpan.FromSeconds(1));

        pendingHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "stale", Ts = 1 } },
        });
        await Task.Yield();

        Assert.DoesNotContain(snapshots, snapshot =>
            snapshot.Timelines["main"].Entries.Any(entry => entry.Text == "stale"));
        Assert.Empty(notifications);
        var history = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("canceled", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Null(history.GetTagItem(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_ClearsHistoryOwnershipForLaterExplicitLoad()
    {
        var pendingHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            return calls == 1
                ? pendingHistory.Task
                : Task.FromResult(new ChatHistoryInfo
                {
                    SessionKey = "main",
                    Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "later", Ts = 2 } },
                });
        };

        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        var firstLoad = provider.LoadHistoryAsync("main");
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        await firstLoad.WaitAsync(TimeSpan.FromSeconds(1));

        await provider.LoadHistoryAsync("main").WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, calls);
        Assert.Empty(notifications);
        Assert.Contains(snapshots, snapshot =>
            snapshot.Timelines["main"].Entries.Any(entry => entry.Text == "later"));
        pendingHistory.SetResult(new ChatHistoryInfo { SessionKey = "main" });
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_DropsHistoryDeliveryQueuedAfterNewerStatusSnapshot()
    {
        var deliveries = new List<Action>();
        var bridge = new FakeBridge
        {
            Sessions = new[] { MainSession() },
            CurrentStatus = ConnectionStatus.Connected,
            HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "loaded", Ts = 1 } },
            }),
        };
        var provider = new OpenClawChatDataProvider(
            bridge,
            post: action => deliveries.Add(action),
            toolMetaCacheFilePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tool-metadata.json"));
        var snapshots = new List<ChatDataSnapshot>();
        provider.Changed += (_, args) => snapshots.Add(args.Snapshot);

        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.Single(deliveries);

        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        Assert.Equal(2, deliveries.Count);

        // Model a dispatcher race where disconnect is delivered before the
        // history callback that was queued from an earlier connection state.
        deliveries[1]();
        deliveries[0]();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal(ConnectionStatus.Disconnected.ToString(), snapshot.ConnectionStatus);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ModelsListUpdated_PopulatesAvailableModelsInSnapshot()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4" },
                new() { Id = "claude-sonnet-4.6", Name = "Claude Sonnet 4.6" },
                new() { Id = "ollama-only-id" }
            }
        });

        Assert.Equal(
            new[] { "gpt-5.4", "claude-sonnet-4.6", "ollama-only-id" },
            snapshots[^1].AvailableModels);
    }

    [Fact]
    public async Task ModelsListUpdated_KeepsExplicitlyUnconfiguredModelsDisabled()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", IsConfigured = true, HasConfiguredFlag = true },
                new() { Id = "gpt-5.5", IsConfigured = false, HasConfiguredFlag = true },
                new() { Id = "needs-auth", IsConfigured = false, HasConfiguredFlag = true, RequiresAuth = true },
                new() { Id = "legacy-gateway-model" }
            }
        });

        Assert.Equal(
            new[] { "gpt-5.4", "needs-auth", "legacy-gateway-model" },
            snapshots[^1].AvailableModels);
        Assert.False(snapshots[^1].ModelChoices!.Single(c => c.Id == "gpt-5.5").IsSelectable);
        Assert.True(snapshots[^1].ModelChoices!.Single(c => c.Id == "needs-auth").IsSelectable);
    }
    [Fact]
    public async Task ModelsListUpdated_DedupesDisplayNames()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4" },
                new() { Id = "gpt-5.4-mirror", Name = "GPT-5.4" },
            }
        });

        // IDs are distinct ("gpt-5.4" vs "gpt-5.4-mirror"), so both appear.
        Assert.Equal(2, snapshots[^1].AvailableModels.Length);
        Assert.Equal("gpt-5.4", snapshots[^1].AvailableModels[0]);
        Assert.Equal("gpt-5.4-mirror", snapshots[^1].AvailableModels[1]);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_PopulatesCatalogInSnapshot()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog
        {
            IsSupported = true,
            Commands = new[]
            {
                new GatewayCommand { Name = "clear", NativeName = "/clear", Category = "Session" },
                new GatewayCommand { Name = "model", NativeName = "/model", Category = "Session", AcceptsArgs = true },
            }
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.EnsureCommandCatalogAsync();

        var snap = snapshots[^1];
        Assert.True(snap.CommandsSupported);
        Assert.NotNull(snap.AvailableCommands);
        Assert.Equal(2, snap.AvailableCommands!.Count);
        Assert.Contains(snap.AvailableCommands, c => c.Name == "clear");
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_RequestsTextScopeAndExcludesNativeOnlyCommands()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        var mixedCatalog = new CommandCatalog
        {
            IsSupported = true,
            Commands = new[]
            {
                new GatewayCommand { Name = "open-native-panel", NativeName = "open-native-panel", Scope = "native" },
                new GatewayCommand { Name = "review", NativeName = "/review", Scope = "text" },
                new GatewayCommand { Name = "model", NativeName = "/model", Scope = "both" },
            }
        };
        bridge.ListCommandsBehavior = query => Task.FromResult(new CommandCatalog
        {
            IsSupported = mixedCatalog.IsSupported,
            Commands = mixedCatalog.Commands.Where(c => query?.Matches(c) ?? true).ToArray(),
        });
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.EnsureCommandCatalogAsync();

        Assert.NotNull(bridge.LastListCommandsQuery);
        Assert.Equal("text", bridge.LastListCommandsQuery!.Scope);
        var commands = snapshots[^1].AvailableCommands!;
        Assert.DoesNotContain(commands, c => c.Name == "open-native-panel");
        Assert.Contains(commands, c => c.Name == "review");
        Assert.Contains(commands, c => c.Name == "model");
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_Unsupported_FlipsSupportedFlag()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog { IsSupported = false };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.EnsureCommandCatalogAsync();

        var snap = snapshots[^1];
        // The UI renders the "unsupported" state from this flag.
        Assert.False(snap.CommandsSupported);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_Exception_PublishesUnsupportedFallback()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.ListCommandsBehavior = _ => Task.FromException<CommandCatalog>(
            new InvalidOperationException("catalog unavailable"));
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.EnsureCommandCatalogAsync();

        var snap = snapshots[^1];
        Assert.False(snap.CommandsSupported);
        Assert.NotNull(snap.AvailableCommands);
        Assert.Empty(snap.AvailableCommands!);

        await provider.EnsureCommandCatalogAsync();
        // The fallback is cached for this connection so reopening the menu does
        // not retry immediately and put the composer back into "loading".
        Assert.Equal(1, bridge.ListCommandsCallCount);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_NotConnected_DoesNotFetch()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog { IsSupported = true };
        await provider.LoadAsync();
        // Provider starts Disconnected (FakeBridge default).

        await provider.EnsureCommandCatalogAsync();

        // The command catalog is a property of the live connection; no fetch
        // should be issued while disconnected.
        Assert.Equal(0, bridge.ListCommandsCallCount);
    }

    [Fact]
    public async Task CommandCatalog_NullBeforeFetch_ThenEmptyNonNullWhenLoadedEmpty()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog { IsSupported = true };

        // Before any commands.list fetch the catalog is null so the UI can
        // distinguish "still loading" from "loaded but empty".
        var initial = await provider.LoadAsync();
        Assert.Null(initial.AvailableCommands);

        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.EnsureCommandCatalogAsync();

        var snap = snapshots[^1];
        Assert.True(snap.CommandsSupported);
        Assert.NotNull(snap.AvailableCommands);
        Assert.Empty(snap.AvailableCommands!);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_FetchesOnceThenReusesCache()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog
        {
            IsSupported = true,
            Commands = new[] { new GatewayCommand { Name = "clear", NativeName = "/clear" } }
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.EnsureCommandCatalogAsync();
        await provider.EnsureCommandCatalogAsync();

        // The catalog is cached after the first successful fetch; a second
        // palette-open does not re-hit commands.list.
        Assert.Equal(1, bridge.ListCommandsCallCount);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_RefetchesAfterReconnect()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.CommandCatalogResult = new CommandCatalog
        {
            IsSupported = true,
            Commands = new[] { new GatewayCommand { Name = "clear", NativeName = "/clear" } }
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.EnsureCommandCatalogAsync();
        Assert.Equal(1, bridge.ListCommandsCallCount);

        // Leaving Connected clears the cached catalog; the next palette-open
        // re-fetches it.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.EnsureCommandCatalogAsync();
        Assert.Equal(2, bridge.ListCommandsCallCount);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_DisconnectDuringFetch_DiscardsLateResult()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });

        // Gate the fetch so we can disconnect while it is in flight.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.ListCommandsBehavior = async _ =>
        {
            await release.Task;
            return new CommandCatalog
            {
                IsSupported = true,
                Commands = new[] { new GatewayCommand { Name = "stale", NativeName = "/stale" } }
            };
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var fetch = provider.EnsureCommandCatalogAsync();

        // Disconnect while the fetch is still awaiting — this bumps the epoch.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        // Now let the in-flight fetch complete; its result must be discarded.
        release.SetResult(true);
        await fetch;

        var snapshots2 = new List<ChatDataSnapshot>();
        provider.Changed += (_, e) => snapshots2.Add(e.Snapshot);
        // Reconnect and fetch fresh — the stale "/stale" catalog must not appear.
        bridge.ListCommandsBehavior = null;
        bridge.CommandCatalogResult = new CommandCatalog
        {
            IsSupported = true,
            Commands = new[] { new GatewayCommand { Name = "fresh", NativeName = "/fresh" } }
        };
        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.EnsureCommandCatalogAsync();

        var snap = snapshots2[^1];
        Assert.NotNull(snap.AvailableCommands);
        Assert.Single(snap.AvailableCommands!);
        Assert.Equal("fresh", snap.AvailableCommands![0].Name);
    }

    [Fact]
    public async Task EnsureCommandCatalogAsync_DisconnectBeforeDelivery_DropsStaleCommandSnapshot()
    {
        var bridge = new FakeBridge
        {
            Sessions = new[] { MainSession() },
            CurrentStatus = ConnectionStatus.Connected,
            CommandCatalogResult = new CommandCatalog
            {
                IsSupported = true,
                Commands = new[] { new GatewayCommand { Name = "stale", NativeName = "/stale" } }
            }
        };
        // Manually-pumped post queue so we can interleave a disconnect between
        // the commands.list snapshot's marshaled delivery being enqueued and run.
        var queued = new List<Action>();
        var provider = new OpenClawChatDataProvider(bridge, post: a => queued.Add(a));
        var delivered = new List<ChatDataSnapshot>();
        provider.Changed += (_, e) => delivered.Add(e.Snapshot);

        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.EnsureCommandCatalogAsync(); // enqueues the command-catalog delivery

        // Disconnect runs and is itself enqueued; drain the queue in order. The
        // command-catalog delivery re-checks the epoch and, finding it bumped by
        // the disconnect, drops itself — so the last delivered snapshot reflects
        // the disconnect (no stale commands), not "connected + /stale".
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        foreach (var action in queued.ToArray())
            action();

        Assert.NotEmpty(delivered);
        // The command-catalog delivery re-checks the epoch (bumped by the
        // disconnect) and drops itself, so no delivered snapshot ever surfaces
        // the now-stale command — regardless of marshaled delivery ordering.
        Assert.DoesNotContain(delivered, s =>
            s.AvailableCommands is { Count: > 0 } cmds && cmds.Any(c => c.Name == "stale"));
        var last = delivered[^1];
        Assert.True(last.AvailableCommands is null || last.AvailableCommands.Count == 0,
            "A stale connected+commands snapshot must not be delivered after disconnect.");

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadAsync_SeedsModelsFromBridgeSnapshot()
    {
        var bridge = new FakeBridge
        {
            Sessions = new[] { MainSession() },
            CurrentModels = new ModelsListInfo
            {
                Models = new List<ModelInfo> { new() { Id = "x", Name = "X" } }
            }
        };
        var provider = new OpenClawChatDataProvider(bridge);

        var snap = await provider.LoadAsync();

        Assert.Equal(new[] { "x" }, snap.AvailableModels);
    }

    [Fact]
    public async Task ModelsListUpdated_PopulatesProviderRichChoices()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "claude-opus-4.8", Name = "Claude Opus 4.8", Provider = "Anthropic", ContextWindow = 200000, IsDefault = true },
                new() { Id = "gemini-3.1-pro", Name = "Gemini 3.1 Pro", Provider = "Google", ContextWindow = 1000000, RequiresAuth = true },
                new() { Id = "local-llama", Provider = "Ollama", IsAvailable = false },
            }
        });

        var choices = snapshots[^1].ModelChoices;
        Assert.NotNull(choices);
        Assert.Equal(3, choices!.Count);

        Assert.Equal("claude-opus-4.8", choices[0].Id);
        Assert.Equal("Anthropic/claude-opus-4.8", choices[0].SelectionId);
        Assert.Equal("Claude Opus 4.8", choices[0].DisplayName);
        Assert.Equal("Anthropic", choices[0].Provider);
        Assert.Equal(200000, choices[0].ContextWindow);
        Assert.True(choices[0].IsDefault);

        Assert.True(choices[1].RequiresAuth);
        Assert.False(choices[2].IsAvailable);
        Assert.False(choices[2].IsSelectable);

        // AvailableModels stays a selectable id list for safe reconnect persistence.
        Assert.Equal(new[] { "claude-opus-4.8", "gemini-3.1-pro" }, snapshots[^1].AvailableModels);
    }

    [Fact]
    public async Task ModelsListUpdated_DedupesChoicesById()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4" },
                new() { Id = "gpt-5.4", Name = "GPT-5.4 (dupe)" },
                new() { Id = "", Name = "no id" },
            }
        });

        var choices = snapshots[^1].ModelChoices!;
        Assert.Single(choices);
        Assert.Equal("gpt-5.4", choices[0].Id);
        Assert.Equal("GPT-5.4", choices[0].DisplayName); // first wins
    }

    [Fact]
    public async Task ModelsListUpdated_KeepsDuplicateRawModelIdsFromDifferentProviders()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.4", Name = "GPT-5.4", Provider = "openai" },
                new() { Id = "gpt-5.4", Name = "GPT-5.4 via OpenRouter", Provider = "openrouter" },
            }
        });

        var choices = snapshots[^1].ModelChoices!;
        Assert.Equal(2, choices.Count);
        Assert.Equal("openai/gpt-5.4", choices[0].SelectionId);
        Assert.Equal("openrouter/gpt-5.4", choices[1].SelectionId);
        Assert.Equal(new[] { "gpt-5.4" }, snapshots[^1].AvailableModels);
    }

    [Fact]
    public async Task SetModelAsync_ForwardsModelToBridge()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.SetModelAsync("main", "claude-opus-4.8");

        Assert.Equal(new[] { "main" }, bridge.PatchedModelKeys);
        Assert.Equal(new[] { "claude-opus-4.8" }, bridge.PatchedModels);
    }

    [Fact]
    public async Task SetModelAsync_EmptyModel_IsNoOp_NotSent()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        // The gateway's sessions.patch schema rejects an empty model (NonEmpty
        // string); a blank Set is a no-op. Clearing goes through ClearModelAsync.
        await provider.SetModelAsync("main", "");
        await provider.SetModelAsync("main", "   ");

        Assert.Empty(bridge.PatchedModels);
        Assert.Empty(bridge.ClearedModelKeys);
    }

    [Fact]
    public async Task ClearModelAsync_ClearsOverrideViaBridge()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        // The picker's "Default" entry clears the session's model override
        // (tri-state sessions.patch null) — distinct from a Set.
        await provider.ClearModelAsync("main");

        Assert.Equal(new[] { "main" }, bridge.ClearedModelKeys);
        Assert.Empty(bridge.PatchedModels);
    }

    [Fact]
    public async Task SetThinkingLevelAsync_ForwardsConcreteLevelToBridge()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.SetThinkingLevelAsync("main", "high");

        Assert.Equal([("main", "high")], bridge.PatchedThinkingLevels);
        Assert.Empty(bridge.ClearedThinkingLevelKeys);
    }

    [Fact]
    public async Task ClearThinkingLevelAsync_ClearsOverrideViaBridge()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        await provider.ClearThinkingLevelAsync("main");

        Assert.Equal(["main"], bridge.ClearedThinkingLevelKeys);
        Assert.Empty(bridge.PatchedThinkingLevels);
    }

    [Fact]
    public async Task SendMessageAsync_WaitsForInFlightThinkingLevelClearBeforeGatewaySend()
    {
        var patchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.ClearSessionThinkingLevelBehavior = _ =>
        {
            patchStarted.TrySetResult();
            return releasePatch.Task;
        };
        await provider.LoadAsync();

        var clearTask = provider.ClearThinkingLevelAsync("main");
        await patchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var sendTask = provider.SendMessageAsync("main", "Use default reasoning");
        await Task.Delay(50);

        Assert.Empty(bridge.SentMessages);
        releasePatch.SetResult();
        await Task.WhenAll(clearTask, sendTask);

        Assert.Equal(["main"], bridge.ClearedThinkingLevelKeys);
        Assert.Equal(["Use default reasoning"], bridge.SentMessages);
    }

    [Fact]
    public async Task SendMessageAsync_WaitsForInFlightModelPatchBeforeGatewaySend()
    {
        var patchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.PatchSessionModelBehavior = (_, _) =>
        {
            patchStarted.TrySetResult();
            return releasePatch.Task;
        };
        await provider.LoadAsync();
        snapshots.Clear();

        var modelTask = provider.SetModelAsync("main", "openai/gpt-5.4");
        var sendTask = provider.SendMessageAsync("main", "Hello");
        await Task.Delay(50);

        Assert.Single(snapshots);
        Assert.Empty(bridge.SentMessages);

        await patchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releasePatch.SetResult();
        await Task.WhenAll(modelTask, sendTask);

        Assert.Equal(new[] { "openai/gpt-5.4" }, bridge.PatchedModels);
        Assert.Equal(new[] { "Hello" }, bridge.SentMessages);
    }

    [Fact]
    public async Task SendMessageAsync_ModelPatchWait_DropsDispatchAfterReconnect()
    {
        var patchStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.PatchSessionModelBehavior = (_, _) =>
        {
            patchStarted.TrySetResult();
            return releasePatch.Task;
        };
        await provider.LoadAsync();

        var modelTask = provider.SetModelAsync("main", "openai/gpt-5.4");
        await patchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var sendTask = provider.SendMessageAsync("main", "stale after reconnect");
        await Task.Delay(50);
        Assert.Empty(bridge.SentMessages);

        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.SendMessageAsync("main", "fresh after reconnect");
        Assert.Empty(bridge.SentMessages);
        releasePatch.SetResult();
        await Task.WhenAll(modelTask, sendTask);

        await WaitForConditionAsync(() => bridge.SentMessages.Count == 1);
        Assert.Equal(new[] { "fresh after reconnect" }, bridge.SentMessages);
    }

    [Fact]
    public async Task SendMessageAsync_InFlightResultAfterReconnect_EndsStaleTurn()
    {
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = async (_, _, _) =>
        {
            sendStarted.TrySetResult();
            await releaseSend.Task;
        };
        await provider.LoadAsync();

        var staleSend = provider.SendMessageAsync("main", "in flight");
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.SendBehavior = null;
        await provider.SendMessageAsync("main", "fresh after stale result");
        releaseSend.SetResult();
        await staleSend;

        await WaitForConditionAsync(() => bridge.SentMessages.Count == 2);
        Assert.Equal(
            new[] { "in flight", "fresh after stale result" },
            bridge.SentMessages);
    }

    [Fact]
    public async Task SendMessageAsync_StaleFailureAfterReconnect_DrainsFreshMessage()
    {
        var sendStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = async (_, _, _) =>
        {
            if (Interlocked.Increment(ref sendCount) != 1)
                return;
            sendStarted.TrySetResult();
            await releaseSend.Task;
            throw new InvalidOperationException("stale failure");
        };
        await provider.LoadAsync();

        var staleSend = provider.SendMessageAsync("main", "failing in flight");
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        bridge.RaiseStatus(ConnectionStatus.Connected);
        await provider.SendMessageAsync("main", "fresh after stale failure");
        releaseSend.SetResult();
        await staleSend;

        await WaitForConditionAsync(() => bridge.SentMessages.Count == 2);
        Assert.Equal(
            new[] { "failing in flight", "fresh after stale failure" },
            bridge.SentMessages);
    }

    [Fact]
    public async Task SendMessageAsync_ContinuesWhenInFlightModelPatchFails()
    {
        var patchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.PatchSessionModelBehavior = async (_, _) =>
        {
            patchStarted.SetResult();
            await releasePatch.Task;
            throw new InvalidOperationException("patch failed");
        };
        await provider.LoadAsync();
        snapshots.Clear();

        var modelTask = provider.SetModelAsync("main", "openai/gpt-5.4");
        await patchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var sendTask = provider.SendMessageAsync("main", "Hello");
        await Task.Delay(50);

        Assert.Empty(bridge.SentMessages);
        releasePatch.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => modelTask);
        await sendTask;

        Assert.Equal(new[] { "openai/gpt-5.4" }, bridge.PatchedModels);
        Assert.Equal(new[] { "Hello" }, bridge.SentMessages);
        Assert.DoesNotContain(
            snapshots.SelectMany(s => s.Timelines["main"].Entries),
            e => e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("patch failed"));
    }

    [Fact]
    public async Task ModelPatches_AreSerializedSoLatestSelectionCannotBeOvertaken()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.PatchSessionModelBehavior = (_, model) =>
        {
            if (model == "openai/gpt-5.4")
                return releaseFirst.Task;
            if (model == "openai/gpt-5.4-pro")
                secondStarted.SetResult();
            return Task.CompletedTask;
        };
        await provider.LoadAsync();

        var firstTask = provider.SetModelAsync("main", "openai/gpt-5.4");
        var secondTask = provider.SetModelAsync("main", "openai/gpt-5.4-pro");
        await Task.Delay(50);

        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(firstTask, secondTask);

        Assert.True(secondStarted.Task.IsCompleted);
        Assert.Equal(new[] { "openai/gpt-5.4", "openai/gpt-5.4-pro" }, bridge.PatchedModels);
    }


    [Fact]
    public async Task LoadHistoryAsync_CapturesPerEntryTimestamps()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Q",  State = "final", Ts = 1714600000000 },
                new ChatMessageInfo { Role = "assistant", Text = "A",  State = "final", Ts = 1714600001000 },
            }
        });
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(2, meta.Count);
        var entries = (await provider.LoadAsync()).Timelines["main"].Entries;
        var userTs = meta[entries[0].Id].Timestamp;
        var asstTs = meta[entries[1].Id].Timestamp;
        Assert.NotNull(userTs);
        Assert.NotNull(asstTs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600000000).ToLocalTime(), userTs);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600001000).ToLocalTime(), asstTs);
    }

    [Fact]
    public async Task LoadHistoryAsync_AssignsModelFromActiveSession()
    {
        var session = new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Model = "gpt-5.5" };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "user", Text = "Hi", State = "final", Ts = 1 } }
        });
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");

        var meta = provider.GetEntryMetadata("main");
        var entry = (await provider.LoadAsync()).Timelines["main"].Entries[0];
        Assert.Equal("gpt-5.5", meta[entry.Id].Model);
    }

    [Fact]
    public async Task LoadHistoryAsync_CapturesAssistantUsageMetadata()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    Role = "assistant",
                    Text = "A",
                    State = "final",
                    Ts = 1714600001000,
                    InputTokens = 10,
                    OutputTokens = 20,
                    ResponseTokens = 30,
                    ContextPercent = 4,
                },
            }
        });
        await provider.LoadAsync();

        await provider.LoadHistoryAsync("main");

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);
        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(10, meta[entry.Id].InputTokens);
        Assert.Equal(20, meta[entry.Id].OutputTokens);
        Assert.Equal(30, meta[entry.Id].ResponseTokens);
        Assert.Equal(4, meta[entry.Id].ContextPercent);
    }

    [Fact]
    public async Task SendMessageAsync_AssignsTimestampToLocalUserEntry()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var before = DateTimeOffset.Now.AddSeconds(-1);
        await provider.SendMessageAsync("main", "hi");
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "hi",
            State = "final"
        });
        var after = DateTimeOffset.Now.AddSeconds(1);

        var snap = await provider.LoadAsync();
        var entry = snap.Timelines["main"].Entries[0];
        var meta = provider.GetEntryMetadata("main");
        Assert.True(meta.TryGetValue(entry.Id, out var m) && m.Timestamp.HasValue);
        Assert.InRange(m!.Timestamp!.Value, before, after);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantFinal_AssignsMetadata()
    {
        var session = new SessionInfo { Key = "main", IsMain = true, DisplayName = "Main", Model = "claude-sonnet-4.6" };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final",
            Ts = 1714600005000
        });

        var snap = await provider.LoadAsync();
        var entry = snap.Timelines["main"].Entries[0];
        var meta = provider.GetEntryMetadata("main");
        Assert.True(meta.TryGetValue(entry.Id, out var m));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714600005000).ToLocalTime(), m!.Timestamp);
        Assert.Equal("claude-sonnet-4.6", m.Model);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantFinal_MergesUsageMetadataOntoStreamingEntry()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "partial",
            State = "delta",
            Ts = 1714600005000
        });

        var firstSnap = await provider.LoadAsync();
        var entryId = Assert.Single(firstSnap.Timelines["main"].Entries).Id;

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600006000,
            InputTokens = 12,
            OutputTokens = 34,
            ResponseTokens = 46,
            ContextPercent = 7
        });

        var snap = await provider.LoadAsync();
        var entry = Assert.Single(snap.Timelines["main"].Entries);
        Assert.Equal(entryId, entry.Id);
        Assert.Equal("final", entry.Text);

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(12, meta[entry.Id].InputTokens);
        Assert.Equal(34, meta[entry.Id].OutputTokens);
        Assert.Equal(46, meta[entry.Id].ResponseTokens);
        Assert.Equal(7, meta[entry.Id].ContextPercent);
    }

    [Fact]
    public async Task ChatMessageReceived_AssistantFinal_AccumulatesUsageAcrossAssistantMessages()
    {
        var session = new SessionInfo
        {
            Key = "main",
            IsMain = true,
            DisplayName = "Main session",
            Status = "active",
            ContextTokens = 144_000,
        };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "first",
            State = "final",
            Ts = 1714600005000,
            InputTokens = 20,
            OutputTokens = 7,
            ResponseTokens = 27,
        });

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "again",
            State = "final",
            Ts = 1714600005500,
        });

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "second",
            State = "final",
            Ts = 1714600006000,
            InputTokens = 20,
            OutputTokens = 5,
            ResponseTokens = 25,
        });

        var entries = (await provider.LoadAsync()).Timelines["main"].Entries
            .Where(e => e.Kind == ChatTimelineItemKind.Assistant)
            .ToArray();
        Assert.Equal(2, entries.Length);

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(27, meta[entries[0].Id].ResponseTokens);
        Assert.Equal(52, meta[entries[1].Id].ResponseTokens);
        Assert.Equal(144_000, meta[entries[1].Id].ContextTokens);
    }

    [Fact]
    public async Task ChatMessageReceived_DuplicateAssistantFinal_DoesNotDoubleAccumulateUsage()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var message = new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "same",
            State = "final",
            Ts = 1714600005000,
            InputTokens = 20,
            OutputTokens = 7,
            ResponseTokens = 27,
        };

        bridge.RaiseChat(message);
        bridge.RaiseChat(message);

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);
        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(27, meta[entry.Id].ResponseTokens);
    }

    [Fact]
    public async Task SessionsUpdated_SnapshotsThreadUsageOntoLatestAssistantWithoutOwnUsage()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600005000
        });

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);

        bridge.RaiseSessions(new[]
        {
            new SessionInfo
            {
                Key = "main",
                IsMain = true,
                DisplayName = "Main session",
                InputTokens = 200,
                OutputTokens = 50,
                TotalTokens = 999,
                ContextTokens = 5_000,
            }
        });

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(999, meta[entry.Id].ResponseTokens);
        Assert.Equal(5_000, meta[entry.Id].ContextTokens);
    }

    [Fact]
    public async Task SessionsUpdated_OverwritesLatestAssistantResponseUsageWithCumulativeUsage()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600005000,
            InputTokens = 1_000,
            OutputTokens = 500,
            ResponseTokens = 1_500,
        });

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);

        bridge.RaiseSessions(new[]
        {
            new SessionInfo
            {
                Key = "main",
                IsMain = true,
                DisplayName = "Main session",
                InputTokens = 20_000,
                OutputTokens = 3_700,
                TotalTokens = 23_500,
                ContextTokens = 400_000,
            }
        });

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(23_500, meta[entry.Id].ResponseTokens);
        Assert.Equal(400_000, meta[entry.Id].ContextTokens);
    }

    [Fact]
    public async Task SessionsUpdated_DoesNotLowerExistingAssistantUsageSnapshot()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600005000,
            InputTokens = 5_000,
            OutputTokens = 1_900,
            ResponseTokens = 6_900,
        });

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);

        bridge.RaiseSessions(new[]
        {
            new SessionInfo
            {
                Key = "main",
                IsMain = true,
                DisplayName = "Main session",
                InputTokens = 3_000,
                OutputTokens = 1_100,
                ContextTokens = 400_000,
            }
        });

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(6_900, meta[entry.Id].ResponseTokens);
        Assert.Equal(400_000, meta[entry.Id].ContextTokens);

        bridge.RaiseSessions(new[]
        {
            new SessionInfo
            {
                Key = "main",
                IsMain = true,
                DisplayName = "Main session",
                InputTokens = 3_000,
                OutputTokens = 1_100,
                ContextTokens = 400_000,
            }
        });

        meta = provider.GetEntryMetadata("main");
        Assert.Equal(6_900, meta[entry.Id].ResponseTokens);
        Assert.Equal(400_000, meta[entry.Id].ContextTokens);
    }

    [Fact]
    public async Task SessionsUpdated_SnapshotsMainSessionUsageOntoLegacyMainTimeline()
    {
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600005000,
            InputTokens = 20,
            OutputTokens = 6,
            ResponseTokens = 26,
        });

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);

        bridge.RaiseSessions(new[]
        {
            new SessionInfo
            {
                Key = "agent:main:main",
                IsMain = true,
                DisplayName = "Main session",
                InputTokens = 1_000,
                OutputTokens = 100,
                ContextTokens = 144_000,
            }
        });

        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(1_100, meta[entry.Id].ResponseTokens);
        Assert.Equal(144_000, meta[entry.Id].ContextTokens);
    }

    [Fact]
    public async Task ChatMessageReceived_Final_SnapshotsExistingThreadUsageOntoAssistant()
    {
        var session = new SessionInfo
        {
            Key = "main",
            IsMain = true,
            DisplayName = "Main session",
            Status = "active",
            InputTokens = 200,
            OutputTokens = 50,
            TotalTokens = 999,
            ContextTokens = 5_000,
        };
        var (bridge, provider, _, _) = CreateProvider(new[] { session });
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "final",
            State = "final",
            Ts = 1714600005000
        });

        var entry = Assert.Single((await provider.LoadAsync()).Timelines["main"].Entries);
        var meta = provider.GetEntryMetadata("main");
        Assert.Equal(999, meta[entry.Id].ResponseTokens);
        Assert.Equal(5_000, meta[entry.Id].ContextTokens);
    }

    [Fact]
    public async Task GetEntryMetadata_MissingThread_ReturnsEmpty()
    {
        var (_, provider, _, _) = CreateProvider();
        await provider.LoadAsync();

        var meta = provider.GetEntryMetadata("nonexistent");

        Assert.NotNull(meta);
        Assert.Empty(meta);
    }

    [Fact]
    public async Task GetEntryMetadata_ReturnsDefensiveCopy()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        await provider.SendMessageAsync("main", "hi");

        var snapshot1 = (Dictionary<string, ChatEntryMetadata>)provider.GetEntryMetadata("main");
        var initialCount = snapshot1.Count;
        snapshot1.Clear();   // mutate the returned copy

        var snapshot2 = provider.GetEntryMetadata("main");
        Assert.Equal(initialCount, snapshot2.Count);
    }

    [Fact]
    public async Task LoadHistoryAsync_AfterLiveActivity_DoesNotDuplicateEntries()
    {
        // Regression for HIGH 2: prior to the dedup fix, a live assistant
        // message that was later included in chat.history would appear twice
        // in the rebuilt timeline (once from the rebuild, once from the
        // append-prior step), and ID collisions could occur because both
        // sequences reused e1, e2, …
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hi",     State = "final", Ts = nowMs },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final", Ts = nowMs + 1000 }
            }
        });
        await provider.LoadAsync();

        // Simulate live activity arriving before history finishes loading:
        // a live assistant frame for the same content (within 5s of the
        // history timestamp). After history loads, this should be deduped.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Hello!",
            State = "final",
            Ts = nowMs + 1000
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Equal(2, timeline.Entries.Count);
        Assert.Equal("Hi", timeline.Entries[0].Text);
        Assert.Equal("Hello!", timeline.Entries[1].Text);

        // IDs must be unique even after the append step.
        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_AfterLiveActivity_PreservesNonDuplicateLiveEntries()
    {
        // Live status entries (e.g. an "Aborted" warning) that the gateway
        // doesn't replay in history should be preserved after history load,
        // and re-IDed when their original IDs collide with the rebuilt set.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user",      Text = "Hi",     State = "final", Ts = nowMs },
                new ChatMessageInfo { Role = "assistant", Text = "Hello!", State = "final", Ts = nowMs + 1000 }
            }
        });
        await provider.LoadAsync();

        // A live event the history will NOT carry — must survive the rebuild.
        bridge.RaiseAgent(MakeAgentEvent(
            "lifecycle",
            "{\"phase\":\"error\",\"message\":\"net glitch\"}",
            runId: "run"));

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status && e.Text.Contains("net glitch"));

        // All entry IDs unique post-rebuild.
        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_WithMissingTimestamps_PreservesAllLiveEntries()
    {
        // Rubber-duck round 2: when the rebuilt history entry has no
        // timestamp (msg.Ts == 0), we must NOT dedupe a live entry against
        // it on text alone — silent transcript loss is worse than visible
        // duplication. The previous fingerprint logic collapsed all such
        // entries into a single bucket=0 slot and dropped the second.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                // Ts deliberately omitted (= 0) on both rebuilt entries.
                new ChatMessageInfo { Role = "user",      Text = "ok", State = "final" },
                new ChatMessageInfo { Role = "assistant", Text = "ok", State = "final" }
            }
        });
        await provider.LoadAsync();

        // A live assistant frame for "ok" arrives before history loads.
        // Live entries always carry a non-zero Now timestamp, but the
        // rebuilt side has Ts=0 → dedup must NOT match.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final"
            // Ts not set
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        // Two assistant "ok" entries must survive: one from history, one live.
        var oks = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "ok");
        Assert.Equal(2, oks);

        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task LoadHistoryAsync_WithSameTextDifferentTimestamps_PreservesBoth()
    {
        // Rubber-duck round 2: even with valid timestamps, two genuinely
        // distinct events with the same text should NOT collide once the
        // gap exceeds the 2-second tolerance window.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "assistant", Text = "ok", State = "final", Ts = nowMs }
            }
        });
        await provider.LoadAsync();

        // Live assistant message with the same text but 10 s later.
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "ok",
            State = "final",
            Ts = nowMs + 10_000
        });

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        var oks = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Assistant && e.Text == "ok");
        Assert.Equal(2, oks);

        var ids = timeline.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Disconnect_DuringActiveTurn_InjectsInterruptionAndEndsTurn()
    {
        // Rubber-duck round 2 / MEDIUM 5: when the connection drops while
        // a turn is in flight we must synthesize a Status entry +
        // ChatTurnEndEvent so the UI doesn't sit "thinking" forever.
        var sendGate = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        // Establish Connected baseline so the next status change registers
        // as a Connected → Disconnected transition.
        bridge.RaiseStatus(ConnectionStatus.Connected);

        // Start a turn that never completes.
        var sendTask = provider.SendMessageAsync("main", "hi");
        Assert.True(snapshots[^1].Timelines["main"].TurnActive);

        snapshots.Clear();

        // Connection drops while turn is active.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.False(timeline.TurnActive);
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));

        // Count interruption entries before any further events.
        var beforeCount = timeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));
        Assert.Equal(1, beforeCount);

        // Subsequent unrelated events on the thread must not re-trigger
        // the interruption (status is already Disconnected, no transition).
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "late frame",
            State = "final"
        });

        var afterTimeline = snapshots[^1].Timelines["main"];
        var afterCount = afterTimeline.Entries.Count(e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("Chat_Notification_ConnectionInterrupted"));
        Assert.Equal(1, afterCount);

        // Allow the in-flight send to complete so the test can finish.
        sendGate.SetResult();
        await sendTask;
    }

    // ── chat rubber-duck MEDIUM 2: live System (untrusted) / toolresult ──

    [Fact]
    public async Task OnChatMessageReceived_LiveToolResult_RendersAsToolChip()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "toolresult",
            Text = "drwxr-xr-x  3 root root\nProcess exited with code 0",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Contains("Process exited", entry.ToolOutput ?? "");
        Assert.Equal(ChatToolIdentityStrength.Specific, entry.ToolIdentityStrength);
        // Must NOT have rendered as a normal assistant bubble.
        Assert.DoesNotContain(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant);
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveToolResult_AlternateRoleSpelling_AlsoRenders()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "tool_result",
            Text = "Exec completed (exit=0)",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.ToolCall);
        Assert.Contains("Exec completed", entry.ToolOutput ?? "");
        Assert.Equal(ChatToolIdentityStrength.Heuristic, entry.ToolIdentityStrength);
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveUserSystemNote_RendersAsStatus()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "System (untrusted): exec result for tool_call_42 follows",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        // Must render as a dim Status entry (provenance preserved), NOT
        // dropped silently and NOT shown as a real user bubble.
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text.Contains("System (untrusted)"));
        Assert.DoesNotContain(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User);
    }

    [Fact]
    public async Task OnChatMessageReceived_LiveUserPlain_ShownAsRemoteUser()
    {
        // After the cross-client sync fix, non-echo user messages from SSE
        // (e.g. sent from gateway web UI) should appear in the timeline.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "USER",
            Text = "hello there",
            State = "final"
        });

        Assert.Single(snapshots);
        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.User, entry.Kind);
        Assert.Equal("hello there", entry.Text);
    }

    [Fact]
    public async Task OnChatMessageReceived_MediaOnlyAssistant_CreatesSafePresentationEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = string.Empty,
            State = "final",
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.LegacyDirective,
                        FileName = "banner.png",
                    },
                },
            ],
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries);
        Assert.Equal(ChatTimelineItemKind.Assistant, entry.Kind);
        Assert.Equal(string.Empty, entry.Text);
        var metadata = provider.GetEntryMetadata("main");
        var media = Assert.Single(metadata[entry.Id].AssistantContent!.Media);
        Assert.Equal("banner.png", media.DisplayName);
    }

    [Fact]
    public async Task OnChatMessageReceived_FinalMedia_MergesIntoStreamingAssistantEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Rendering",
            State = "delta",
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = "Rendering complete",
            State = "final",
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.Structured,
                        ArtifactId = "artifact-1",
                        FileName = "banner.png",
                    },
                },
            ],
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, item => item.Kind == ChatTimelineItemKind.Assistant);
        Assert.Equal("Rendering complete", entry.Text);
        Assert.False(entry.IsStreaming);
        var media = Assert.Single(provider.GetEntryMetadata("main")[entry.Id].AssistantContent!.Media);
        Assert.Equal("artifact-1", media.Reference.ArtifactId);
    }

    [Fact]
    public async Task OnChatMessageReceived_StructuredFollowUp_PreservesLegacyMediaReference()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = string.Empty,
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.LegacyDirective,
                        FileName = "banner.png",
                    },
                },
            ],
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = string.Empty,
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.Structured,
                        ArtifactId = "artifact-unavailable",
                    },
                },
            ],
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, item => item.Kind == ChatTimelineItemKind.Assistant);
        var media = Assert.Single(provider.GetEntryMetadata("main")[entry.Id].AssistantContent!.Media);
        Assert.Equal(ChatMediaContentSource.LegacyDirective, media.Reference.Source);
        Assert.Equal("banner.png", media.DisplayName);
    }

    [Fact]
    public async Task OnChatMessageReceived_IdentifiedMediaOnlyRetransmit_DoesNotDuplicateEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();
        var message = new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = string.Empty,
            State = "final",
            OpenClawId = "assistant-media-1",
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.LegacyDirective,
                        FileName = "banner.png",
                    },
                },
            ],
        };

        bridge.RaiseChat(message);
        bridge.RaiseChat(message);

        var timeline = snapshots[^1].Timelines["main"];
        Assert.Single(timeline.Entries, item => item.Kind == ChatTimelineItemKind.Assistant);
    }

    [Fact]
    public async Task LoadHistoryAsync_StructuredMedia_CreatesSafePresentationEntry()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "assistant",
                    ContentParts =
                    [
                        new ChatMessageContentPartInfo
                        {
                            Kind = ChatMessageContentPartKind.Media,
                            Media = new ChatMediaContentInfo
                            {
                                Kind = ChatMediaContentKind.Image,
                                Source = ChatMediaContentSource.Structured,
                                ArtifactId = "artifact-1",
                                FileName = "banner.png",
                            },
                        },
                    ],
                },
            ],
        });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        var timeline = snapshots[^1].Timelines["main"];
        var entry = Assert.Single(timeline.Entries, item => item.Kind == ChatTimelineItemKind.Assistant);
        var media = Assert.Single(provider.GetEntryMetadata("main")[entry.Id].AssistantContent!.Media);
        Assert.Equal("banner.png", media.DisplayName);
        Assert.Equal("artifact-1", media.Reference.ArtifactId);
    }

    // ── chat rubber-duck MEDIUM 4: per-message size cap ──

    [Fact]
    public async Task OnChatMessageReceived_OversizedContent_IsTruncated()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        // 300 KiB of ASCII — 1 byte per char in UTF-8.
        var huge = new string('A', 300 * 1024);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "assistant",
            Text = huge,
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var entry = timeline.Entries.Single(e => e.Kind == ChatTimelineItemKind.Assistant);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(entry.Text);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes,
            $"entry was {bytes} bytes; cap is {OpenClawChatDataProvider.MaxEntryTextBytes}");
        Assert.Contains("bytes truncated", entry.Text);
        Assert.True(entry.Text.Length < huge.Length);
    }

    [Fact]
    public void TruncateForChatEntry_BelowCap_ReturnsInputUnchanged()
    {
        var small = "hello world";
        Assert.Same(small, OpenClawChatDataProvider.TruncateForChatEntry(small));
    }

    [Fact]
    public void TruncateForChatEntry_AboveCap_RespectsByteCap()
    {
        var big = new string('Z', OpenClawChatDataProvider.MaxEntryTextBytes + 50_000);
        var truncated = OpenClawChatDataProvider.TruncateForChatEntry(big);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);
        Assert.EndsWith("bytes truncated]", truncated);
    }

    [Fact]
    public void TruncateForChatEntry_DoesNotSplitSurrogatePair()
    {
        // String of repeated 4-byte UTF-8 emoji that crosses the cap
        // boundary. The truncate must not hang and must not return a string
        // whose last char before the marker is an unpaired high surrogate.
        const string emoji = "\uD83D\uDE00"; // 😀 (U+1F600)
        var sb = new System.Text.StringBuilder(OpenClawChatDataProvider.MaxEntryTextBytes);
        for (var i = 0; i < OpenClawChatDataProvider.MaxEntryTextBytes / 4 + 10; i++)
            sb.Append(emoji);
        var truncated = OpenClawChatDataProvider.TruncateForChatEntry(sb.ToString());

        var bytes = System.Text.Encoding.UTF8.GetByteCount(truncated);
        Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);

        var insertedAt = truncated.IndexOf(" … [", StringComparison.Ordinal);
        Assert.True(insertedAt > 0);
        Assert.False(char.IsHighSurrogate(truncated[insertedAt - 1]));
    }

    [Fact]
    public async Task OnAgentEvent_OversizedToolOutput_IsTruncated()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("tool",
            """{"phase":"start","name":"powershell","args":{"command":"ls"}}"""));

        var huge = new string('B', 400 * 1024);
        bridge.RaiseAgent(MakeAgentEvent("command_output",
            JsonSerializer.Serialize(new { output = huge })));

        var timeline = snapshots[^1].Timelines["main"];
        var output = timeline.Entries.LastOrDefault(e => e.Kind == ChatTimelineItemKind.ToolCall);
        if (output?.ToolOutput is { } body)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
            Assert.True(bytes <= OpenClawChatDataProvider.MaxEntryTextBytes);
        }
    }

    [Fact]
    public async Task StopResponseAsync_FailedAbort_ClearsSuppression()
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider(new[] { MainSession() });
        bridge.AbortBehavior = _ => throw new Exception("Network error");
        await provider.LoadAsync();
        snapshots.Clear();

        // Send a message to get a turn active
        await provider.SendMessageAsync("main", "Hello");
        // Simulate lifecycle.start with a runId
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", "main", "run-1"));

        // Now stop — the abort will fail
        await provider.StopResponseAsync("main");

        // The failed abort should generate an error notification
        Assert.Contains(notifications, n => n.Kind == ChatProviderNotificationKind.Error);

        // Crucially: sending a new message should work (thread not permanently suppressed)
        snapshots.Clear();
        await provider.SendMessageAsync("main", "Try again");
        Assert.True(snapshots.Count > 0, "Sending after failed abort should succeed");
        Assert.Contains(bridge.SentMessages, m => m == "Try again");
    }

    [Fact]
    public async Task SendMessageAsync_ClearsPendingAbortCounts()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        // Send a message
        await provider.SendMessageAsync("main", "First");
        // Stop before lifecycle.start (creates a pending abort)
        await provider.StopResponseAsync("main");

        // Now send another message — this should clear pending aborts
        await provider.SendMessageAsync("main", "Second");

        // Simulate lifecycle.start arriving for the second message
        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", "main", "run-2"));

        // The pending abort should NOT have fired (cleared by second send)
        Assert.DoesNotContain("run-2", bridge.AbortedRunIds);
    }

    [Fact]
    public async Task ChatMessageReceived_LocalMediaEcho_ReconcilesOneCleanRowAndPreservesLocalPreview()
    {
        var sendGate = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        _ = provider.SendMessageAsync("main", "Describe this", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
                Content = Convert.ToBase64String([1, 2, 3]),
                SizeBytes = 3,
            },
        ]);
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "[media attached: media://inbound/f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png (image/png)]\nDescribe this",
            State = "final",
            OpenClawId = "gateway-media",
            OpenClawSeq = 12,
        });

        var timeline = Assert.Single(snapshots).Timelines["main"];
        var user = Assert.Single(timeline.Entries, entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Describe this", user.Text);
        Assert.DoesNotContain("media://", user.Text);
        var metadata = provider.GetEntryMetadata("main")[user.Id];
        Assert.Equal("gateway-media", metadata.GatewayMessageId);
        var attachment = Assert.Single(metadata.Attachments!);
        Assert.Equal(ChatAttachmentOrigin.Local, attachment.Origin);
        Assert.Equal(
            "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
            attachment.DisplayFileName);
        Assert.True(attachment.CanAccessPreviewCache);
        Assert.True(ChatImagePreviewCache.Contains(attachment.PreviewCacheKey!));

        sendGate.SetResult();
    }

    [Fact]
    public async Task ChatMessageReceived_AttachmentOnlyMediaEcho_ReconcilesWithoutEmptyTextMatching()
    {
        var sendGate = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        _ = provider.SendMessageAsync("main", "", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
                Content = Convert.ToBase64String([1]),
                SizeBytes = 1,
            },
        ]);
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "[media attached: media://inbound/f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png (image/png)]",
            State = "final",
            OpenClawId = "gateway-only",
        });

        var user = Assert.Single(Assert.Single(snapshots).Timelines["main"].Entries);
        Assert.Equal(string.Empty, user.Text);
        var metadata = provider.GetEntryMetadata("main")[user.Id];
        Assert.Equal("gateway-only", metadata.GatewayMessageId);
        Assert.Equal(
            "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
            Assert.Single(metadata.Attachments!).DisplayFileName);
        sendGate.SetResult();
    }

    [Fact]
    public async Task SessionResetCompletion_MediaEchoUsesCorrelationSignatureAcrossResetGate()
    {
        var sendGate = new TaskCompletionSource();
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.SendBehavior = (_, _, _) => sendGate.Task;
        await provider.LoadAsync();

        _ = provider.SendMessageAsync("main", "before reset", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
                Content = Convert.ToBase64String([1]),
            },
        ]);
        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main",
        });
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "[media attached: media://inbound/f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png (image/png)]\nbefore reset",
            Ts = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
        });

        Assert.Empty((await provider.LoadAsync()).Timelines["main"].Entries);
        sendGate.SetResult();

        await provider.SendMessageAsync("main", "after reset", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "after.png",
                Content = Convert.ToBase64String([2]),
            },
        ]);
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "[media attached: media://inbound/after---7f122605-290a-467c-a5df-8a744c093004.png (image/png)]\nafter reset",
            Ts = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds(),
            OpenClawId = "after-reset-media",
        });

        var user = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("after reset", user.Text);
        Assert.Equal("after-reset-media", provider.GetEntryMetadata("main")[user.Id].GatewayMessageId);
    }

    [Fact]
    public async Task RemoteLifecycleBackfill_MediaEnvelopeBecomesCleanStructuredUserRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "[media attached: media://inbound/remote.pdf (application/pdf)]\nReview it",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            ],
        });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "remote-media-run"));
        for (var i = 0; i < 50 && snapshots.Count == 0; i++)
            await Task.Delay(10);

        var user = Assert.Single(snapshots[^1].Timelines["main"].Entries, entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Review it", user.Text);
        var attachment = Assert.Single(provider.GetEntryMetadata("main")[user.Id].Attachments!);
        Assert.Equal(ChatAttachmentOrigin.GatewayReference, attachment.Origin);
        Assert.Equal("remote.pdf", attachment.DisplayFileName);
        Assert.Null(attachment.PreviewCacheKey);
    }

    [Fact]
    public async Task RemoteLifecycleBackfill_WhenLiveFrameArrivesLater_MergesOneUserRow()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    SessionKey = "main",
                    Role = "user",
                    Text = "race",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    OpenClawId = "race-user",
                    OpenClawSeq = 73,
                },
            ],
        });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseAgent(MakeAgentEvent("lifecycle", """{"phase":"start"}""", runId: "race-run"));
        for (var i = 0; i < 50; i++)
        {
            if (snapshots.Count > 0 &&
                snapshots[^1].Timelines["main"].Entries.Any(entry =>
                    entry.Kind == ChatTimelineItemKind.User && entry.Text == "race"))
            {
                break;
            }
            await Task.Delay(10);
        }
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "[media attached: media://inbound/race.png (image/png)]\nrace",
            State = "final",
            OpenClawId = "race-user",
            OpenClawSeq = 73,
        });

        var latest = snapshots[^1].Timelines["main"];
        var user = Assert.Single(latest.Entries, entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal("race", user.Text);
        Assert.Equal(
            "race.png",
            Assert.Single(provider.GetEntryMetadata("main")[user.Id].Attachments!).DisplayFileName);
    }

    [Fact]
    public async Task ChatMessageReceived_RepeatedSameTextWithDistinctGatewayIdentityKeepsBothTurns()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "repeat",
            State = "final",
            OpenClawId = "repeat-1",
            OpenClawSeq = 1,
        });
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "repeat",
            State = "final",
            OpenClawId = "repeat-2",
            OpenClawSeq = 2,
        });

        var users = snapshots[^1].Timelines["main"].Entries
            .Where(entry => entry.Kind == ChatTimelineItemKind.User && entry.Text == "repeat")
            .ToArray();
        Assert.Equal(2, users.Length);
        var metadata = provider.GetEntryMetadata("main");
        Assert.Equal(
            new[] { "repeat-1", "repeat-2" },
            users.Select(user => metadata[user.Id].GatewayMessageId).ToArray());
    }

    [Fact]
    public async Task LoadHistoryAsync_GatewayMediaUsesLocalSidecarPrecedenceWithoutRestoringBytes()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (_, sender, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await sender.LoadAsync();
        await sender.SendMessageAsync("main", "history caption", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
                Content = Convert.ToBase64String([1, 2]),
            },
        ]);

        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "[media attached: media://inbound/f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png (image/png)]\nhistory caption",
                    Ts = sentTs,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var user = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal("history caption", user.Text);
        var attachment = Assert.Single(provider.GetEntryMetadata("main")[user.Id].Attachments!);
        Assert.Equal(ChatAttachmentOrigin.Local, attachment.Origin);
        Assert.Equal(
            "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png",
            attachment.DisplayFileName);
        Assert.Null(attachment.PreviewCacheKey);
    }

    [Fact]
    public async Task LoadHistoryAsync_RemoteMediaEnvelopeFallsBackToGatewayReference()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = _ => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "[media attached: media://inbound/report.pdf (application/pdf)]",
                    Ts = 123,
                },
            ],
        });

        await provider.LoadHistoryAsync("main");

        var user = Assert.Single(snapshots[^1].Timelines["main"].Entries);
        Assert.Equal(string.Empty, user.Text);
        var attachment = Assert.Single(provider.GetEntryMetadata("main")[user.Id].Attachments!);
        Assert.Equal(ChatAttachmentOrigin.GatewayReference, attachment.Origin);
        Assert.Equal("report.pdf", attachment.DisplayFileName);
        Assert.Null(attachment.PreviewCacheKey);
    }

    [Fact]
    public async Task SendMessageAsync_WithAttachment_SendsThroughInterface()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var attachment = new ChatAttachment
        {
            Type = "file",
            MimeType = "text/plain",
            FileName = "test.txt",
            Content = Convert.ToBase64String(new byte[] { 72, 101, 108, 108, 111 }),
            SizeBytes = 5
        };

        await provider.SendMessageAsync("main", "Check this", default, new[] { attachment });

        Assert.Contains(bridge.SentMessages, m => m == "Check this");
        var sentAttachment = Assert.Single(bridge.SentAttachments);
        Assert.NotNull(sentAttachment);
        Assert.Same(attachment, sentAttachment![0]);
        var optimistic = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Check this", optimistic.Text);
        Assert.Equal(
            "test.txt",
            Assert.Single(provider.GetEntryMetadata("main")[optimistic.Id].Attachments!).DisplayFileName);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "Check this",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var userEntry = timeline.Entries.Last(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Check this", userEntry.Text);
    }

    [Fact]
    public async Task SendMessageAsync_WithMultipleAttachments_SendsAndRendersAllMarkers()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        var fileAttachment = new ChatAttachment
        {
            Type = "file",
            MimeType = "text/plain",
            FileName = "notes.txt",
            Content = Convert.ToBase64String(new byte[] { 1 }),
            SizeBytes = 1
        };
        var imageAttachment = new ChatAttachment
        {
            Type = "image",
            MimeType = "image/png",
            FileName = "diagram.png",
            Content = Convert.ToBase64String(new byte[] { 2, 3 }),
            SizeBytes = 2
        };

        await provider.SendMessageAsync("main", "See both", default, new[] { fileAttachment, imageAttachment });

        var sentAttachments = Assert.Single(bridge.SentAttachments);
        Assert.NotNull(sentAttachments);
        Assert.Collection(
            sentAttachments!,
            a => Assert.Same(fileAttachment, a),
            a => Assert.Same(imageAttachment, a));
        var optimistic = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("See both", optimistic.Text);
        Assert.Equal(2, provider.GetEntryMetadata("main")[optimistic.Id].Attachments!.Count);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "See both",
            State = "final"
        });

        var timeline = snapshots[^1].Timelines["main"];
        var userEntry = timeline.Entries.Last(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("See both", userEntry.Text);
    }

    [Fact]
    public async Task SendMessageAsync_OversizedMediaCaptionUsesSameBoundedEchoText()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        var caption = new string('x', OpenClawChatDataProvider.MaxEntryTextBytes + 50_000);

        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-large", Status = "started" });
        await provider.SendMessageAsync("main", caption, default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "large.png",
                Content = Convert.ToBase64String([1]),
                SizeBytes = 1,
            },
        ]);

        var optimistic = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(optimistic.Text) <=
            OpenClawChatDataProvider.MaxEntryTextBytes);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = $"[media attached: media://inbound/large.png (image/png)]\n{caption}",
            State = "final",
            OpenClawId = "large-echo",
            OpenClawSeq = 1,
        });

        var user = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal("large-echo", provider.GetEntryMetadata("main")[user.Id].GatewayMessageId);
    }

    [Fact]
    public async Task AttachmentMetadata_PersistsAndRehydratesFromHistory()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "Check this", default, new[]
        {
            new ChatAttachment
            {
                Type = "file",
                MimeType = "text/plain",
                FileName = "test.txt",
                Content = Convert.ToBase64String(new byte[] { 72, 105 }),
                SizeBytes = 2
            }
        });

        var (bridge2, provider2, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "[media attached: media://inbound/test.txt (text/plain)]\nCheck this",
                    State = "final",
                    Ts = sentTs,
                }
            }
        });

        await provider2.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("Check this", userEntry.Text);
        Assert.Equal(
            "test.txt",
            Assert.Single(provider2.GetEntryMetadata("main")[userEntry.Id].Attachments!).DisplayFileName);
    }

    [Fact]
    public async Task AttachmentMetadata_IdentitylessRewrittenLiveEchoDoesNotDuplicateRehydratedHistory()
    {
        const string localFileName =
            "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png";
        const string gatewayFileName =
            "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png";
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "Check this", default,
        [
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = localFileName,
                Content = Convert.ToBase64String([1, 2, 3]),
                SizeBytes = 3,
            },
        ]);

        var gatewayText =
            $"[media attached: media://inbound/{gatewayFileName} (image/png)]\nCheck this";
        var (bridge2, provider2, snapshots, _) = CreateProvider(
            new[] { MainSession() },
            toolPath,
            attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages =
            [
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = gatewayText,
                    State = "final",
                    Ts = sentTs,
                },
            ],
        });

        await provider2.LoadHistoryAsync("main");
        bridge2.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = gatewayText,
            State = "final",
            Ts = sentTs,
        });

        var user = Assert.Single(
            snapshots[^1].Timelines["main"].Entries,
            entry => entry.Kind == ChatTimelineItemKind.User);
        Assert.Equal(
            localFileName,
            Assert.Single(provider2.GetEntryMetadata("main")[user.Id].Attachments!).DisplayFileName);
    }

    [Fact]
    public async Task AttachmentMetadata_ConcurrentSavesPreserveEveryCompletedSend()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var firstSnapshotCaptured = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstSnapshot = new ManualResetEventSlim();
        using var store = new ChatMetadataStore(
            toolPath,
            attachmentPath,
            version =>
            {
                if (version != 1)
                    return;

                firstSnapshotCaptured.SetResult();
                releaseFirstSnapshot.Wait();
            });

        static ChatAttachment Attachment(string fileName) => new()
        {
            Type = "image",
            MimeType = "image/png",
            FileName = fileName,
            Content = Convert.ToBase64String([1]),
            SizeBytes = 1,
        };

        var firstSave = Task.Run(() =>
            store.CacheAttachments(
                "thread-1",
                "session-1",
                0,
                "first",
                [Attachment("first.png")],
                1_000));
        try
        {
            await firstSnapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.CacheAttachments(
                "thread-2",
                "session-2",
                0,
                "second",
                [Attachment("second.png")],
                2_000);
        }
        finally
        {
            releaseFirstSnapshot.Set();
        }

        await firstSave;

        var cache = JsonSerializer.Deserialize<
            Dictionary<string, List<ChatMetadataStore.CachedAttachmentMeta>>>(
                await File.ReadAllTextAsync(attachmentPath));
        Assert.Equal("first.png", Assert.Single(cache!["session-1"]).Attachments[0].FileName);
        Assert.Equal("second.png", Assert.Single(cache["session-2"]).Attachments[0].FileName);
    }

    [Fact]
    public async Task AttachmentMetadata_PersistsAndRehydratesMultipleAttachments()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "See both", default, new[]
        {
            new ChatAttachment
            {
                Type = "file",
                MimeType = "text/plain",
                FileName = "notes.txt",
                Content = Convert.ToBase64String(new byte[] { 1 }),
                SizeBytes = 1
            },
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "diagram.png",
                Content = Convert.ToBase64String(new byte[] { 2, 3 }),
                SizeBytes = 2
            }
        });

        var (bridge2, provider2, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "[media attached: media://inbound/notes.txt (text/plain)]\n" +
                        "[media attached: media://inbound/diagram.png (image/png)]\nSee both",
                    State = "final",
                    Ts = sentTs,
                }
            }
        });

        await provider2.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("See both", userEntry.Text);
        Assert.Equal(2, provider2.GetEntryMetadata("main")[userEntry.Id].Attachments!.Count);
    }

    [Fact]
    public async Task AttachmentMetadata_RehydratesAttachmentOnlyHistoryMessage()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var (_, provider1, _, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        await provider1.LoadAsync();
        await provider1.SendMessageAsync("main", "", default, new[]
        {
            new ChatAttachment
            {
                Type = "image",
                MimeType = "image/png",
                FileName = "screenshot.png",
                Content = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                SizeBytes = 3
            }
        });

        var (bridge2, provider2, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge2.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo
                {
                    Role = "user",
                    Text = "[media attached: media://inbound/screenshot.png (image/png)]",
                    State = "final",
                    Ts = sentTs,
                }
            }
        });

        await provider2.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal(string.Empty, userEntry.Text);
        Assert.Equal(
            "screenshot.png",
            Assert.Single(provider2.GetEntryMetadata("main")[userEntry.Id].Attachments!).DisplayFileName);
    }

    [Fact]
    public async Task AttachmentMetadata_DoesNotRehydratePastedMarkerTextWithoutSidecar()
    {
        using var tempDir = new TempDirectory();
        var toolPath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var attachmentPath = Path.Combine(tempDir.DirectoryPath, "attachment-metadata.json");
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() }, toolPath, attachmentPath);
        bridge.HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
        {
            SessionKey = key ?? "",
            SessionId = "session-1",
            Messages = new[]
            {
                new ChatMessageInfo { Role = "user", Text = "\u200B📎 spoof.txt", State = "final", Ts = 1 }
            }
        });

        await provider.LoadHistoryAsync("main");

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("📎 spoof.txt", userEntry.Text);
    }

    [Fact]
    public async Task SendMessageAsync_WithoutAttachment_EscapesPastedMarkerText()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();
        snapshots.Clear();

        await provider.SendMessageAsync("main", "\u200B📎 spoof.txt");

        Assert.Equal("📎 spoof.txt", snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User).Text);

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "main",
            Role = "user",
            Text = "\u200B📎 spoof.txt",
            State = "final"
        });

        var userEntry = snapshots[^1].Timelines["main"].Entries.Single(e => e.Kind == ChatTimelineItemKind.User);
        Assert.Equal("📎 spoof.txt", userEntry.Text);
    }

    // ── Metadata-first session updates ──

    [Fact]
    public async Task SessionsUpdated_WhileConnected_DoesNotLoadHistory()
    {
        var historyRequested = new List<string?>();
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        await provider.LoadAsync();

        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        var sessions = Enumerable.Range(0, 100)
            .Select(i => new SessionInfo
            {
                Key = i == 0 ? "main" : $"agent:main:session-{i}",
                IsMain = i == 0,
                DisplayName = $"Session {i}",
                Model = "test-model",
                TotalTokens = i,
            })
            .ToArray();
        bridge.RaiseSessions(sessions);

        Assert.Empty(historyRequested);
        var snapshot = snapshots[^1];
        Assert.Equal(100, snapshot.Threads.Length);
        Assert.Equal(100, snapshot.Timelines.Count);
        Assert.Equal("test-model", snapshot.Threads[42].Model);
    }

    [Fact]
    public async Task SessionsUpdated_WhileDisconnected_DoesNotLoadHistory()
    {
        var historyRequested = new List<string?>();
        var (bridge, provider, _, _) = CreateProvider();
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        await provider.LoadAsync();

        // Status stays Disconnected, sessions arrive.
        bridge.RaiseSessions(new[] { MainSession() });

        Assert.Empty(historyRequested);
    }

    // ── Reconnect invalidates history without bulk reload ──

    [Fact]
    public async Task StatusChanged_Connected_ClearsHistoryInFlightWithoutReloading()
    {
        var historyRequested = new List<string?>();
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.HistoryBehavior = key =>
        {
            historyRequested.Add(key);
            return Task.FromResult(new ChatHistoryInfo { SessionKey = key ?? "" });
        };
        await provider.LoadAsync();

        bridge.RaiseStatus(ConnectionStatus.Connected);

        Assert.Empty(historyRequested);

        await provider.LoadHistoryAsync("main");

        Assert.Equal(new[] { "main" }, historyRequested);
    }

    // ── LoadHistoryAsync retry on failure while connected ──

    [Fact]
    public async Task LoadHistoryAsync_WhenConnected_RetriesAfterFailure()
    {
        var calls = 0;
        Func<Task>? retry = null;
        var (bridge, provider, snapshots, notifications) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, _, callback) =>
            {
                retry = callback;
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("gateway not ready");

            return Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = "main",
                Messages = new[]
                {
                    new ChatMessageInfo { Role = "assistant", Text = "hello", State = "final", Ts = 1 },
                }
            });
        };
        await provider.LoadAsync();

        // Mark as connected so retry logic triggers.
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");

        // First call fails.
        Assert.Contains(notifications, n =>
            n.Kind == ChatProviderNotificationKind.Error &&
            n.Message?.Contains("gateway not ready") == true);

        Assert.NotNull(retry);
        await retry();

        // Retry should have succeeded.
        Assert.Equal(2, calls);
        Assert.Contains(snapshots, s =>
            s.Timelines.TryGetValue("main", out var tl) &&
            tl.Entries.Any(e => e.Kind == ChatTimelineItemKind.Assistant && e.Text == "hello"));
    }

    [Fact]
    public async Task LoadHistoryAsync_DoesNotRetryFailureFromPreviousConnection()
    {
        using var activities = new ChatActivityCollector();
        var calls = 0;
        Func<Task>? retry = null;
        var (bridge, provider, _, _) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, _, callback) =>
            {
                retry = callback;
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            throw new InvalidOperationException("gateway not ready");
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.LoadHistoryAsync("main");
        Assert.NotNull(retry);
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await retry();

        Assert.Equal(1, calls);
        var history = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("failure", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Equal("initial", history.GetTagItem(OpenClawTelemetryTagKey.Source.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadHistoryAsync_StaleBeforeNotification_DoesNotNotifyOrRetry()
    {
        using var activities = new ChatActivityCollector();
        FakeBridge? bridgeForFailureHook = null;
        var calls = 0;
        var retries = new List<Func<Task>>();
        var failureHookPending = true;
        var (bridge, provider, _, notifications) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, _, callback) =>
            {
                retries.Add(callback);
                return Task.CompletedTask;
            },
            historyFailureReservedForTesting: () =>
            {
                if (!failureHookPending)
                    return;

                failureHookPending = false;
                bridgeForFailureHook!.RaiseStatus(ConnectionStatus.Disconnected);
                bridgeForFailureHook.RaiseStatus(ConnectionStatus.Connected);
            });
        bridgeForFailureHook = bridge;
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            throw new InvalidOperationException("old connection failure");
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.LoadHistoryAsync("main");

        Assert.Equal(1, calls);
        Assert.Empty(notifications);
        Assert.Empty(retries);
        var history = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("canceled", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Null(history.GetTagItem(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task LoadHistoryAsync_QueuedNotification_DropsAfterDispose()
    {
        var deliveries = new List<Action>();
        var notifications = new List<ChatProviderNotification>();
        var retries = new List<Func<Task>>();
        var bridge = new FakeBridge
        {
            Sessions = new[] { MainSession() },
            CurrentStatus = ConnectionStatus.Connected,
            HistoryBehavior = _ => Task.FromException<ChatHistoryInfo>(
                new InvalidOperationException("old connection failure")),
        };
        var provider = new OpenClawChatDataProvider(
            bridge,
            post: action => deliveries.Add(action),
            toolMetaCacheFilePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tool-metadata.json"),
            historyRetryScheduler: (_, _, callback) =>
            {
                retries.Add(callback);
                return Task.CompletedTask;
            });
        provider.NotificationRequested += (_, args) => notifications.Add(args.Notification);

        await provider.LoadAsync();
        await provider.LoadHistoryAsync("main");
        Assert.Single(deliveries);
        Assert.Single(retries);

        await provider.DisposeAsync();
        deliveries[0]();
        await retries[0]();

        Assert.Empty(notifications);
    }

    [Fact]
    public async Task LoadHistoryAsync_ReconnectDuringFailureNotification_PreservesNewGenerationRetryBudget()
    {
        using var activities = new ChatActivityCollector();
        var calls = 0;
        var retries = new List<Func<Task>>();
        var (bridge, provider, _, notifications) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, _, callback) =>
            {
                retries.Add(callback);
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            throw new InvalidOperationException("gateway not ready");
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        var reconnectOnNextNotification = true;
        provider.NotificationRequested += (_, _) =>
        {
            if (!reconnectOnNextNotification)
                return;

            reconnectOnNextNotification = false;
            bridge.RaiseStatus(ConnectionStatus.Disconnected);
            bridge.RaiseStatus(ConnectionStatus.Connected);
        };

        await provider.LoadHistoryAsync("main");

        // Reconnect during notification invalidates the old reservation before
        // it can enqueue delayed work or consume the new generation's budget.
        Assert.Empty(retries);
        Assert.Single(notifications);
        var staleFailure = Assert.Single(
            activities.Stopped,
            activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName);
        Assert.Equal("canceled", staleFailure.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName()));
        Assert.Null(staleFailure.GetTagItem(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()));
        Assert.Equal(1, calls);

        // A fresh failure still receives the full three-retry budget.
        await provider.LoadHistoryAsync("main");
        for (var retryIndex = 0; retryIndex < retries.Count; retryIndex++)
            await retries[retryIndex]();

        Assert.Equal(3, retries.Count);
        Assert.Equal(5, calls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_CancelsInFlightHistoryAndDelayedRetry()
    {
        using var activities = new ChatActivityCollector();
        var pendingHistory = new TaskCompletionSource<ChatHistoryInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<Task>? retry = null;
        CancellationToken retryCancellation = default;
        var (bridge, provider, snapshots, notifications) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, cancellationToken, callback) =>
            {
                retryCancellation = cancellationToken;
                retry = callback;
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("gateway not ready");
            return pendingHistory.Task;
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);
        snapshots.Clear();

        await provider.LoadHistoryAsync("main");
        Assert.NotNull(retry);
        Assert.False(retryCancellation.IsCancellationRequested);

        var inFlightRetry = retry();
        Assert.Equal(2, calls);
        await provider.DisposeAsync();
        await inFlightRetry.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(retryCancellation.IsCancellationRequested);
        pendingHistory.SetResult(new ChatHistoryInfo
        {
            SessionKey = "main",
            Messages = new[] { new ChatMessageInfo { Role = "assistant", Text = "stale", Ts = 1 } },
        });
        await Task.Yield();

        Assert.Equal(2, calls);
        Assert.DoesNotContain(snapshots, snapshot =>
            snapshot.Timelines["main"].Entries.Any(entry => entry.Text == "stale"));
        Assert.Single(notifications);
        var historySpans = activities.Stopped
            .Where(activity => activity.OperationName == ChatTelemetryTracker.HistoryLoadSpanName)
            .ToArray();
        Assert.Equal(2, historySpans.Length);
        Assert.Contains(historySpans, history =>
            Equals("failure", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName())));
        Assert.Contains(historySpans, history =>
            Equals("canceled", history.GetTagItem(OpenClawTelemetryTagKey.Outcome.ToTelemetryName())));
        Assert.Single(historySpans, history =>
            history.GetTagItem(OpenClawTelemetryTagKey.ErrorType.ToTelemetryName()) is not null);
    }

    [Fact]
    public async Task Dispose_CancelsPendingHistoryRetryBeforeBridgeCall()
    {
        var calls = 0;
        Func<Task>? retry = null;
        CancellationToken retryCancellation = default;
        var (bridge, provider, _, notifications) = CreateProvider(
            new[] { MainSession() },
            historyRetryScheduler: (_, cancellationToken, callback) =>
            {
                retryCancellation = cancellationToken;
                retry = callback;
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            throw new InvalidOperationException("gateway not ready");
        };
        await provider.LoadAsync();
        bridge.RaiseStatus(ConnectionStatus.Connected);

        await provider.LoadHistoryAsync("main");
        Assert.NotNull(retry);
        Assert.False(retryCancellation.IsCancellationRequested);

        await provider.DisposeAsync();
        await retry().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(retryCancellation.IsCancellationRequested);
        Assert.Equal(1, calls);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task Dispose_CapturedLateStatusCallback_DoesNotReuseDisposedGeneration()
    {
        var (bridge, provider, _, _) = CreateProvider(new[] { MainSession() });
        bridge.RaiseStatus(ConnectionStatus.Connected);
        var capturedStatusHandlers = bridge.CaptureStatusChangedHandlers();
        Assert.NotNull(capturedStatusHandlers);

        await provider.DisposeAsync();

        Exception? exception = null;
        try
        {
            capturedStatusHandlers!(bridge, ConnectionStatus.Disconnected);
        }
        catch (Exception ex)
        {
            exception = ex;
        }
        Assert.Null(exception);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Compose-target fix: covers the "fresh install, zero sessions" path
    //  that previously stranded optimistic state under a synthetic "main"
    //  key while the gateway echoed events back under "agent:main:main".
    //  See OpenClawChatDataProvider.BuildSnapshotLocked for the design.
    // ─────────────────────────────────────────────────────────────────────

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots)
        CreateConnectedProvider(string canonicalMainKey = "agent:main:main")
    {
        var (bridge, provider, snapshots, _) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = canonicalMainKey;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        // Mirror real gateway behavior: after handshake the gateway always
        // emits sessions.list (even an empty one). The provider needs that
        // signal to flip ComposeTarget.IsReady on, so that the UI doesn't
        // briefly render the welcome zero-state for returning users whose
        // real sessions are about to be delivered.
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        return (bridge, provider, snapshots);
    }

    private static (FakeBridge bridge, OpenClawChatDataProvider provider, List<ChatDataSnapshot> snapshots, List<ChatProviderNotification> notifications)
        CreateConnectedProviderWithNotifications(string canonicalMainKey = "agent:main:main")
    {
        var (bridge, provider, snapshots, notifications) = CreateProvider();
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = canonicalMainKey;
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());
        return (bridge, provider, snapshots, notifications);
    }

    [Fact]
    public async Task SendMessageAsync_FreshInstall_QueuedStateKeyedByCanonicalSessionKey()
    {
        // Regression for the zero-state bug: the user clicks a suggestion on
        // a fresh install (zero sessions). The queued local state must land in
        // a timeline keyed by the gateway's canonical session key — NOT a
        // literal "main". Otherwise the gateway's chat events (which come
        // back keyed by the canonical key) build a SECOND timeline and the
        // pending state is orphaned.
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();

        await provider.SendMessageAsync("agent:main:main", "hi");

        Assert.Contains(bridge.SentMessages, m => m == "hi");
        Assert.Equal("agent:main:main", bridge.SentSessionKeys[0]);
        var latest = snapshots[^1];
        Assert.True(latest.Timelines.ContainsKey("agent:main:main"));
        Assert.False(latest.Timelines.ContainsKey("main"),
            "The provider must never key timelines by the literal 'main' alias.");
        Assert.Single(latest.Timelines["agent:main:main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.User && e.Text == "hi");
        Assert.Empty(GetQueuedMessages(latest, "agent:main:main"));
    }

    [Fact]
    public async Task SendMessageAsync_FreshInstall_SnapshotExposesComposeOnlyTimeline()
    {
        // Before the first SessionsUpdated arrives, the gateway-side session
        // doesn't exist yet, so Threads is empty. But the queued user message
        // must still be reachable to the UI: it's stored under the
        // compose-target key. The UI then synthesizes a compose-only thread
        // (matching the canonical key) so the timeline can render.
        var (_, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();

        await provider.SendMessageAsync("agent:main:main", "hi");

        var latest = snapshots[^1];
        // BuildSnapshotLocked surfaces a synthetic ChatThread when the
        // compose key has queued/active local state but isn't materialized yet.
        Assert.Single(latest.Threads);
        Assert.Equal("agent:main:main", latest.Threads[0].Id);
        Assert.Equal("agent:main:main", latest.ComposeTarget.SessionKey);
    }

    [Fact]
    public async Task SessionsUpdated_AfterFirstSend_PreservesQueuedLocalState()
    {
        // The critical assertion: when the gateway materializes the session
        // and emits SessionsUpdated with the canonical key, the direct local
        // state that was written under that exact key SURVIVES (no second
        // empty timeline gets created on top of it).
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main session", Status = "active" }
        });

        var latest = snapshots[^1];
        Assert.Single(latest.Threads);
        Assert.Equal("agent:main:main", latest.Threads[0].Id);
        var timeline = latest.Timelines["agent:main:main"];
        Assert.Single(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "hi");
        Assert.Empty(GetQueuedMessages(latest, "agent:main:main"));
    }

    [Fact]
    public async Task ChatEvent_WithEmptySessionKey_IsDropped()
    {
        // The "main" literal fallback in event handlers was the second half
        // of the bug: it would silently route mis-routed events to a synthetic
        // bucket. The fix is to drop the event and log — surfacing protocol
        // bugs instead of papering over them.
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "echo with no session key — should be dropped"
        });

        // And specifically: no synthetic "main" timeline was created.
        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.False(latest.Timelines.ContainsKey("main"));
        Assert.DoesNotContain(timeline.Entries, e => e.Text.Contains("echo with no session key"));
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Tone == ChatTone.Warning &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("echo with no session key") ?? false) ||
            (n.Message?.Contains("echo with no session key") ?? false));
    }

    [Fact]
    public async Task KeylessEvents_RaiseOnlyOneDiagnostic()
    {
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "first dropped payload"
        });
        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"second dropped payload"}""", sessionKey: ""));
        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "",
            Role = "assistant",
            Text = "third dropped payload"
        });

        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.Single(snapshots[^1].Timelines["agent:main:main"].Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("dropped payload") ?? false) ||
            (n.Message?.Contains("dropped payload") ?? false));
    }

    [Fact]
    public async Task KeylessEvents_DiagnosticResetsOnReconnect()
    {
        // The one-shot guard should be reset when the gateway reconnects so
        // that a still-broken gateway surfaces the notification again in the
        // new session instead of staying silent forever.
        var (bridge, provider, _, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        // First keyless event — diagnostic fires once.
        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "", Role = "assistant", Text = "pre-reconnect drop" });
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");

        // Simulate gateway disconnect + reconnect.
        bridge.RaiseStatus(ConnectionStatus.Disconnected);
        bridge.HasHandshakeSnapshot = true;
        bridge.MainSessionKey = "agent:main:main";
        bridge.RaiseStatus(ConnectionStatus.Connected);
        bridge.RaiseSessions(Array.Empty<SessionInfo>());

        // After reconnect, the same keyless-event drop should fire the diagnostic again.
        bridge.RaiseChat(new ChatMessageInfo { SessionKey = "", Role = "assistant", Text = "post-reconnect drop" });
        Assert.Equal(2, notifications.Count(n => n.Title == "Chat_Notification_KeylessEventDropped"));
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("pre-reconnect drop") ?? false) ||
            (n.Message?.Contains("pre-reconnect drop") ?? false) ||
            (n.Title?.Contains("post-reconnect drop") ?? false) ||
            (n.Message?.Contains("post-reconnect drop") ?? false));
    }

    [Fact]
    public async Task AgentEvent_WithEmptySessionKey_IsDroppedAndDiagnosed()
    {
        var (bridge, provider, snapshots, notifications) = CreateConnectedProviderWithNotifications("agent:main:main");
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeAgentEvent("assistant", """{"delta":"secret agent payload"}""", sessionKey: ""));

        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.False(latest.Timelines.ContainsKey("main"));
        Assert.DoesNotContain(timeline.Entries, e => e.Text.Contains("secret agent payload"));
        Assert.Contains(timeline.Entries, e =>
            e.Kind == ChatTimelineItemKind.Status &&
            e.Tone == ChatTone.Warning &&
            e.Text == "Chat_Notification_KeylessEventDroppedMessage");
        Assert.Single(notifications, n => n.Title == "Chat_Notification_KeylessEventDropped");
        Assert.DoesNotContain(notifications, n =>
            (n.Title?.Contains("secret agent payload") ?? false) ||
            (n.Message?.Contains("secret agent payload") ?? false));
    }

    [Fact]
    public async Task ChatEvent_WithCanonicalSessionKey_AppendsToExistingTimeline()
    {
        // Happy path: gateway echoes assistant text back under the same
        // canonical key the optimistic entry used. They land in one timeline.
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        await provider.LoadAsync();
        await provider.SendMessageAsync("agent:main:main", "hi");

        bridge.RaiseChat(new ChatMessageInfo
        {
            SessionKey = "agent:main:main",
            Role = "assistant",
            Text = "hello back",
            State = "final"
        });

        var latest = snapshots[^1];
        var timeline = latest.Timelines["agent:main:main"];
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.User && e.Text == "hi");
        Assert.Contains(timeline.Entries, e => e.Kind == ChatTimelineItemKind.Assistant && e.Text == "hello back");
        Assert.False(latest.Timelines.ContainsKey("main"));
    }

    [Fact]
    public async Task SendMessageAsync_PreHandshake_GatewayClientRefusesViaProvider()
    {
        // Provider-level proof that the upstream guard fires: SendMessageAsync
        // bubbles the InvalidOperationException raised by the gateway client's
        // ResolveEffectiveSessionKey when no canonical sessionKey is known.
        // (The pure-function unit test for the helper itself lives in
        //  OpenClawGatewayClientSessionKeyTests in OpenClaw.Shared.Tests.)
        var (bridge, provider, _, _) = CreateProvider();
        bridge.IsConnected = true;
        bridge.SendBehavior = (_, key, _) =>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "chat.send requires a sessionKey, but the gateway handshake has not resolved one yet.");
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.SendMessageAsync("", "hi"));
    }

    [Fact]
    public async Task ResolveDefaultThreadId_PrefersIsMain_NotLiteralStringMatch()
    {
        // The pre-refactor ResolveDefaultThreadIdLocked heuristic compared
        // thread.Id to the literal "main", which would silently lose the
        // default when the canonical key was "agent:main:main".
        var (bridge, provider, snapshots) = CreateConnectedProvider("agent:main:main");
        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "agent:main:other", IsMain = false, DisplayName = "Other" },
            new SessionInfo { Key = "agent:main:main",  IsMain = true,  DisplayName = "Main" }
        });
        var snap = await provider.LoadAsync();
        Assert.Equal("agent:main:main", snap.DefaultThreadId);
    }

    [Fact]
    public async Task RememberSelectedThread_PrefersSelectionAfterReload()
    {
        using var temp = new TempDirectory();
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main" },
            new SessionInfo { Key = "agent:main:review", IsMain = false, DisplayName = "Review", Model = "gpt-5.1", Provider = "openai" }
        };
        var (_, provider, _, _) = CreateProvider(
            sessions,
            lastChatStatePath: Path.Combine(temp.DirectoryPath, "last-chat-state.json"));

        var first = await provider.LoadAsync();
        Assert.Equal("agent:main:main", first.DefaultThreadId);

        provider.RememberSelectedThread("agent:main:review");
        var reloaded = await provider.LoadAsync();

        Assert.Equal("agent:main:review", reloaded.DefaultThreadId);
        Assert.Equal("agent:main:review", provider.CachedLastChatState?.DefaultThreadId);
        Assert.Equal("Review", provider.CachedLastChatState?.ThreadTitle);
        Assert.Equal("gpt-5.1", provider.CachedLastChatState?.Model);
        Assert.Equal("openai", provider.CachedLastChatState?.ModelProvider);
    }

    [Fact]
    public async Task RememberSelectedThread_FallsBackWhenSelectionDisappears()
    {
        using var temp = new TempDirectory();
        var main = new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main" };
        var review = new SessionInfo { Key = "agent:main:review", IsMain = false, DisplayName = "Review" };
        var (bridge, provider, snapshots, _) = CreateProvider(
            new[] { main, review },
            lastChatStatePath: Path.Combine(temp.DirectoryPath, "last-chat-state.json"));

        await provider.LoadAsync();
        provider.RememberSelectedThread("agent:main:review");
        snapshots.Clear();

        bridge.RaiseSessions(new[] { main });

        Assert.Equal("agent:main:main", snapshots[^1].DefaultThreadId);
        Assert.Equal("agent:main:main", provider.CachedLastChatState?.DefaultThreadId);
    }

    [Fact]
    public async Task RememberSelectedThread_CancelsPendingDefaultStateSave()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.DirectoryPath, "last-chat-state.json");
        var main = new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main" };
        var review = new SessionInfo { Key = "agent:main:review", IsMain = false, DisplayName = "Review" };
        var (bridge, provider, _, _) = CreateProvider(
            new[] { main, review },
            lastChatStatePath: statePath,
            lastChatStateSaveDelay: TimeSpan.FromMilliseconds(25));

        bridge.RaiseSessions(new[] { main, review });
        provider.RememberSelectedThread("agent:main:review");

        await Task.Delay(150);

        var persisted = OpenClawChatDataProvider.LoadLastChatState(statePath);
        Assert.Equal("agent:main:review", persisted?.DefaultThreadId);
    }

    [Fact]
    public async Task RememberSelectedThread_ModelsBeforeSessionsKeepsSavedSelectionPending()
    {
        using var temp = new TempDirectory();
        var statePath = Path.Combine(temp.DirectoryPath, "last-chat-state.json");
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new OpenClawChatDataProvider.LastChatState
        {
            DefaultThreadId = "agent:main:review",
            ThreadTitle = "Review (main/review)",
            Model = "gpt-5.1",
            ModelProvider = "openai",
            AvailableModels = new[] { "gpt-5.0" }
        }));
        var main = new SessionInfo { Key = "agent:main:main", IsMain = true, DisplayName = "Main" };
        var review = new SessionInfo { Key = "agent:main:review", IsMain = false, DisplayName = "Review", Model = "gpt-5.1", Provider = "openai" };
        var (bridge, provider, snapshots, _) = CreateProvider(
            lastChatStatePath: statePath,
            lastChatStateSaveDelay: TimeSpan.FromMilliseconds(25));

        await provider.LoadAsync();
        snapshots.Clear();

        bridge.RaiseModels(new ModelsListInfo
        {
            Models = new List<ModelInfo>
            {
                new() { Id = "gpt-5.1", Name = "GPT-5.1" }
            }
        });

        Assert.Equal("agent:main:review", snapshots[^1].DefaultThreadId);
        Assert.Equal("agent:main:review", provider.CachedLastChatState?.DefaultThreadId);

        await Task.Delay(150);
        var persistedBeforeSessions = OpenClawChatDataProvider.LoadLastChatState(statePath);
        Assert.Equal("agent:main:review", persistedBeforeSessions?.DefaultThreadId);

        bridge.RaiseSessions(new[] { main, review });

        Assert.Equal("agent:main:review", snapshots[^1].DefaultThreadId);
        Assert.Equal("agent:main:review", provider.CachedLastChatState?.DefaultThreadId);
    }

    // ─── RespondToPermissionAsync routes through the RPC bridge ────────────
    // These tests pin the slash-command → RPC behavioral pivot. The old code
    // sent ``/approve <id> <decision>`` as chat input, which deadlocked
    // because the agent was blocked on the approval. The new code calls
    // bridge.ResolveExecApprovalAsync. If a refactor reintroduces a slash
    // command path here, these tests fail.
    // ──────────────────────────────────────────────────────────────────────

    private static AgentEventInfo MakeApprovalRequestedEvent(string approvalId, string sessionKey = "main")
        => MakeApprovalRequestedEventWithIds(approvalId, approvalId, sessionKey);

    private static AgentEventInfo MakeApprovalRequestedEventWithIds(
        string approvalId,
        string? approvalSlug,
        string sessionKey = "main",
        string title = "Exec approval")
    {
        var json = $$"""
            {
              "phase": "requested",
              "approvalId": "{{approvalId}}",
              "approvalSlug": "{{approvalSlug ?? ""}}",
              "host": "gateway",
              "command": "openclaw nodes invoke --node \"Windows Node\" --command system.run",
              "title": "{{title}}",
              "message": "Approve this exec?",
              "agentId": "main"
            }
            """;
        return MakeAgentEvent("approval", json, sessionKey: sessionKey);
    }

    [Fact]
    public async Task RespondToPermissionAsync_AllowRoutesAllowOnceThroughRpcAndClearsBanner()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-allow-1"));
        // Banner must be visible before the response.
        Assert.NotNull(snapshots[^1].Timelines["main"].PendingPermission);

        await provider.RespondToPermissionAsync("main", "appr-allow-1", allow: true);

        // RPC was called with allow-once (NOT a slash command).
        Assert.Single(bridge.ResolvedApprovals);
        Assert.Equal("appr-allow-1", bridge.ResolvedApprovals[0].Id);
        Assert.Equal("allow-once", bridge.ResolvedApprovals[0].Decision);

        // No chat message was sent (would mean a slash-command regression).
        Assert.Empty(bridge.SentMessages);

        // Banner cleared on success.
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task RespondToPermissionAsync_DenyRoutesDenyThroughRpcAndClearsBanner()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-deny-1"));
        Assert.NotNull(snapshots[^1].Timelines["main"].PendingPermission);

        await provider.RespondToPermissionAsync("main", "appr-deny-1", allow: false);

        Assert.Single(bridge.ResolvedApprovals);
        Assert.Equal("appr-deny-1", bridge.ResolvedApprovals[0].Id);
        Assert.Equal("deny", bridge.ResolvedApprovals[0].Decision);
        Assert.Empty(bridge.SentMessages);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task RespondToPermissionAsync_AllowAlwaysRoutesAllowAlwaysThroughRpcAndMarksAlwaysAllowed()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-always-1"));
        var pendingEntry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Contains(ChatPermissionActionKeys.AllowAlways, pendingEntry.PermissionActions!);

        await provider.RespondToPermissionAsync("main", "appr-always-1", ChatPermissionActionKeys.AllowAlways);

        Assert.Single(bridge.ResolvedApprovals);
        Assert.Equal("appr-always-1", bridge.ResolvedApprovals[0].Id);
        Assert.Equal("allow-always", bridge.ResolvedApprovals[0].Decision);
        Assert.Empty(bridge.SentMessages);

        var decidedEntry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.AllowedAlways, decidedEntry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task RespondToPermissionAsync_RpcThrows_BannerPreservedForRetry()
    {
        // Critical contract: if ResolveExecApprovalAsync throws (e.g. gateway
        // disconnected, see OpenClawGatewayClient.ResolveExecApprovalAsync's
        // explicit IsConnected guard), the banner MUST remain so the user can
        // retry. Clearing it would silently swallow the failure and leave
        // the agent waiting on an approval the user has no way to re-issue.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-fail-1"));
        var before = snapshots[^1].Timelines["main"].PendingPermission;
        Assert.NotNull(before);

        bridge.ResolveApprovalBehavior = (_, _) =>
            Task.FromException(new InvalidOperationException("gateway not connected"));

        await provider.RespondToPermissionAsync("main", "appr-fail-1", allow: true);

        Assert.Single(bridge.ResolvedApprovals);
        // Banner preserved on failure — the matching pending request is still there.
        var after = snapshots[^1].Timelines["main"].PendingPermission;
        Assert.NotNull(after);
        Assert.Equal("appr-fail-1", after!.RequestId);
    }

    [Fact]
    public async Task ResolvedEcho_WithAllowDecision_MarksEntryAllowedNotExpired()
    {
        // Regression for the "approvals always render Expired" race: the
        // gateway broadcasts exec.approval.resolved on the same WebSocket the
        // RPC response travels on, and the echo typically wins the race. The
        // terminal-phase handler must honor the gateway's actual decision
        // (phase="resolved" → Allowed) rather than the legacy default Expired,
        // otherwise ResolvePermission's no-overwrite guard then blocks the
        // user-click stamp from ever landing.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-echo-allow"));
        bridge.RaiseAgent(MakeApprovalResolvedEvent("appr-echo-allow", phase: "resolved"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Allowed, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ResolvedEcho_WithAllowAlwaysDecision_MarksEntryAlwaysAllowed()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-echo-always"));
        bridge.RaiseAgent(MakeApprovalResolvedEvent("appr-echo-always", phase: "resolved", decision: "allow-always"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.AllowedAlways, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ResolvedEcho_WithDenyDecision_MarksEntryDeniedNotExpired()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-echo-deny"));
        bridge.RaiseAgent(MakeApprovalResolvedEvent("appr-echo-deny", phase: "denied"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Denied, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ResolvedEcho_WithNonDecidedTerminalPhase_StaysExpired()
    {
        // Phases that aren't allow/deny (aborted, canceled, expired, timeout,
        // error) collapse to Expired — the "decided elsewhere or never
        // decided" badge. Spot-check one of them.
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-echo-expired"));
        bridge.RaiseAgent(MakeApprovalResolvedEvent("appr-echo-expired", phase: "expired"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Expired, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ResolvedEcho_WithExpiredPhaseAndAllowDecision_StaysExpired()
    {
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEvent("appr-echo-expired-allow"));
        bridge.RaiseAgent(MakeApprovalResolvedEvent(
            "appr-echo-expired-allow",
            phase: "expired",
            decision: ChatPermissionActionKeys.AllowAlways));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Expired, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ApprovalRequested_DedupesUuidFirstSlugTwin_AndSlugOnlyResolvedClearsBanner()
    {
        // Regression for the second "one Expired, one Allowed" root cause:
        // the top-level translator can emit a UUID-only requested event before
        // the agent-stream slug+UUID twin. Suppressing that twin must still
        // record slug<->UUID linkage so a later slug-only terminal echo clears
        // the original UUID-keyed banner.
        const string uuid = "8653b04d-fa8f-4188-9f22-c1c4f08fe6b8";
        const string slug = "8653b04d";
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEventWithIds(uuid, approvalSlug: ""));
        bridge.RaiseAgent(MakeApprovalRequestedEventWithIds(uuid, approvalSlug: slug, title: "Command approval requested"));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(uuid, entry.PermissionRequestId);
        Assert.Equal(uuid, snapshots[^1].Timelines["main"].PendingPermission?.RequestId);

        bridge.RaiseAgent(MakeApprovalResolvedEvent(approvalId: "", phase: "resolved", approvalSlug: slug));

        entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Allowed, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    [Fact]
    public async Task ApprovalRequested_DedupesSlugFirstUuidTwin_AndUuidOnlyResolvedClearsBanner()
    {
        // Covers the reverse ordering: if the slug+UUID stream wins first,
        // the UUID-only top-level twin must not render a duplicate, and a
        // UUID-only terminal echo must still resolve the slug-keyed banner.
        const string uuid = "b4fd7109-4b8f-4706-8d47-ec7963e65d8d";
        const string slug = "b4fd7109";
        var (bridge, provider, snapshots, _) = CreateProvider(new[] { MainSession() });
        await provider.LoadAsync();

        bridge.RaiseAgent(MakeApprovalRequestedEventWithIds(uuid, approvalSlug: slug, title: "Command approval requested"));
        bridge.RaiseAgent(MakeApprovalRequestedEventWithIds(uuid, approvalSlug: ""));

        var entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(slug, entry.PermissionRequestId);
        Assert.Equal(slug, snapshots[^1].Timelines["main"].PendingPermission?.RequestId);

        bridge.RaiseAgent(MakeApprovalResolvedEvent(approvalId: uuid, phase: "resolved", approvalSlug: ""));

        entry = Assert.Single(snapshots[^1].Timelines["main"].Entries,
            e => e.Kind == ChatTimelineItemKind.PermissionRequest);
        Assert.Equal(ChatPermissionDecision.Allowed, entry.PermissionDecision);
        Assert.Null(snapshots[^1].Timelines["main"].PendingPermission);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int attempts = 50)
    {
        for (var i = 0; i < attempts && !condition(); i++)
            await Task.Delay(10);
    }

    private static AgentEventInfo MakeApprovalResolvedEvent(
        string approvalId,
        string phase,
        string sessionKey = "main",
        string? approvalSlug = null,
        string? decision = null)
    {
        // Mirrors the flat envelope that OpenClawGatewayClient.HandleExecApprovalEvent
        // synthesizes from a top-level exec.approval.resolved broadcast.
        var json = $$"""
            {
              "phase": "{{phase}}",
              "approvalId": "{{approvalId}}",
              "approvalSlug": "{{approvalSlug ?? approvalId}}",
              "decision": "{{decision ?? ""}}",
              "host": "gateway",
              "command": "openclaw nodes invoke --node \"Windows Node\" --command system.run",
              "agentId": "main"
            }
            """;
        return MakeAgentEvent("approval", json, sessionKey: sessionKey);
    }

    [Fact]
    public async Task RuntimeGolden_PublicSnapshotPreservesCrossDomainState()
    {
        var session = new SessionInfo
        {
            Key = "main",
            IsMain = true,
            DisplayName = "Main session",
            Status = "active",
            Model = "provider/model-a",
            Provider = "provider",
            ThinkingLevel = "high",
        };
        var (bridge, provider, snapshots, _) = CreateProvider([session]);
        bridge.MainSessionKey = "main";
        bridge.HasHandshakeSnapshot = true;
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            bridge.RaiseSessions([session]);
            bridge.RaiseModels(new ModelsListInfo
            {
                Models =
                [
                    new ModelInfo { Id = "provider/model-a", Name = "Model A", Provider = "provider" },
                    new ModelInfo { Id = "provider/model-b", Name = "Model B", Provider = "provider" },
                ],
            });

            await provider.SendMessageAsync("main", "hello");
            bridge.RaiseAgent(MakeAgentEvent(
                "lifecycle",
                """{"phase":"start"}""",
                runId: "run-1"));
            bridge.RaiseAgent(MakeAgentEvent(
                "item",
                """{"kind":"tool","phase":"start","title":"read notes.txt","itemId":"tool-1"}""",
                runId: "run-1"));
            bridge.RaiseAgent(MakeAgentEvent(
                "approval",
                """{"phase":"requested","approvalSlug":"approve-1","approvalId":"approval-1","title":"Run command","host":"node","command":"echo ok"}""",
                runId: "run-1"));
            bridge.RaiseChat(new ChatMessageInfo
            {
                SessionKey = "main",
                Role = "assistant",
                Text = "done",
                State = "final",
                OpenClawId = "message-1",
                OpenClawSeq = 4,
            });

            var snapshot = snapshots[^1];
            Assert.Equal(
                [
                    "main|Main session||provider/model-a|high|Running|Working",
                ],
                snapshot.Threads.Select(thread =>
                    $"{thread.Id}|{thread.Title}|{thread.AgentId}|{thread.Model}|{thread.ThinkingLevel}|{thread.Status}|{thread.Activity}"));
            Assert.Equal(
                [
                    "e1|User|hello|False|||Pending",
                    "e2|ToolCall|read notes.txt|False|Tool|Interrupted|Pending",
                    "e3|PermissionRequest|echo ok|False|node||Pending",
                    "e4|Assistant|done|False|||Pending",
                ],
                snapshot.Timelines["main"].Entries.Select(entry =>
                    $"{entry.Id}|{entry.Kind}|{entry.Text}|{entry.IsStreaming}|{entry.ToolName}|{entry.ToolResult}|{entry.PermissionDecision}"));
            Assert.Equal("approve-1", snapshot.Timelines["main"].PendingPermission?.RequestId);
            Assert.Equal(["provider/model-a", "provider/model-b"], snapshot.AvailableModels);
            Assert.Equal("main", snapshot.DefaultThreadId);
            Assert.Equal(new ChatComposeTarget("main", true, "main"), snapshot.ComposeTarget);
            Assert.Equal("Connected", snapshot.ConnectionStatus);
            Assert.Equal(0, snapshot.TimelineGenerations!.GetValueOrDefault("main"));
            Assert.Empty(GetQueuedMessages(snapshot, "main"));
        }
    }

    [Fact]
    public async Task RuntimeGolden_ResetAtomicallyClearsQueueAndAdvancesGeneration()
    {
        var (bridge, provider, snapshots, _) = CreateProvider([MainSession()]);
        bridge.MainSessionKey = "main";
        bridge.HasHandshakeSnapshot = true;
        bridge.SendResults.Enqueue(new ChatSendResult { RunId = "run-1", Status = "started" });

        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            bridge.RaiseSessions([MainSession()]);
            await provider.SendMessageAsync("main", "first");
            bridge.RaiseAgent(MakeAgentEvent(
                "lifecycle",
                """{"phase":"start"}""",
                runId: "run-1"));
            await provider.SendMessageAsync("main", "second");

            var queuedSnapshot = snapshots[^1];
            Assert.Equal(
                ["q2|second|Queued"],
                GetQueuedMessages(queuedSnapshot, "main")
                    .Select(message => $"{message.Id}|{message.Text}|{message.SendState}"));

            var result = await provider.ExecuteLifecycleCommandAsync(
                "main",
                ChatLifecycleCommandKind.Reset);

            Assert.True(result.Succeeded);
            var resetSnapshot = snapshots[^1];
            Assert.Empty(resetSnapshot.Timelines["main"].Entries);
            Assert.True(resetSnapshot.Timelines["main"].HistoryLoaded);
            Assert.Empty(GetQueuedMessages(resetSnapshot, "main"));
            Assert.Equal(1, resetSnapshot.TimelineGenerations!["main"]);
            await WaitForConditionAsync(() => bridge.AbortedRunIds.Count == 2);
            Assert.Contains("run-1", bridge.AbortedRunIds);
            Assert.Equal(2, bridge.AbortedRunIds.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public async Task LoadHistoryAsync_QueuedDeliveryDropsAfterResetGenerationAdvances()
    {
        using var temp = new TempDirectory();
        var bridge = new FakeBridge
        {
            Sessions = [MainSession()],
            HistoryBehavior = key => Task.FromResult(new ChatHistoryInfo
            {
                SessionKey = key ?? string.Empty,
                Messages =
                [
                    new ChatMessageInfo
                    {
                        Role = "assistant",
                        Text = "stale history",
                        OpenClawSeq = 1,
                    },
                ],
            }),
        };
        var deliveries = new List<Action>();
        var snapshots = new List<ChatDataSnapshot>();
        var provider = new OpenClawChatDataProvider(
            bridge,
            post: deliveries.Add,
            toolMetaCacheFilePath: Path.Combine(
                temp.DirectoryPath,
                "tool-metadata.json"));
        provider.Changed += (_, args) => snapshots.Add(args.Snapshot);
        await using (provider)
        {
            await provider.LoadAsync();
            await provider.LoadHistoryAsync("main");
            Assert.Single(deliveries);

            bridge.RaiseSessionCommandCompleted(new SessionCommandResult
            {
                Method = "sessions.reset",
                Ok = true,
                Key = "main",
            });
            Assert.Equal(2, deliveries.Count);

            deliveries[1]();
            deliveries[0]();

            var snapshot = Assert.Single(snapshots);
            Assert.Empty(snapshot.Timelines["main"].Entries);
            Assert.Equal(1, snapshot.TimelineGenerations!["main"]);
        }
    }

    [Fact]
    public async Task LoadHistoryAsync_DelayedRetryDoesNotCrossResetGeneration()
    {
        var retries = new List<Func<Task>>();
        var calls = 0;
        var (bridge, provider, _, _) = CreateProvider(
            [MainSession()],
            historyRetryScheduler: (_, _, retry) =>
            {
                retries.Add(retry);
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
        {
            calls++;
            throw new InvalidOperationException("history unavailable");
        };
        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.LoadHistoryAsync("main");
            var retry = Assert.Single(retries);
            Assert.Equal(1, GetHistoryRetryEntryCount(provider));

            bridge.RaiseSessionCommandCompleted(new SessionCommandResult
            {
                Method = "sessions.reset",
                Ok = true,
                Key = "main",
            });
            Assert.Equal(0, GetHistoryRetryEntryCount(provider));
            await retry();

            Assert.Equal(1, calls);
            Assert.Single(retries);
        }
    }

    [Fact]
    public async Task HistoryResetCleanup_PreservesCurrentGenerationPendingWork()
    {
        var (_, provider, _, _) = CreateProvider([MainSession()]);
        await using (provider)
        {
            await provider.LoadAsync();
            var state = GetConversationState(provider);
            var loader = GetHistoryLoader(provider);
            var oldToken = state.CaptureHistoryToken("main");
            var reset = state.ResetThread(
                "main",
                new ChatProjectionContext(
                    "main",
                    HasHandshakeSnapshot: true));
            var currentToken = state.CaptureHistoryToken("main");
            var pending = GetHistoryPendingReloads(loader);
            var retries = GetHistoryRetryEntries(loader);
            pending["main"] = currentToken;
            retries[oldToken] = 1;
            retries[currentToken] = 1;

            loader.ApplyReset("main", reset.ResetGeneration);

            Assert.Equal(currentToken, pending["main"]);
            Assert.False(retries.ContainsKey(oldToken));
            Assert.Equal(1, retries[currentToken]);
        }
    }

    [Fact]
    public async Task HistoryRetry_StaleTokenCannotOccupyCurrentPendingSlot()
    {
        var retries = new List<Func<Task>>();
        var (bridge, provider, _, _) = CreateProvider(
            [MainSession()],
            historyRetryScheduler: (_, _, retry) =>
            {
                retries.Add(retry);
                return Task.CompletedTask;
            });
        bridge.HistoryBehavior = _ =>
            throw new InvalidOperationException("history unavailable");
        await using (provider)
        {
            await provider.LoadAsync();
            bridge.RaiseStatus(ConnectionStatus.Connected);
            await provider.LoadHistoryAsync("main", force: true);
            var staleRetry = Assert.Single(retries);
            var currentHistory = new TaskCompletionSource<ChatHistoryInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var loader = GetHistoryLoader(provider);
            bridge.HistoryBehavior = _ => currentHistory.Task;

            var state = GetConversationState(provider);
            var reset = state.ResetThread(
                "main",
                new ChatProjectionContext(
                    "main",
                    HasHandshakeSnapshot: true));
            loader.ApplyReset("main", reset.ResetGeneration);
            var currentLoad = provider.LoadHistoryAsync(
                "main",
                force: true,
                authoritative: true);
            await Task.Yield();

            await staleRetry();

            Assert.Empty(GetHistoryPendingReloads(loader));
            currentHistory.SetResult(new ChatHistoryInfo
            {
                SessionKey = "main",
            });
            await currentLoad;
        }
    }

    [Fact]
    public void LoadLastChatState_WithCorruptedJson_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.DirectoryPath, "last-chat-state.json");
        File.WriteAllText(path, "{not json");

        var state = OpenClawChatDataProvider.LoadLastChatState(path);

        Assert.Null(state);
    }

    private static IReadOnlyList<ChatQueuedMessage> GetQueuedMessages(ChatDataSnapshot snapshot, string threadId)
        => snapshot.QueuedMessagesByThread is not null &&
           snapshot.QueuedMessagesByThread.TryGetValue(threadId, out var queued)
            ? queued
            : Array.Empty<ChatQueuedMessage>();

    private static ISet<string> GetQueuedDrainScheduledThreads(OpenClawChatDataProvider provider)
    {
        var stateField = typeof(OpenClawChatDataProvider).GetField(
            "_state",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var state = Assert.IsType<ChatConversationState>(stateField?.GetValue(provider));
        var queueField = typeof(ChatConversationState).GetField(
            "_queue",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var queue = Assert.IsType<ChatQueueState>(queueField?.GetValue(state));
        var scheduledField = typeof(ChatQueueState).GetField(
            "_drainScheduledThreads",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(scheduledField);
        return Assert.IsAssignableFrom<ISet<string>>(scheduledField.GetValue(queue));
    }

    private static void MarkPersistedMessageAborted(
        OpenClawChatDataProvider provider,
        string threadId,
        string messageId)
    {
        var field = typeof(OpenClawChatDataProvider).GetField(
            "_persistence",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var persistence =
            Assert.IsType<ChatStatePersistence>(field.GetValue(provider));
        Assert.True(persistence.TryAddAbortedIds(
            threadId,
            resetGeneration: 0,
            [messageId]));
    }

    private static int GetHistoryRetryEntryCount(OpenClawChatDataProvider provider)
    {
        var loaderField = typeof(OpenClawChatDataProvider).GetField(
            "_historyLoader",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var loader = Assert.IsType<ChatHistoryLoader>(
            loaderField?.GetValue(provider));
        var retriesField = typeof(ChatHistoryLoader).GetField(
            "_retryCounts",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var retries = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            retriesField?.GetValue(loader));
        return retries.Count;
    }

    private static ChatConversationState GetConversationState(
        OpenClawChatDataProvider provider)
    {
        var field = typeof(OpenClawChatDataProvider).GetField(
            "_state",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<ChatConversationState>(
            field?.GetValue(provider));
    }

    private static ChatHistoryLoader GetHistoryLoader(
        OpenClawChatDataProvider provider)
    {
        var field = typeof(OpenClawChatDataProvider).GetField(
            "_historyLoader",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<ChatHistoryLoader>(
            field?.GetValue(provider));
    }

    private static Dictionary<string, ChatHistoryCommitToken>
        GetHistoryPendingReloads(ChatHistoryLoader loader)
    {
        var field = typeof(ChatHistoryLoader).GetField(
            "_authoritativePending",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<
            Dictionary<string, ChatHistoryCommitToken>>(
            field?.GetValue(loader));
    }

    private static Dictionary<ChatHistoryCommitToken, int>
        GetHistoryRetryEntries(ChatHistoryLoader loader)
    {
        var field = typeof(ChatHistoryLoader).GetField(
            "_retryCounts",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<
            Dictionary<ChatHistoryCommitToken, int>>(
            field?.GetValue(loader));
    }

    private static bool HasFailedQueuedMessage(ChatDataSnapshot snapshot, string threadId, string text) =>
        GetQueuedMessages(snapshot, threadId).Any(message =>
            message.Text == text &&
            message.SendState == ChatQueuedMessageSendState.Failed);

    private static void AssertNoQueuedTranscriptDuplicate(
        IEnumerable<ChatDataSnapshot> snapshots,
        string threadId,
        string text)
    {
        foreach (var snapshot in snapshots)
        {
            var hasQueued = GetQueuedMessages(snapshot, threadId).Any(message => message.Text == text);
            var hasTranscript = snapshot.Timelines.TryGetValue(threadId, out var timeline)
                && timeline.Entries.Any(entry => entry.Kind == ChatTimelineItemKind.User && entry.Text == text);
            Assert.False(hasQueued && hasTranscript, $"'{text}' was visible in both queue and transcript.");
        }
    }

    private sealed class TestLogger : OpenClaw.Shared.IOpenClawLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "openclaw-chat-attachments-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
