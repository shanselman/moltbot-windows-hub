using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Chat;

internal sealed class ChatStatePersistence : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _abortedFileGate = new(1, 1);
    private readonly string _lastStatePath;
    private readonly string _abortedIdsPath;
    private readonly TimeSpan _lastStateSaveDelay;
    private readonly Dictionary<string, HashSet<string>> _abortedIds;
    private readonly Dictionary<string, Dictionary<string, long>>
        _abortedIdGenerations;
    private readonly Dictionary<string, long> _resetGenerations = new(StringComparer.Ordinal);
    private Timer? _lastStateSaveTimer;
    private long _lastStateSaveVersion;
    private OpenClawChatDataProvider.LastChatState? _lastState;
    private bool _disposed;

    internal ChatStatePersistence(
        string? lastStatePath = null,
        TimeSpan? lastStateSaveDelay = null,
        string? abortedIdsPath = null)
    {
        _lastStatePath = !string.IsNullOrWhiteSpace(lastStatePath)
            ? lastStatePath
            : ChatMetadataStore.LastChatStateFilePath;
        _lastStateSaveDelay = lastStateSaveDelay ?? TimeSpan.FromSeconds(2);
        _abortedIdsPath = !string.IsNullOrWhiteSpace(abortedIdsPath)
            ? abortedIdsPath
            : ChatMetadataStore.AbortedIdsFilePath;
        InitialLastChatState = LoadLastChatState(_lastStatePath);
        _lastState = InitialLastChatState;
        _abortedIds = LoadAbortedIds(_abortedIdsPath);
        _abortedIdGenerations = _abortedIds.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToDictionary(
                id => id,
                _ => 0L,
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    internal OpenClawChatDataProvider.LastChatState? InitialLastChatState { get; }

    internal bool IsMessageAborted(string threadId, string? openClawId)
        => IsMessageAborted(threadId, openClawId, resetGeneration: 0);

    internal bool IsMessageAborted(
        string threadId,
        string? openClawId,
        long resetGeneration)
    {
        if (openClawId is null)
            return false;
        lock (_gate)
        {
            return _abortedIds.TryGetValue(threadId, out var ids) &&
                   ids.Contains(openClawId) &&
                   _abortedIdGenerations.TryGetValue(threadId, out var generations) &&
                   generations.TryGetValue(openClawId, out var generation) &&
                   generation >= resetGeneration;
        }
    }

    internal bool ApplyReset(string threadId, long resetGeneration)
    {
        lock (_gate)
        {
            if (_resetGenerations.TryGetValue(threadId, out var current) &&
                current >= resetGeneration)
            {
                return false;
            }
            _resetGenerations[threadId] = resetGeneration;
            if (!_abortedIds.TryGetValue(threadId, out var ids) ||
                !_abortedIdGenerations.TryGetValue(
                    threadId,
                    out var generations))
            {
                return false;
            }
            var removed = ids.RemoveWhere(id =>
                !generations.TryGetValue(id, out var generation) ||
                generation < resetGeneration) > 0;
            foreach (var id in generations
                         .Where(pair => pair.Value < resetGeneration)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                generations.Remove(id);
            }
            if (ids.Count == 0)
            {
                _abortedIds.Remove(threadId);
                _abortedIdGenerations.Remove(threadId);
            }
            return removed;
        }
    }

    internal void SaveAbortedIds()
    {
        _abortedFileGate.Wait();
        try
        {
            Dictionary<string, List<string>> snapshot;
            lock (_gate)
            {
                snapshot = _abortedIds.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList(),
                    StringComparer.Ordinal);
            }

            var directory = Path.GetDirectoryName(_abortedIdsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var path = _abortedIdsPath;
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    tempPath,
                    JsonSerializer.Serialize(
                        snapshot,
                        new JsonSerializerOptions { WriteIndented = true }));
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
                    Logger.Debug(
                        $"Aborted ID temp file cleanup failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Chat aborted ID persistence failed: {ex.Message}");
        }
        finally
        {
            _abortedFileGate.Release();
        }
    }

    internal IReadOnlyList<string> FindAbortedMessageIds(
        string threadId,
        IReadOnlyList<ChatMessageInfo> messages,
        long resetGeneration = 0) =>
        FindAbortedMessageIds(messages, threadId, resetGeneration);

    internal bool TryAddAbortedIds(
        string threadId,
        long resetGeneration,
        IReadOnlyList<string> newIds)
    {
        if (newIds.Count == 0)
            return false;
        lock (_gate)
        {
            if (_resetGenerations.TryGetValue(threadId, out var fence) &&
                resetGeneration < fence)
            {
                return false;
            }
            if (!_abortedIds.TryGetValue(threadId, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                _abortedIds[threadId] = ids;
            }
            if (!_abortedIdGenerations.TryGetValue(
                    threadId,
                    out var generations))
            {
                generations = new Dictionary<string, long>(
                    StringComparer.Ordinal);
                _abortedIdGenerations[threadId] = generations;
            }
            var changed = false;
            foreach (var id in newIds)
            {
                changed |= ids.Add(id);
                if (!generations.TryGetValue(id, out var generation) ||
                    generation < resetGeneration)
                {
                    generations[id] = resetGeneration;
                }
            }
            return changed;
        }
    }

    internal void SaveSelectedState(OpenClawChatDataProvider.LastChatState state)
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _lastStateSaveTimer;
            _lastStateSaveTimer = null;
            _lastStateSaveVersion++;
            _lastState = state;
        }
        timer?.Dispose();
        SaveLastChatState(state, _lastStatePath);
    }

    internal void DebounceSnapshot(ChatDataSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var state = CreateLastChatState(snapshot, _lastState);
            if (state is null)
                return;
            _lastState = state;
            var version = ++_lastStateSaveVersion;
            _lastStateSaveTimer?.Dispose();
            _lastStateSaveTimer = new Timer(
                _ => SaveLastStateIfCurrent(state, version),
                null,
                _lastStateSaveDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    internal void SaveSnapshot(ChatDataSnapshot snapshot)
    {
        Timer? timer;
        OpenClawChatDataProvider.LastChatState? state;
        lock (_gate)
        {
            if (_disposed)
                return;
            state = CreateLastChatState(snapshot, _lastState);
            if (state is null)
                return;
            _lastState = state;
            _lastStateSaveVersion++;
            timer = _lastStateSaveTimer;
            _lastStateSaveTimer = null;
            // SaveLastStateIfCurrent also writes under this gate. Keeping the
            // final write serialized ensures an in-flight debounce callback
            // cannot overwrite the authoritative shutdown snapshot afterward.
            SaveLastChatState(state, _lastStatePath);
        }
        timer?.Dispose();
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            timer = _lastStateSaveTimer;
            _lastStateSaveTimer = null;
            _lastStateSaveVersion++;
        }
        timer?.Dispose();
    }

    internal static OpenClawChatDataProvider.LastChatState? LoadLastChatState(
        string? pathOverride = null)
    {
        var path = pathOverride ?? ChatMetadataStore.LastChatStateFilePath;
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<OpenClawChatDataProvider.LastChatState>(
                File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load last chat state from '{path}': {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, HashSet<string>> LoadAbortedIds(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var persisted = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(path));
            return persisted?.ToDictionary(
                pair => pair.Key,
                pair => new HashSet<string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Debug($"Aborted message IDs could not be loaded: {ex.Message}");
            return [];
        }
    }

    private List<string> FindAbortedMessageIds(
        IReadOnlyList<ChatMessageInfo> messages,
        string threadId,
        long resetGeneration)
    {
        var result = new List<string>();
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                message.OpenClawId is null ||
                IsMessageAborted(
                    threadId,
                    message.OpenClawId,
                    resetGeneration) ||
                result.Contains(message.OpenClawId, StringComparer.Ordinal))
            {
                continue;
            }

            ChatMessageInfo? nextAssistant = null;
            for (var j = i + 1; j < messages.Count; j++)
            {
                var candidate = messages[j];
                if (string.Equals(
                        candidate.Role,
                        "assistant",
                        StringComparison.OrdinalIgnoreCase))
                {
                    nextAssistant = candidate;
                    break;
                }
                if (string.Equals(
                        candidate.Role,
                        "user",
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (nextAssistant is null ||
                !string.IsNullOrEmpty(nextAssistant.StopReason) &&
                !string.Equals(nextAssistant.StopReason, "stop", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(nextAssistant.StopReason, "end_turn", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(nextAssistant.StopReason, "toolUse", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(message.OpenClawId);
            }
        }
        return result;
    }

    private void SaveLastStateIfCurrent(
        OpenClawChatDataProvider.LastChatState state,
        long version)
    {
        lock (_gate)
        {
            if (_disposed || version != _lastStateSaveVersion)
                return;
            SaveLastChatState(state, _lastStatePath);
            _lastStateSaveTimer?.Dispose();
            _lastStateSaveTimer = null;
        }
    }

    private static OpenClawChatDataProvider.LastChatState? CreateLastChatState(
        ChatDataSnapshot snapshot,
        OpenClawChatDataProvider.LastChatState? previous)
    {
        var defaultThread = snapshot.DefaultThreadId is { } defaultId
            ? Array.Find(snapshot.Threads, thread => thread.Id == defaultId)
            : snapshot.Threads.FirstOrDefault();
        if (defaultThread is null && snapshot.AvailableModels.Length == 0)
            return null;

        return new OpenClawChatDataProvider.LastChatState
        {
            DefaultThreadId = snapshot.DefaultThreadId ?? previous?.DefaultThreadId,
            ThreadTitle = defaultThread?.Title ?? previous?.ThreadTitle,
            Model = defaultThread?.Model ?? previous?.Model,
            ModelProvider = defaultThread?.ModelProvider ?? previous?.ModelProvider,
            AvailableModels = snapshot.AvailableModels.ToArray(),
        };
    }

    private static void SaveLastChatState(
        OpenClawChatDataProvider.LastChatState state,
        string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Chat state persistence failed: {ex.Message}");
        }
    }
}
