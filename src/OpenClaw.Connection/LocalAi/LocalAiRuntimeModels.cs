// <summary>
// Shared runtime state models for the local AI subsystem: runtime/ownership/model-availability
// enums, LocalAiModelEvidence (digest-backed model verification), LocalAiRuntimeSnapshot
// (published state including process and KV-cache details), its change-event args, and the
// ILocalAiRuntime contract that LlamaServerRuntimeService implements.
// </summary>
// Usage:
//   runtime.StateChanged += (_, args) =>
//   {
//       LocalAiRuntimeSnapshot s = args.Snapshot;
//       Log($"{s.State} ownership={s.Ownership} model={s.ModelId} at {s.Endpoint}");
//       if (s.ModelEvidence.Availability is LocalAiModelAvailabilityState.Loaded) { /* ready */ }
//   };
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.Connection.LocalAi;

public enum LocalAiRuntimeState
{
    NotInstalled,
    Stopped,
    Starting,
    Healthy,
    Stopping,
    Conflict,
    Failed,
}

public enum LocalAiOwnership
{
    None,
    CompanionManaged,
}

/// <summary>
/// Evidence-backed availability of the exact model recorded in the managed manifest.
/// Verified and Loaded states always carry the artifact digest and observed size.
/// </summary>
public enum LocalAiModelAvailabilityState
{
    Unknown,
    NotInstalled,
    Verified,
    Loaded,
}

public sealed record LocalAiModelEvidence
{
    public LocalAiModelEvidence(
        LocalAiModelAvailabilityState state,
        DateTimeOffset observedAtUtc,
        string? sha256 = null,
        long? sizeBytes = null,
        string? serverModelId = null)
    {
        if (state is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded)
        {
            if (sha256?.Length != 64 || sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("Verified model evidence requires a lowercase SHA-256 digest.", nameof(sha256));
            if (sizeBytes is null or <= 0)
                throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Verified model evidence requires a positive observed size.");
        }
        else if (sha256 is not null || sizeBytes is not null || serverModelId is not null)
        {
            throw new ArgumentException("Unknown or missing model evidence cannot carry artifact or server claims.");
        }

        if (state == LocalAiModelAvailabilityState.Loaded && string.IsNullOrWhiteSpace(serverModelId))
            throw new ArgumentException("Loaded model evidence requires the server-observed model identifier.", nameof(serverModelId));
        if (state != LocalAiModelAvailabilityState.Loaded && serverModelId is not null)
            throw new ArgumentException("Only loaded model evidence can carry a server-observed model identifier.", nameof(serverModelId));

        State = state;
        ObservedAtUtc = observedAtUtc;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        ServerModelId = serverModelId;
    }

    public LocalAiModelAvailabilityState State { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string? Sha256 { get; }
    public long? SizeBytes { get; }
    public string? ServerModelId { get; }

    public static LocalAiModelEvidence Unknown(DateTimeOffset now) =>
        new(LocalAiModelAvailabilityState.Unknown, now);

    public static LocalAiModelEvidence NotInstalled(DateTimeOffset now) =>
        new(LocalAiModelAvailabilityState.NotInstalled, now);
}

public sealed record LocalAiRuntimeSnapshot(
    LocalAiRuntimeState State,
    LocalAiOwnership Ownership,
    Uri Endpoint,
    string? EngineVersion,
    string? ModelId,
    LocalAiModelEvidence ModelEvidence,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string? Detail,
    DateTimeOffset UpdatedAtUtc,
    int? ContextLength = null,
    KvCachePrecision? KeyCachePrecision = null,
    KvCachePrecision? ValueCachePrecision = null,
    KvCachePrecision? DraftKeyCachePrecision = null,
    KvCachePrecision? DraftValueCachePrecision = null)
{
    public static LocalAiRuntimeSnapshot Initial(Uri endpoint, DateTimeOffset now) =>
        new(
            LocalAiRuntimeState.Stopped,
            LocalAiOwnership.None,
            endpoint,
            null,
            null,
            LocalAiModelEvidence.Unknown(now),
            null,
            null,
            null,
            now);
}

public sealed class LocalAiRuntimeSnapshotChangedEventArgs(LocalAiRuntimeSnapshot snapshot) : EventArgs
{
    public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
}

public interface ILocalAiRuntime : IAsyncDisposable
{
    LocalAiRuntimeSnapshot Snapshot { get; }
    event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;
    Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default);
    Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
