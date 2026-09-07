using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed class AttachmentMetaMatcher
{
    private static readonly TimeSpan MatchWindow = TimeSpan.FromHours(24);
    private readonly List<ChatMetadataStore.CachedAttachmentMeta> _entries;
    private readonly bool[] _used;

    public AttachmentMetaMatcher(List<ChatMetadataStore.CachedAttachmentMeta> entries)
    {
        _entries = entries;
        _used = new bool[entries.Count];
    }

    public ChatMetadataStore.CachedAttachmentMeta? TryMatch(
        string text,
        string attachmentCorrelationSignature,
        long historyTsMs)
    {
        if (string.IsNullOrEmpty(attachmentCorrelationSignature))
            return null;

        for (var i = 0; i < _entries.Count; i++)
        {
            if (_used[i])
                continue;

            var entry = _entries[i];
            if (!string.Equals(entry.Text, text, StringComparison.Ordinal))
                continue;
            if (!string.Equals(
                    GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(
                        ChatMetadataStore.CreatePersistedLocalPresentations(
                            entry.Attachments)),
                    attachmentCorrelationSignature,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (historyTsMs > 0 && entry.Ts > 0 &&
                Math.Abs(historyTsMs - entry.Ts) > MatchWindow.TotalMilliseconds)
            {
                continue;
            }

            _used[i] = true;
            return entry;
        }

        return null;
    }
}

/// <summary>
/// Owns the live tool and attachment metadata caches, their persistence
/// lifecycle, and attachment marker security/rehydration.
/// </summary>
internal sealed class ChatMetadataStore : IDisposable
{
    internal enum CachedToolLookupOutcome
    {
        NoCandidates,
        Matched,
        Unmatched,
    }

    internal readonly record struct CachedToolLookup(
        CachedToolMeta? Match,
        CachedToolLookupOutcome Outcome);

    private readonly record struct CachedToolCorrelationIdentity(
        string? RunId,
        string ToolCallId,
        long LegacyTurn);

    internal sealed class CachedToolMeta
    {
        public long Ts { get; set; }
        public string ToolName { get; set; } = "";
        public string Label { get; set; } = "";
        public string? ToolCallId { get; set; }
        public string? RunId { get; set; }
        public long LegacyTurn { get; set; }
        public JsonObject? ToolArgs { get; set; }
        public ChatToolIdentityStrength IdentityStrength { get; set; } =
            ChatToolIdentityStrength.Heuristic;
        [JsonIgnore] public string ThreadId { get; set; } = "";
        [JsonIgnore] public long ResetGeneration { get; set; }
    }

    internal sealed class CachedAttachmentMeta
    {
        public long Ts { get; set; }
        public string Text { get; set; } = "";
        public List<CachedAttachmentItem> Attachments { get; set; } = [];
        [JsonIgnore] public string ThreadId { get; set; } = "";
        [JsonIgnore] public long ResetGeneration { get; set; }
    }

    internal sealed class CachedAttachmentItem
    {
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public bool IsImage { get; set; }
    }

    internal const int MaxCachedSessions = 20;
    internal const int MaxToolEntriesPerSession = 500;
    internal const int MaxAttachmentEntriesPerSession = 500;

    internal static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly string LastChatStateFilePath = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "last-chat-state.json");

    internal static readonly string AbortedIdsFilePath = Path.Combine(
        AppIdentity.ResolveLocalDataDirectory(), "aborted-messages.json");

    private readonly object _gate = new();
    private readonly object _toolSaveGate = new();
    private readonly object _attachmentSaveGate = new();
    private readonly string _toolCacheFilePath;
    private readonly string _attachmentCacheFilePath;
    private readonly Action<long>? _attachmentSnapshotCaptured;
    private Dictionary<string, List<CachedToolMeta>> _toolCache;
    private Dictionary<string, List<CachedAttachmentMeta>> _attachmentCache;
    private readonly Dictionary<string, long> _evictedResetGenerations = new(StringComparer.Ordinal);
    private Timer? _toolSaveTimer;
    private long _toolSaveVersion;
    private long _attachmentSaveVersion;
    private bool _toolCacheDirty;
    private bool _attachmentCacheDirty;
    private bool _disposed;

    internal ChatMetadataStore(
        string toolCacheFilePath,
        string? attachmentCacheFilePath = null,
        Action<long>? attachmentSnapshotCaptured = null)
    {
        _toolCacheFilePath = !string.IsNullOrWhiteSpace(toolCacheFilePath)
            ? toolCacheFilePath
            : throw new ArgumentException("Tool metadata cache path is required.", nameof(toolCacheFilePath));
        _attachmentCacheFilePath = !string.IsNullOrWhiteSpace(attachmentCacheFilePath)
            ? attachmentCacheFilePath
            : DefaultAttachmentMetaCacheFilePath(_toolCacheFilePath);
        _attachmentSnapshotCaptured = attachmentSnapshotCaptured;
        _toolCache = LoadToolMetaCache(_toolCacheFilePath);
        _attachmentCache = LoadAttachmentMetaCache(_attachmentCacheFilePath);
    }

    internal static string DefaultToolMetaCacheFilePath =>
        Path.Combine(AppIdentity.ResolveLocalDataDirectory(), "tool-metadata.json");

    internal static string DefaultAttachmentMetaCacheFilePath(string toolMetaCacheFilePath)
    {
        var dir = Path.GetDirectoryName(toolMetaCacheFilePath);
        return Path.Combine(
            string.IsNullOrEmpty(dir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : dir,
            "attachment-metadata.json");
    }

    internal void CacheTool(
        string threadId,
        string cacheKey,
        long resetGeneration,
        long tsMs,
        string toolName,
        string label,
        string? toolCallId = null,
        JsonObject? toolArgs = null,
        ChatToolIdentityStrength identityStrength =
            ChatToolIdentityStrength.Heuristic,
        string? runId = null,
        long legacyTurn = 0)
    {
        Timer? timerToDispose;
        long saveVersion;
        runId = string.IsNullOrWhiteSpace(runId) ? null : runId;
        lock (_gate)
        {
            if (_disposed || IsStaleResetGenerationLocked(threadId, resetGeneration))
                return;

            if (!_toolCache.TryGetValue(cacheKey, out var list))
            {
                list = [];
                _toolCache[cacheKey] = list;
            }

            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                var existing = list.FindLast(entry =>
                    string.Equals(
                        entry.ToolCallId,
                        toolCallId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        entry.RunId,
                        runId,
                        StringComparison.Ordinal) &&
                    (runId is not null || entry.LegacyTurn == legacyTurn));
                if (existing is not null)
                {
                    if (identityStrength > existing.IdentityStrength)
                    {
                        existing.ToolName =
                            NormalizeCachedDisplayText(toolName);
                        existing.IdentityStrength = identityStrength;
                    }
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        existing.Label =
                            NormalizeCachedDisplayText(label);
                    }
                    existing.ToolArgs = MergeCachedToolArgs(
                        existing.ToolArgs,
                        toolArgs);
                    ScheduleToolSaveLocked(
                        out saveVersion,
                        out timerToDispose);
                    goto ExitLock;
                }
            }
            else if (list.Count > 0 &&
                     list[^1].Ts == tsMs &&
                     list[^1].ToolName == toolName)
            {
                return;
            }

            list.Add(new CachedToolMeta
            {
                Ts = tsMs,
                ToolName = NormalizeCachedDisplayText(toolName),
                Label = NormalizeCachedDisplayText(label),
                ToolCallId = toolCallId,
                RunId = runId,
                LegacyTurn = runId is null ? legacyTurn : 0,
                ToolArgs = NormalizeCachedToolArgs(toolArgs),
                IdentityStrength = identityStrength,
                ThreadId = threadId,
                ResetGeneration = resetGeneration,
            });
            if (list.Count > MaxToolEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxToolEntriesPerSession);

            ScheduleToolSaveLocked(
                out saveVersion,
                out timerToDispose);
        ExitLock:
            ;
        }

        timerToDispose?.Dispose();
    }

    internal void CacheTool(ChatToolMetadataWrite metadata) =>
        CacheTool(
            metadata.ThreadId,
            metadata.CacheKey,
            metadata.ResetGeneration,
            metadata.TimestampMs,
            metadata.ToolName,
            metadata.Label,
            metadata.ToolCallId,
            metadata.ToolArgs,
            metadata.IdentityStrength,
            metadata.RunId,
            metadata.LegacyTurn);

    private void ScheduleToolSaveLocked(
        out long saveVersion,
        out Timer? timerToDispose)
    {
        _toolCacheDirty = true;
        var version = ++_toolSaveVersion;
        saveVersion = version;
        timerToDispose = _toolSaveTimer;
        _toolSaveTimer = new Timer(
            _ => SaveToolCache(version),
            null,
            TimeSpan.FromMilliseconds(500),
            Timeout.InfiniteTimeSpan);
    }

    internal void CacheAttachments(
        string threadId,
        string? sessionId,
        long resetGeneration,
        string text,
        IReadOnlyList<ChatAttachment> attachments,
        long tsMs)
    {
        if (attachments.Count == 0)
            return;

        var items = attachments
            .Where(attachment => !string.IsNullOrWhiteSpace(attachment.FileName))
            .Select(attachment =>
            {
                var mimeType =
                    GatewayMediaMessageProjection.NormalizeMimeType(
                        attachment.MimeType);
                return new CachedAttachmentItem
                {
                    FileName = NormalizeCachedDisplayText(attachment.FileName),
                    MimeType = mimeType,
                    IsImage = string.Equals(
                            attachment.Type,
                            "image",
                            StringComparison.OrdinalIgnoreCase) ||
                        mimeType.StartsWith("image/", StringComparison.Ordinal),
                };
            })
            .ToList();
        if (items.Count == 0)
            return;

        long saveVersion;
        lock (_gate)
        {
            if (_disposed || IsStaleResetGenerationLocked(threadId, resetGeneration))
                return;

            var key = !string.IsNullOrEmpty(sessionId) ? sessionId : threadId;
            if (!_attachmentCache.TryGetValue(key, out var list))
            {
                list = [];
                _attachmentCache[key] = list;
            }

            list.Add(new CachedAttachmentMeta
            {
                Ts = tsMs,
                Text = NormalizeCachedDisplayText(
                    ChatContentFormatting.TruncateForChatEntry(
                        EscapeUntrustedAttachmentMarkerLines(text))),
                Attachments = items,
                ThreadId = threadId,
                ResetGeneration = resetGeneration,
            });
            if (list.Count > MaxAttachmentEntriesPerSession)
                list.RemoveRange(0, list.Count - MaxAttachmentEntriesPerSession);
            _attachmentCacheDirty = true;
            saveVersion = ++_attachmentSaveVersion;
        }

        SaveAttachmentCache(saveVersion);
    }

    internal Queue<CachedToolMeta>? GetToolMetadata(
        string? sessionId,
        string threadId,
        long resetGeneration)
    {
        if (string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(threadId))
            return null;

        lock (_gate)
        {
            IReadOnlyList<CachedToolMeta>? sessionEntries = null;
            IReadOnlyList<CachedToolMeta>? threadEntries = null;
            if (!string.IsNullOrEmpty(sessionId) &&
                _toolCache.TryGetValue(sessionId, out var cachedSessionEntries))
            {
                sessionEntries = cachedSessionEntries
                    .Where(entry => !IsOlderResetEntry(
                        entry.ThreadId,
                        entry.ResetGeneration,
                        threadId,
                        resetGeneration))
                    .ToArray();
            }

            if (!string.IsNullOrEmpty(threadId) &&
                (string.IsNullOrEmpty(sessionId) || !string.Equals(sessionId, threadId, StringComparison.Ordinal)) &&
                _toolCache.TryGetValue(threadId, out var cachedThreadEntries))
            {
                threadEntries = cachedThreadEntries
                    .Where(entry => !IsOlderResetEntry(
                        entry.ThreadId,
                        entry.ResetGeneration,
                        threadId,
                        resetGeneration))
                    .ToArray();
            }

            return BuildToolMetadataQueue(sessionEntries, threadEntries);
        }
    }

    internal static Queue<CachedToolMeta>? BuildToolMetadataQueue(
        IReadOnlyList<CachedToolMeta>? sessionEntries,
        IReadOnlyList<CachedToolMeta>? threadEntries)
    {
        var merged = new List<CachedToolMeta>();
        var stableIdentities =
            new Dictionary<CachedToolCorrelationIdentity, int>();
        var ordered = (sessionEntries ?? Array.Empty<CachedToolMeta>())
            .Concat(threadEntries ?? Array.Empty<CachedToolMeta>())
            .OrderBy(entry => entry.Ts);

        foreach (var source in ordered)
        {
            var entry = Clone(source);
            if (!TryGetCorrelationIdentity(entry, out var identity) ||
                !stableIdentities.TryGetValue(identity, out var existingIndex))
            {
                if (TryGetCorrelationIdentity(entry, out identity))
                    stableIdentities[identity] = merged.Count;
                merged.Add(entry);
                continue;
            }

            MergeToolMetadata(merged[existingIndex], entry);
        }

        return merged.Count == 0
            ? null
            : new Queue<CachedToolMeta>(merged);
    }

    internal AttachmentMetaMatcher CreateAttachmentMatcher(
        string? sessionId,
        string threadId,
        long resetGeneration)
    {
        var entries = new List<CachedAttachmentMeta>();
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(sessionId) &&
                _attachmentCache.TryGetValue(sessionId, out var sessionEntries))
            {
                entries.AddRange(sessionEntries
                    .Where(entry => !IsOlderResetEntry(
                        entry.ThreadId,
                        entry.ResetGeneration,
                        threadId,
                        resetGeneration))
                    .Select(Clone));
            }

            if (!string.IsNullOrEmpty(threadId) &&
                (string.IsNullOrEmpty(sessionId) || !string.Equals(sessionId, threadId, StringComparison.Ordinal)) &&
                _attachmentCache.TryGetValue(threadId, out var threadEntries))
            {
                entries.AddRange(threadEntries
                    .Where(entry => !IsOlderResetEntry(
                        entry.ThreadId,
                        entry.ResetGeneration,
                        threadId,
                        resetGeneration))
                    .Select(Clone));
            }
        }

        return new AttachmentMetaMatcher(entries.OrderBy(entry => entry.Ts).ToList());
    }

    private static bool IsOlderResetEntry(
        string? entryThreadId,
        long entryResetGeneration,
        string threadId,
        long resetGeneration) =>
        (string.IsNullOrEmpty(entryThreadId) ||
         string.Equals(entryThreadId, threadId, StringComparison.Ordinal)) &&
        entryResetGeneration < resetGeneration;

    internal void EvictReset(string threadId, string? oldSessionId, long resetGeneration)
    {
        var saveTool = false;
        var saveAttachments = false;
        long attachmentSaveVersion = 0;
        lock (_gate)
        {
            if (_evictedResetGenerations.TryGetValue(threadId, out var current) &&
                current >= resetGeneration)
            {
                return;
            }

            _evictedResetGenerations[threadId] = resetGeneration;
            if (!string.IsNullOrEmpty(oldSessionId))
            {
                saveTool = RemoveOlderToolEntries(
                    oldSessionId,
                    threadId,
                    resetGeneration);
                saveAttachments = RemoveOlderAttachmentEntries(
                    oldSessionId,
                    threadId,
                    resetGeneration);
            }

            saveTool = RemoveOlderToolEntries(
                threadId,
                threadId,
                resetGeneration) || saveTool;
            saveAttachments = RemoveOlderAttachmentEntries(
                threadId,
                threadId,
                resetGeneration) || saveAttachments;
            if (saveTool)
            {
                _toolCacheDirty = true;
                _toolSaveVersion++;
            }
            if (saveAttachments)
            {
                _attachmentCacheDirty = true;
                attachmentSaveVersion = ++_attachmentSaveVersion;
            }
        }

        if (saveTool)
            SaveToolCache();
        if (saveAttachments)
            SaveAttachmentCache(attachmentSaveVersion);
    }

    private bool RemoveOlderToolEntries(
        string cacheKey,
        string threadId,
        long resetGeneration)
    {
        if (!_toolCache.TryGetValue(cacheKey, out var entries))
            return false;
        var removed = entries.RemoveAll(entry =>
            (string.IsNullOrEmpty(entry.ThreadId) ||
             string.Equals(entry.ThreadId, threadId, StringComparison.Ordinal)) &&
            entry.ResetGeneration < resetGeneration) > 0;
        if (entries.Count == 0)
            _toolCache.Remove(cacheKey);
        return removed;
    }

    private bool RemoveOlderAttachmentEntries(
        string cacheKey,
        string threadId,
        long resetGeneration)
    {
        if (!_attachmentCache.TryGetValue(cacheKey, out var entries))
            return false;
        var removed = entries.RemoveAll(entry =>
            (string.IsNullOrEmpty(entry.ThreadId) ||
             string.Equals(entry.ThreadId, threadId, StringComparison.Ordinal)) &&
            entry.ResetGeneration < resetGeneration) > 0;
        if (entries.Count == 0)
            _attachmentCache.Remove(cacheKey);
        return removed;
    }

    internal static CachedToolMeta? TryMatchCachedTool(
        Queue<CachedToolMeta>? cache,
        long historyTsMs)
    {
        if (cache is null || cache.Count == 0)
            return null;

        var candidate = cache.Peek();
        if (historyTsMs > 0 && candidate.Ts > 0 && candidate.Ts > historyTsMs + 300_000)
            return null;

        var match = cache.Dequeue();
        match.ToolName = NormalizeCachedDisplayText(match.ToolName);
        match.Label = NormalizeCachedDisplayText(match.Label);
        match.ToolArgs = NormalizeCachedToolArgs(match.ToolArgs);
        return match;
    }

    internal static CachedToolLookup TryMatchCachedToolByCallId(
        Queue<CachedToolMeta>? cache,
        string? toolCallId,
        long historyTsMs)
    {
        if (cache is null ||
            cache.Count == 0 ||
            string.IsNullOrWhiteSpace(toolCallId))
        {
            return new(null, CachedToolLookupOutcome.NoCandidates);
        }

        var entryCount = cache.Count;
        var entries = new CachedToolMeta[entryCount];
        var matchIndex = -1;
        var bestTimestampDistance = double.PositiveInfinity;
        for (var index = 0; index < entryCount; index++)
        {
            var candidate = cache.Dequeue();
            entries[index] = candidate;
            if (string.IsNullOrWhiteSpace(candidate.ToolCallId) ||
                !string.Equals(
                    candidate.ToolCallId,
                    toolCallId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var timestampDistance =
                historyTsMs > 0 && candidate.Ts > 0
                    ? Math.Abs((double)candidate.Ts - historyTsMs)
                    : double.PositiveInfinity;
            if (matchIndex < 0 ||
                timestampDistance < bestTimestampDistance)
            {
                matchIndex = index;
                bestTimestampDistance = timestampDistance;
            }
        }

        for (var index = 0; index < entryCount; index++)
        {
            if (index != matchIndex)
                cache.Enqueue(entries[index]);
        }

        if (matchIndex < 0)
            return new(null, CachedToolLookupOutcome.Unmatched);

        var match = entries[matchIndex];
        match.ToolName = NormalizeCachedDisplayText(match.ToolName);
        match.Label = NormalizeCachedDisplayText(match.Label);
        match.ToolArgs = NormalizeCachedToolArgs(match.ToolArgs);
        return new(match, CachedToolLookupOutcome.Matched);
    }

    internal void Flush()
    {
        Timer? timer;
        long attachmentSaveVersion;
        lock (_gate)
        {
            timer = _toolSaveTimer;
            _toolSaveTimer = null;
            _toolSaveVersion++;
            attachmentSaveVersion = _attachmentSaveVersion;
        }

        timer?.Dispose();
        SaveToolCache();
        SaveAttachmentCache(attachmentSaveVersion);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            timer = _toolSaveTimer;
            _toolSaveTimer = null;
            _toolSaveVersion++;
        }

        timer?.Dispose();
        SaveToolCache();
    }

    private bool IsStaleResetGenerationLocked(string threadId, long resetGeneration) =>
        _evictedResetGenerations.TryGetValue(threadId, out var evictedGeneration) &&
        resetGeneration < evictedGeneration;

    private void SaveToolCache(long? expectedVersion = null)
    {
        try
        {
            Dictionary<string, List<CachedToolMeta>> snapshot;
            lock (_gate)
            {
                if (expectedVersion is { } version &&
                    (version != _toolSaveVersion || _disposed))
                {
                    return;
                }
                if (!_toolCacheDirty)
                    return;

                snapshot = _toolCache.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(Clone).ToList(),
                    StringComparer.Ordinal);
            }

            EvictOldestSessions(snapshot);
            var json = JsonSerializer.Serialize(snapshot, CacheJsonOptions);
            lock (_toolSaveGate)
            {
                if (expectedVersion is { } version)
                {
                    lock (_gate)
                    {
                        if (version != _toolSaveVersion || _disposed)
                            return;
                    }
                }

                AtomicWrite(_toolCacheFilePath, json, "tool metadata");
                lock (_gate)
                {
                    if (expectedVersion is null || expectedVersion == _toolSaveVersion)
                        _toolCacheDirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Chat metadata cache could not be saved: {ex.Message}");
        }
    }

    private void SaveAttachmentCache(long expectedVersion)
    {
        try
        {
            Dictionary<string, List<CachedAttachmentMeta>> snapshot;
            lock (_gate)
            {
                if (expectedVersion != _attachmentSaveVersion ||
                    !_attachmentCacheDirty)
                {
                    return;
                }

                snapshot = _attachmentCache.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Select(Clone).ToList(),
                    StringComparer.Ordinal);
            }

            _attachmentSnapshotCaptured?.Invoke(expectedVersion);
            EvictOldestSessions(snapshot);
            var json = JsonSerializer.Serialize(snapshot, CacheJsonOptions);
            lock (_attachmentSaveGate)
            {
                lock (_gate)
                {
                    if (expectedVersion != _attachmentSaveVersion)
                        return;
                }

                AtomicWrite(_attachmentCacheFilePath, json, "attachment metadata");
                lock (_gate)
                {
                    if (expectedVersion == _attachmentSaveVersion)
                        _attachmentCacheDirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Attachment metadata cache could not be saved: {ex.Message}");
        }
    }

    private static void EvictOldestSessions<T>(Dictionary<string, List<T>> snapshot)
        where T : class
    {
        if (snapshot.Count <= MaxCachedSessions)
            return;

        static long Timestamp(T entry) => entry switch
        {
            CachedToolMeta tool => tool.Ts,
            CachedAttachmentMeta attachment => attachment.Ts,
            _ => 0,
        };

        var keys = snapshot
            .OrderBy(pair => pair.Value.Count > 0 ? Timestamp(pair.Value[^1]) : 0)
            .Take(snapshot.Count - MaxCachedSessions)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
            snapshot.Remove(key);
    }

    private static void AtomicWrite(string path, string json, string cacheName)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Logger.Debug($"{cacheName} temp file cleanup failed: {ex.Message}");
            }
        }
    }

    private static CachedToolMeta Clone(CachedToolMeta entry) => new()
    {
        Ts = entry.Ts,
        ToolName = NormalizeCachedDisplayText(entry.ToolName),
        Label = NormalizeCachedDisplayText(entry.Label),
        ToolCallId = entry.ToolCallId,
        RunId = entry.RunId,
        LegacyTurn = entry.LegacyTurn,
        ToolArgs = NormalizeCachedToolArgs(entry.ToolArgs),
        IdentityStrength = entry.IdentityStrength,
        ThreadId = entry.ThreadId,
        ResetGeneration = entry.ResetGeneration,
    };

    private static bool TryGetCorrelationIdentity(
        CachedToolMeta entry,
        out CachedToolCorrelationIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(entry.ToolCallId))
        {
            identity = default;
            return false;
        }

        var runId = string.IsNullOrWhiteSpace(entry.RunId)
            ? null
            : entry.RunId;
        identity = new CachedToolCorrelationIdentity(
            runId,
            entry.ToolCallId,
            runId is null ? entry.LegacyTurn : 0);
        return true;
    }

    private static void MergeToolMetadata(
        CachedToolMeta existing,
        CachedToolMeta incoming)
    {
        if (incoming.IdentityStrength > existing.IdentityStrength)
        {
            existing.ToolName = incoming.ToolName;
            existing.IdentityStrength = incoming.IdentityStrength;
        }
        else if (string.IsNullOrWhiteSpace(existing.ToolName))
        {
            existing.ToolName = incoming.ToolName;
        }

        if (!string.IsNullOrWhiteSpace(incoming.Label))
            existing.Label = incoming.Label;
        existing.ToolArgs = MergeCachedToolArgs(
            existing.ToolArgs,
            incoming.ToolArgs);
    }

    private static CachedAttachmentMeta Clone(CachedAttachmentMeta entry) => new()
    {
        Ts = entry.Ts,
        Text = NormalizeCachedDisplayText(entry.Text),
        ThreadId = entry.ThreadId,
        ResetGeneration = entry.ResetGeneration,
        Attachments = entry.Attachments.Select(attachment => new CachedAttachmentItem
        {
            FileName = NormalizeCachedDisplayText(attachment.FileName),
            MimeType = GatewayMediaMessageProjection.NormalizeMimeType(
                attachment.MimeType),
            IsImage = attachment.IsImage,
        }).ToList(),
    };

    internal static Dictionary<string, List<CachedToolMeta>> LoadToolMetaCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return [];
            var json = File.ReadAllText(cacheFilePath);
            var cache = JsonSerializer.Deserialize<Dictionary<string, List<CachedToolMeta>>>(json) ?? [];
            foreach (var entry in cache.Values.SelectMany(entries => entries))
            {
                entry.ToolName = NormalizeCachedDisplayText(entry.ToolName);
                entry.Label = NormalizeCachedDisplayText(entry.Label);
                entry.ToolArgs = NormalizeCachedToolArgs(entry.ToolArgs);
            }
            return cache;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Tool metadata cache could not be loaded: {ex.Message}");
            return [];
        }
    }

    internal static Dictionary<string, List<CachedAttachmentMeta>> LoadAttachmentMetaCache(string cacheFilePath)
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return [];
            var json = File.ReadAllText(cacheFilePath);
            var cache = JsonSerializer.Deserialize<Dictionary<string, List<CachedAttachmentMeta>>>(json) ?? [];
            foreach (var entry in cache.Values.SelectMany(entries => entries))
            {
                entry.Text = NormalizeCachedDisplayText(entry.Text);
                foreach (var attachment in entry.Attachments)
                {
                    attachment.FileName = NormalizeCachedDisplayText(attachment.FileName);
                    attachment.MimeType =
                        GatewayMediaMessageProjection.NormalizeMimeType(
                            attachment.MimeType);
                }
            }
            return cache;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Attachment metadata cache could not be loaded: {ex.Message}");
            return [];
        }
    }

    internal static string EscapeUntrustedAttachmentMarkerLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var lines = text.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmedStart = line.TrimStart();
            if (trimmedStart.StartsWith("\u200B🖼️ ", StringComparison.Ordinal) ||
                trimmedStart.StartsWith("\u200B📎 ", StringComparison.Ordinal))
            {
                var prefixLength = line.Length - trimmedStart.Length;
                lines[i] = string.Concat(line.AsSpan(0, prefixLength), trimmedStart.AsSpan(1));
                changed = true;
            }
        }

        return changed ? string.Join('\n', lines) : text;
    }

    internal static string BuildAttachmentMarkerLines(IEnumerable<ChatAttachment> attachments) =>
        string.Join("\n", attachments.Select(attachment =>
            string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase)
                ? $"\u200B🖼️ {attachment.FileName}"
                : $"\u200B📎 {attachment.FileName}"));

    internal static string BuildAttachmentMarkerLines(IEnumerable<CachedAttachmentItem> attachments) =>
        string.Join("\n", attachments.Select(attachment =>
            attachment.IsImage
                ? $"\u200B🖼️ {attachment.FileName}"
                : $"\u200B📎 {attachment.FileName}"));

    internal static IReadOnlyList<ChatAttachmentPresentation>
        CreatePersistedLocalPresentations(
            IEnumerable<CachedAttachmentItem> attachments) =>
        attachments.Select(attachment => new ChatAttachmentPresentation(
            ChatAttachmentOrigin.Local,
            GatewayMediaMessageProjection.NormalizeDisplayFileName(
                attachment.FileName),
            GatewayMediaMessageProjection.NormalizeMimeType(
                attachment.MimeType),
            attachment.IsImage,
            PreviewCacheKey: null)).ToArray();

    internal static string NormalizeCachedDisplayText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static JsonObject? NormalizeCachedToolArgs(JsonObject? args)
    {
        if (args is null)
            return null;

        var normalized = new JsonObject();
        foreach (var key in NativeToolProjector.DisplayArgumentKeys)
        {
            if (args[key] is JsonValue value &&
                value.TryGetValue<string>(out var text))
            {
                var safe = NativeToolProjector.SanitizeToolDisplayValue(
                    NormalizeCachedDisplayText(text));
                if (!string.IsNullOrWhiteSpace(safe))
                    normalized[key] = safe;
            }
        }
        return normalized.Count == 0 ? null : normalized;
    }

    private static JsonObject? MergeCachedToolArgs(
        JsonObject? existing,
        JsonObject? incoming)
    {
        var merged = NormalizeCachedToolArgs(existing) ?? new JsonObject();
        var normalizedIncoming = NormalizeCachedToolArgs(incoming);
        if (normalizedIncoming is not null)
        {
            foreach (var key in NativeToolProjector.DisplayArgumentKeys)
            {
                if (normalizedIncoming[key] is not JsonValue value ||
                    !value.TryGetValue<string>(out var incomingText))
                {
                    continue;
                }

                if (merged[key] is JsonValue existingValue &&
                    existingValue.TryGetValue<string>(out var existingText) &&
                    !string.Equals(
                        existingText,
                        incomingText,
                        StringComparison.Ordinal))
                {
                    var combined = existingText + "\n" + incomingText;
                    merged[key] = combined.Length > 512
                        ? combined[..509] + "..."
                        : combined;
                }
                else
                {
                    merged[key] = incomingText;
                }
            }
        }
        return merged.Count == 0 ? null : merged;
    }
}
