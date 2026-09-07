using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for the tool metadata cache matching logic used to recover tool
/// names/labels after gateway history flattening.
/// </summary>
public class ToolMetaCacheTests
{
    private static ChatMetadataStore.CachedToolMeta Meta(long ts, string tool, string label) =>
        new() { Ts = ts, ToolName = tool, Label = label };

    // ── TryMatchCachedTool ──

    [Fact]
    public void TryMatch_NullCache_ReturnsNull()
    {
        Assert.Null(ChatMetadataStore.TryMatchCachedTool(null, 1000));
    }

    [Fact]
    public void TryMatch_EmptyCache_ReturnsNull()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        Assert.Null(ChatMetadataStore.TryMatchCachedTool(cache, 1000));
    }

    [Fact]
    public void TryMatch_SingleEntry_DequeuesAndReturns()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "ls -la"));

        var result = ChatMetadataStore.TryMatchCachedTool(cache, 200);

        Assert.NotNull(result);
        Assert.Equal("bash", result!.ToolName);
        Assert.Equal("ls -la", result.Label);
        Assert.Empty(cache); // consumed
    }

    [Fact]
    public void TryMatch_SequentialOrder_MatchesByPosition()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "first"));
        cache.Enqueue(Meta(200, "grep", "second"));
        cache.Enqueue(Meta(300, "view", "third"));

        // Each call should dequeue the next entry regardless of timestamp
        var r1 = ChatMetadataStore.TryMatchCachedTool(cache, 500);
        var r2 = ChatMetadataStore.TryMatchCachedTool(cache, 600);
        var r3 = ChatMetadataStore.TryMatchCachedTool(cache, 700);

        Assert.Equal("bash", r1!.ToolName);
        Assert.Equal("grep", r2!.ToolName);
        Assert.Equal("view", r3!.ToolName);
        Assert.Empty(cache);
    }

    [Fact]
    public void TryMatch_MoreHistoryThanCache_ReturnsNullWhenExhausted()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "only entry"));

        var r1 = ChatMetadataStore.TryMatchCachedTool(cache, 200);
        var r2 = ChatMetadataStore.TryMatchCachedTool(cache, 300);

        Assert.NotNull(r1);
        Assert.Null(r2); // exhausted
    }

    [Fact]
    public void TryMatch_CachedEntryFarAfterHistory_SkipsMatch()
    {
        // Cache entry is >5 minutes (300_000ms) after the history entry —
        // means this history tool result predates the cache.
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(500_000, "bash", "future entry"));

        var result = ChatMetadataStore.TryMatchCachedTool(cache, 100_000);

        Assert.Null(result);
        Assert.Single(cache); // NOT consumed — entry stays for later
    }

    [Fact]
    public void TryMatch_CachedEntrySlightlyAfterHistory_StillMatches()
    {
        // Cache entry is <5 min after history — normal SSE delay, should match.
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(200_000, "bash", "recent entry"));

        var result = ChatMetadataStore.TryMatchCachedTool(cache, 100_000);

        Assert.NotNull(result);
        Assert.Equal("bash", result!.ToolName);
    }

    [Fact]
    public void TryMatch_ZeroTimestamps_AlwaysMatch()
    {
        // When timestamps are 0, the guard is skipped — always dequeue.
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(0, "bash", "no timestamp"));

        var result = ChatMetadataStore.TryMatchCachedTool(cache, 0);

        Assert.NotNull(result);
    }

    [Fact]
    public void TryMatch_RepeatedToolNames_PreservesOrder()
    {
        // Multiple entries with the same tool name should be matched in order.
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "first bash"));
        cache.Enqueue(Meta(200, "bash", "second bash"));
        cache.Enqueue(Meta(300, "bash", "third bash"));

        var r1 = ChatMetadataStore.TryMatchCachedTool(cache, 500);
        var r2 = ChatMetadataStore.TryMatchCachedTool(cache, 600);

        Assert.Equal("first bash", r1!.Label);
        Assert.Equal("second bash", r2!.Label);
    }

    [Fact]
    public void TryMatchByCallId_MiddleMatchPreservesUnmatchedOrder()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>(
        [
            new() { Ts = 100, ToolName = "read", Label = "first", ToolCallId = "call-a" },
            new() { Ts = 200, ToolName = "bash", Label = "middle", ToolCallId = "call-b" },
            new() { Ts = 300, ToolName = "write", Label = "last", ToolCallId = "call-c" },
        ]);

        var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
            cache,
            "call-b",
            historyTsMs: 200);

        Assert.Equal(
            ChatMetadataStore.CachedToolLookupOutcome.Matched,
            lookup.Outcome);
        var match = lookup.Match;
        Assert.Equal("bash", match!.ToolName);
        Assert.Equal("middle", match.Label);
        Assert.Equal(
            ["call-a", "call-c"],
            cache.Select(entry => entry.ToolCallId));
    }

    [Fact]
    public void TryMatchByCallId_MissingMatchPreservesEntireQueue()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>(
        [
            new() { Ts = 100, ToolCallId = "call-a" },
            new() { Ts = 200, ToolCallId = "call-b" },
        ]);

        var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
            cache,
            "missing",
            historyTsMs: 200);

        Assert.Equal(
            ChatMetadataStore.CachedToolLookupOutcome.Unmatched,
            lookup.Outcome);
        Assert.Null(lookup.Match);
        Assert.Equal(
            ["call-a", "call-b"],
            cache.Select(entry => entry.ToolCallId));
    }

    [Fact]
    public void TryMatchByCallId_ReusedIdChoosesNearestTimestampAndPreservesOrder()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>(
        [
            new() { Ts = 0, ToolName = "read", Label = "old", ToolCallId = "same" },
            new() { Ts = 200, ToolName = "write", Label = "other", ToolCallId = "other" },
            new() { Ts = 300, ToolName = "bash", Label = "current", ToolCallId = "same" },
            new() { Ts = 400, ToolName = "edit", Label = "last", ToolCallId = "last" },
        ]);

        var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
            cache,
            "same",
            historyTsMs: 290);

        Assert.Equal(
            ChatMetadataStore.CachedToolLookupOutcome.Matched,
            lookup.Outcome);
        var match = lookup.Match;
        Assert.Equal("bash", match!.ToolName);
        Assert.Equal("current", match.Label);
        Assert.Equal(
            ["same", "other", "last"],
            cache.Select(entry => entry.ToolCallId));
    }

    [Fact]
    public void TryMatchByCallId_EmptyCacheReportsNoCandidates()
    {
        var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
            new Queue<ChatMetadataStore.CachedToolMeta>(),
            "call-a",
            historyTsMs: 100);

        Assert.Equal(
            ChatMetadataStore.CachedToolLookupOutcome.NoCandidates,
            lookup.Outcome);
        Assert.Null(lookup.Match);
    }

    [Fact]
    public void TryMatchByCallId_MatchBehindNullIdHeadReportsMatched()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>(
        [
            new() { Ts = 100, ToolName = "bash" },
            new() { Ts = 200, ToolName = "read", ToolCallId = "call-b" },
        ]);

        var lookup = ChatMetadataStore.TryMatchCachedToolByCallId(
            cache,
            "call-b",
            historyTsMs: 200);

        Assert.Equal(
            ChatMetadataStore.CachedToolLookupOutcome.Matched,
            lookup.Outcome);
        Assert.Equal(200, lookup.Match!.Ts);
        Assert.Null(Assert.Single(cache).ToolCallId);
    }

    // ── Constants ──

    [Fact]
    public void SessionLimits_AreReasonable()
    {
        Assert.Equal(20, ChatMetadataStore.MaxCachedSessions);
        Assert.Equal(500, ChatMetadataStore.MaxToolEntriesPerSession);
    }

    [Fact]
    public async Task CacheToolMeta_ConcurrentAdds_FlushesCompleteValidJson()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        Parallel.For(0, 100, i =>
            store.CacheTool("main", "session-1", 0, 1_000 + i, "bash", $"echo {i}"));

        store.Flush();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(json);

        Assert.NotNull(cache);
        Assert.True(cache!.TryGetValue("session-1", out var entries));
        Assert.Equal(100, entries!.Count);
        Assert.Empty(Directory.EnumerateFiles(tempDir.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CacheToolMeta_PersistsReadableJsonWithoutUnicodeOrNewlineEscapes()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        store.CacheTool(
            "main",
            "session-1",
            0,
            1_000,
            "bash",
            "exec search \"duplicate\" -> {\"timestamp\":\"2025-01-01T00:00:00+00:00\",\"message\":\"line1\r\n      line2\"}");

        store.Flush();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(json);
        var entry = Assert.Single(cache!["session-1"]);

        Assert.DoesNotContain("\\u0022", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\r\\n", json, StringComparison.Ordinal);
        Assert.Contains("+00:00", json, StringComparison.Ordinal);
        Assert.Contains("\\\"duplicate\\\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', entry.Label);
        Assert.DoesNotContain('\n', entry.Label);
        Assert.Contains("line1       line2", entry.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constructor_DoesNotRewriteLegacyEscapedToolMetaCache()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        const string legacyJson = """
            {
              "session-1": [
                {
                  "Ts": 1000,
                  "ToolName": "bash",
                  "Label": "exec \u0022duplicate\u0022 at 2025-01-01T00:00:00\u002B00:00\r\n      next line"
                }
              ]
            }
            """;
        File.WriteAllText(cachePath, legacyJson);

        using (var store = new ChatMetadataStore(cachePath))
        {
        }

        var json = File.ReadAllText(cachePath);
        Assert.Equal(legacyJson, json);
        Assert.Contains("\\u0022", json, StringComparison.Ordinal);
        Assert.Contains("\\u002B", json, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheToolMeta_WithoutSessionId_FallsBackToThreadKey()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        store.CacheTool("main", "main", 0, 1_000, "bash", "echo after reset");

        store.Flush();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(json);

        Assert.NotNull(cache);
        Assert.True(cache!.TryGetValue("main", out var entries));
        var entry = Assert.Single(entries!);
        Assert.Equal("bash", entry.ToolName);
        Assert.Equal("echo after reset", entry.Label);
    }

    [Fact]
    public void CacheToolMeta_SameIdAcrossRunsPersistsDistinctRecords()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        store.CacheTool(
            "main",
            "main",
            0,
            1_000,
            "Bash",
            "first",
            toolCallId: "tool-1",
            runId: "run-1");
        store.CacheTool(
            "main",
            "main",
            0,
            2_000,
            "Apply Patch",
            "second",
            toolCallId: "tool-1",
            runId: "run-2");
        store.CacheTool(
            "main",
            "main",
            0,
            2_100,
            "Apply Patch",
            "upgraded second",
            toolCallId: "tool-1",
            identityStrength: ChatToolIdentityStrength.Explicit,
            runId: "run-2");

        store.Flush();

        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
            File.ReadAllText(cachePath));
        Assert.Collection(
            cache!["main"],
            first =>
            {
                Assert.Equal("run-1", first.RunId);
                Assert.Equal("first", first.Label);
            },
            second =>
            {
                Assert.Equal("run-2", second.RunId);
                Assert.Equal("upgraded second", second.Label);
            });
    }

    [Fact]
    public void CacheToolMeta_LegacyIdReuseAcrossTurnsPersistsDistinctRecords()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        store.CacheTool(
            "main",
            "main",
            0,
            1_000,
            "Bash",
            "first",
            toolCallId: "tool-1",
            legacyTurn: 1);
        store.CacheTool(
            "main",
            "main",
            0,
            2_000,
            "Apply Patch",
            "second",
            toolCallId: "tool-1",
            legacyTurn: 2);

        store.Flush();

        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
            File.ReadAllText(cachePath));
        Assert.Collection(
            cache!["main"],
            first => Assert.Equal(1, first.LegacyTurn),
            second => Assert.Equal(2, second.LegacyTurn));
    }

    [Fact]
    public void BuildToolMetadataQueue_MergesStableCrossKeyIdentity()
    {
        var sessionEntries = new[]
        {
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 100,
                ToolName = "Tool",
                Label = "starting",
                ToolCallId = "tool-1",
                RunId = "run-1",
                IdentityStrength = ChatToolIdentityStrength.Fallback,
                ToolArgs = new JsonObject { ["command"] = "Get-Date" },
            },
        };
        var threadEntries = new[]
        {
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 110,
                ToolName = "Bash",
                Label = "finished",
                ToolCallId = "tool-1",
                RunId = "run-1",
                IdentityStrength = ChatToolIdentityStrength.Specific,
                ToolArgs = new JsonObject { ["path"] = "src" },
            },
        };

        var queue = ChatMetadataStore.BuildToolMetadataQueue(
            sessionEntries,
            threadEntries);

        var merged = Assert.Single(queue!);
        Assert.Equal(100, merged.Ts);
        Assert.Equal("Bash", merged.ToolName);
        Assert.Equal("finished", merged.Label);
        Assert.Equal(
            "Get-Date",
            merged.ToolArgs!["command"]!.GetValue<string>());
        Assert.Equal("src", merged.ToolArgs["path"]!.GetValue<string>());
    }

    [Fact]
    public void BuildToolMetadataQueue_ReusedIdsAcrossScopesRemainDistinct()
    {
        var entries = new[]
        {
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 1,
                ToolCallId = "same",
                RunId = "run-1",
            },
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 2,
                ToolCallId = "same",
                RunId = "run-2",
            },
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 3,
                ToolCallId = "same",
                LegacyTurn = 1,
            },
            new ChatMetadataStore.CachedToolMeta
            {
                Ts = 4,
                ToolCallId = "same",
                LegacyTurn = 2,
            },
        };

        var queue = ChatMetadataStore.BuildToolMetadataQueue(entries, null);

        Assert.Equal([1L, 2L, 3L, 4L], queue!.Select(entry => entry.Ts));
    }

    [Fact]
    public void CachedToolMeta_LegacyJsonWithoutScopeMigratesToNullRunAndZeroTurn()
    {
        const string json = """
            {
              "Ts": 1000,
              "ToolName": "Bash",
              "Label": "legacy",
              "ToolCallId": "tool-1"
            }
            """;

        var entry = JsonSerializer.Deserialize<ChatMetadataStore.CachedToolMeta>(json);

        Assert.NotNull(entry);
        Assert.Null(entry!.RunId);
        Assert.Equal(0, entry.LegacyTurn);
        Assert.Equal("tool-1", entry.ToolCallId);
    }

    [Fact]
    public void Dispose_RejectsLateCacheAdds()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var store = new ChatMetadataStore(cachePath);
        store.CacheTool("main", "session-1", 0, 1_000, "bash", "before dispose");

        store.Dispose();
        store.CacheTool("main", "session-1", 0, 2_000, "bash", "after dispose");

        var cache = JsonSerializer.Deserialize<
            Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
                File.ReadAllText(cachePath));
        var entry = Assert.Single(cache!["session-1"]);
        Assert.Equal("before dispose", entry.Label);
    }

    [Fact]
    public void TryMatch_NormalizesLegacyCachedNewlines()
    {
        var cache = new Queue<ChatMetadataStore.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash\r\nname", "line1\r\n      \"line2\""));

        var result = ChatMetadataStore.TryMatchCachedTool(cache, 200);

        Assert.Equal("bash name", result!.ToolName);
        Assert.Equal("line1       \"line2\"", result.Label);
    }

    [Fact]
    public void CacheToolMeta_SameToolCallId_UpgradesSpecificIdentityWithoutDuplicate()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);

        store.CacheTool(
            "main",
            "main",
            0,
            100,
            "Tool",
            "Tool",
            "tool-1",
            identityStrength: ChatToolIdentityStrength.Fallback);
        store.CacheTool(
            "main",
            "main",
            0,
            110,
            "Bash",
            "Get-Date",
            "tool-1",
            new System.Text.Json.Nodes.JsonObject { ["command"] = "Get-Date" },
            ChatToolIdentityStrength.Specific);
        store.Flush();

        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
            File.ReadAllText(cachePath));
        var entry = Assert.Single(cache!["main"]);
        Assert.Equal("Bash", entry.ToolName);
        Assert.Equal("Get-Date", entry.ToolArgs!["command"]!.GetValue<string>());
        Assert.Equal(ChatToolIdentityStrength.Specific, entry.IdentityStrength);
    }

    [Fact]
    public void Reset_DoesNotReseedClearedSessionIdFromStaleSessionsList()
    {
        var state = new ChatConversationState(
            ConnectionStatus.Connected,
            lastChatState: null,
            seedModels: null);
        var context = new ChatProjectionContext(
            MainSessionKey: "main",
            HasHandshakeSnapshot: true);
        SessionInfo[] staleSessions =
        [
            new SessionInfo
            {
                Key = "main",
                IsMain = true,
                SessionId = "old-session"
            }
        ];

        state.ApplySessions(staleSessions, context);
        Assert.Equal("old-session", state.ResolveMetadataKey("main").CacheKey);
        state.ResetThread("main", context);
        state.ApplySessions(staleSessions, context);

        Assert.Equal("main", state.ResolveMetadataKey("main").CacheKey);
    }

    [Fact]
    public void ResetEviction_DropsStaleGenerationAndAcceptsCurrentThreadKey()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);
        store.CacheTool("main", "old-session", 0, 900, "bash", "stale tool");
        store.EvictReset("main", "old-session", resetGeneration: 1);
        store.CacheTool("main", "old-session", 0, 950, "bash", "late stale tool");
        store.CacheTool("main", "main", 1, 1_000, "bash", "echo after reset");
        store.Flush();

        var cache = JsonSerializer.Deserialize<
            Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
                File.ReadAllText(cachePath));

        Assert.NotNull(cache);
        Assert.False(cache!.ContainsKey("old-session"));
        Assert.True(cache.TryGetValue("main", out var entries));
        Assert.Equal("echo after reset", Assert.Single(entries!).Label);
    }

    [Fact]
    public void ResetEviction_PreservesCurrentGenerationAddedBeforeEviction()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);
        store.CacheTool("main", "main", 0, 900, "bash", "stale tool");
        store.CacheTool("main", "main", 1, 1_000, "bash", "current tool");

        store.EvictReset("main", oldSessionId: null, resetGeneration: 1);
        store.Flush();

        var cache = JsonSerializer.Deserialize<
            Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(
                File.ReadAllText(cachePath));
        var entry = Assert.Single(cache!["main"]);
        Assert.Equal("current tool", entry.Label);
    }

    [Fact]
    public void CurrentGenerationRead_FiltersOlderMetadataBeforeEviction()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        using var store = new ChatMetadataStore(cachePath);
        store.CacheTool("main", "main", 0, 900, "bash", "stale tool");
        store.CacheTool("main", "main", 1, 1_000, "bash", "current tool");

        var entries = store.GetToolMetadata(
            sessionId: null,
            threadId: "main",
            resetGeneration: 1);

        var entry = Assert.Single(entries!);
        Assert.Equal("current tool", entry.Label);
    }

    [Fact]
    public async Task Reset_PersistsClearedToolMetaWhenCacheWasClean()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        const string initialJson = """
            {
              "old-session": [
                {
                  "Ts": 1000,
                  "ToolName": "bash",
                  "Label": "stale tool"
                }
              ]
            }
            """;
        File.WriteAllText(cachePath, initialJson);
        var bridge = new FakeBridge
        {
            History = new ChatHistoryInfo
            {
                SessionKey = "main",
                SessionId = "old-session"
            }
        };
        var provider = new OpenClawChatDataProvider(bridge, post: null, toolMetaCacheFilePath: cachePath);
        await provider.LoadHistoryAsync("main");

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<ChatMetadataStore.CachedToolMeta>>>(json);

        Assert.NotEqual(initialJson, json);
        Assert.NotNull(cache);
        Assert.DoesNotContain("old-session", cache!.Keys);
    }

    private sealed class FakeBridge : IChatGatewayBridge
    {
        public bool IsConnected { get; set; }
        public ConnectionStatus CurrentStatus { get; set; }
        public string? MainSessionKey { get; set; }
        public bool HasHandshakeSnapshot { get; set; }
        public ChatHistoryInfo History { get; set; } = new() { SessionKey = "main" };

        public SessionInfo[] GetSessionList() => Array.Empty<SessionInfo>();
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }
        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) => Task.FromResult(new CommandCatalog { IsSupported = true });
        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) => Task.CompletedTask;
        public Task<ChatSendResult> SendChatMessageForRunAsync(
            string message,
            string? sessionKey,
            string? sessionId,
            IReadOnlyList<ChatAttachment>? attachments = null,
            string? idempotencyKey = null) => Task.FromResult(new ChatSendResult());
        public Task PatchSessionModelAsync(string sessionKey, string model) => Task.CompletedTask;
        public Task ClearSessionModelAsync(string sessionKey) => Task.CompletedTask;
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => Task.CompletedTask;
        public Task ClearSessionThinkingLevelAsync(string sessionKey) => Task.CompletedTask;
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) => Task.FromResult(History);
        public Task SendChatAbortAsync(string runId, string? sessionKey = null) => Task.CompletedTask;
        public Task ResolveExecApprovalAsync(string approvalId, string decision) => Task.CompletedTask;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public void RaiseStatus(ConnectionStatus status) => StatusChanged?.Invoke(this, status);
        public void RaiseSessions(SessionInfo[] sessions) => SessionsUpdated?.Invoke(this, sessions);
        public void RaiseSessionCommandCompleted(SessionCommandResult result) => SessionCommandCompleted?.Invoke(this, result);
        public void RaiseChat(ChatMessageInfo message) => ChatMessageReceived?.Invoke(this, message);
        public void RaiseAgent(AgentEventInfo evt) => AgentEventReceived?.Invoke(this, evt);
        public void RaiseModels(ModelsListInfo models) => ModelsListUpdated?.Invoke(this, models);
        public void Dispose() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "openclaw-tool-meta-" + Guid.NewGuid().ToString("N"));

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
