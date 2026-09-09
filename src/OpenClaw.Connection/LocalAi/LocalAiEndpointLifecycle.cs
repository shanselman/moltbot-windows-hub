namespace OpenClaw.Connection.LocalAi;

public sealed record LocalAiEndpointLifecycleResult(bool Success, string? Detail = null)
{
    public static LocalAiEndpointLifecycleResult Ok() => new(true);
    public static LocalAiEndpointLifecycleResult Failed(string detail) => new(false, detail);
}

/// <summary>
/// Why managed routing is being withdrawn. The distinction matters because an
/// endpoint cycle is followed by a republish, while a teardown is not.
/// </summary>
public enum LocalAiQuiesceReason
{
    /// <summary>
    /// The managed endpoint is about to move or restart and will be republished.
    /// The managed primary model is retained so the gateway cannot resolve its
    /// built-in default provider while the endpoint is briefly absent.
    /// </summary>
    EndpointCycle,

    /// <summary>
    /// Local AI is being stopped for good (stop, shutdown, failed publish).
    /// The gateway primary model is restored to whatever preceded Local AI.
    /// </summary>
    Teardown,
}

/// <summary>
/// Coordinates consumers of the app-owned endpoint with native process changes.
/// Implementations must remove managed routing before a listener can disappear,
/// and publish routing only after the replacement endpoint is proven healthy.
/// </summary>
public interface ILocalAiEndpointLifecycle
{
    Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
        CancellationToken cancellationToken = default);

    Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default);
}

internal sealed class NullLocalAiEndpointLifecycle : ILocalAiEndpointLifecycle
{
    public static NullLocalAiEndpointLifecycle Instance { get; } = new();

    public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
        LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    }

    public Task<LocalAiEndpointLifecycleResult> PublishAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    }
}
