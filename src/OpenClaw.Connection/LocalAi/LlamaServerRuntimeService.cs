using OpenClaw.Shared;
using System.Net;
using System.Text;

namespace OpenClaw.Connection.LocalAi;

public sealed record LlamaServerRuntimeOptions
{
    public required LocalAiPaths Paths { get; init; }
    public Uri InitialEndpoint { get; init; } = new("http://127.0.0.1:18803/v1");
    public ILocalAiEndpointLifecycle EndpointLifecycle { get; init; } = NullLocalAiEndpointLifecycle.Instance;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxRestartAttempts { get; init; } = 2;
    public long MaxLogBytes { get; init; } = 8 * 1024 * 1024;
    public int LogBackupCount { get; init; } = 2;
    public int MaxLogLineCharacters { get; init; } = 16 * 1024;
}

internal interface ILlamaServerRuntimePlatform
{
    DateTimeOffset UtcNow { get; }
    WindowsTcpListenerSnapshotResult CaptureListeners();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemLlamaServerRuntimePlatform : ILlamaServerRuntimePlatform
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public WindowsTcpListenerSnapshotResult CaptureListeners() => WindowsTcpListenerSnapshot.Capture();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// Owns the native llama-server router for the lifetime of the Windows companion.
/// The router starts without a model; the first inference request triggers the
/// model load defined by the verified preset.
/// </summary>
public sealed class LlamaServerRuntimeService : ILocalAiRuntime
{
    private readonly LlamaServerRuntimeOptions _options;
    private readonly LocalAiManifestStore _manifestStore;
    private readonly IOpenClawLogger _logger;
    private readonly ILocalAiManagedProcessHost _processHost;
    private readonly ILlamaServerRuntimePlatform _platform;
    private readonly ILlamaServerClient _client;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _exitTasksGate = new();
    private readonly HashSet<Task> _exitTasks = [];
    private readonly object _snapshotGate = new();
    private LocalAiRuntimeSnapshot _snapshot;
    private ILocalAiManagedProcess? _managedProcess;
    private LocalAiResolvedInstall? _install;
    private long _generation;
    private int _restartAttempts;
    private bool _stopping;
    private bool _disposed;
    private bool _acceptExitTasks = true;
    private int _disposeStarted;

    public LlamaServerRuntimeService(LlamaServerRuntimeOptions options, IOpenClawLogger? logger = null)
        : this(
            options,
            logger ?? NullLogger.Instance,
            new WindowsLocalAiManagedProcessHost(logger ?? NullLogger.Instance),
            new SystemLlamaServerRuntimePlatform(),
            new LlamaServerClient())
    {
    }

    internal LlamaServerRuntimeService(
        LlamaServerRuntimeOptions options,
        IOpenClawLogger logger,
        ILocalAiManagedProcessHost processHost,
        ILlamaServerRuntimePlatform platform,
        ILlamaServerClient client)
    {
        _options = ValidateOptions(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _manifestStore = new LocalAiManifestStore(options.Paths);
        _snapshot = LocalAiRuntimeSnapshot.Initial(options.InitialEndpoint, platform.UtcNow);
    }

    public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

    public LocalAiRuntimeSnapshot Snapshot
    {
        get { lock (_snapshotGate) return _snapshot; }
    }

    public async Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            LocalAiRuntimeSnapshot stopped = await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_managedProcess is not null || stopped.State == LocalAiRuntimeState.Failed)
                return stopped;
            _restartAttempts = 0;
            return await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<LocalAiRuntimeSnapshot> EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        LocalAiResolvedInstall install = _install!;
        if (_managedProcess is { HasExited: false })
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        if (_managedProcess is not null)
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);

        // Decide the port before touching gateway routing. Rebinding the last
        // verified port keeps the already-published route valid for the whole
        // startup, so the gateway is never left without a Local AI provider.
        WindowsTcpListenerSnapshotResult beforeStart = _platform.CaptureListeners();
        if (!beforeStart.Ipv4Complete)
        {
            return await FailStartupAsync(
                    LocalAiRuntimeState.Conflict,
                    "TCP listener ownership could not be determined.",
                    install)
                .ConfigureAwait(false);
        }

        // A foreign process owns the port the published route points at, so that
        // route must be withdrawn before returning rather than left aimed at it.
        int plannedPort = ResolvePlannedPort(install, beforeStart);
        if (plannedPort != LocalAiPortPolicy.Automatic &&
            FindEndpointListeners(beforeStart, plannedPort).Count > 0)
        {
            return await FailStartupAsync(
                    LocalAiRuntimeState.Conflict,
                    "The configured llama-server port is already in use.",
                    install)
                .ConfigureAwait(false);
        }

        // The published route already names the port we are about to bind, so
        // withdrawing it would only open a window in which the gateway falls
        // back to its built-in default provider. PublishAsync re-verifies below.
        Uri? retainedRoute = plannedPort != LocalAiPortPolicy.Automatic &&
            install.Endpoint is { } publishedRoute &&
            publishedRoute.Port == plannedPort
                ? publishedRoute
                : null;
        if (retainedRoute is null)
        {
            LocalAiEndpointLifecycleResult quiesced = await _options.EndpointLifecycle
                .QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle, cancellationToken)
                .ConfigureAwait(false);
            if (!quiesced.Success)
            {
                return Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.");
            }
        }

        LlamaServerRouterLaunchPlan launchPlan;
        try
        {
            ValidateInstalledFiles(install);
            LocalAiPortPolicy.Validate(plannedPort);
            launchPlan = LlamaServerRouterConfiguration.Build(
                _options.Paths,
                install,
                plannedPort);
            await WritePresetAtomicallyAsync(launchPlan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not prepare the managed llama-server router.", ex);
            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    Sanitize(ex.Message),
                    retainedRoute is null ? null : install)
                .ConfigureAwait(false);
        }

        long generation = ++_generation;
        Publish(LocalAiRuntimeState.Starting, LocalAiOwnership.CompanionManaged, "Starting the local AI router.");
        var spec = new LocalAiProcessStartSpec(
            install.ExecutablePath,
            Path.GetDirectoryName(install.ExecutablePath)!,
            launchPlan.Arguments,
            launchPlan.Environment,
            _options.Paths.StandardOutputLogPath,
            _options.Paths.StandardErrorLogPath,
            _options.MaxLogBytes,
            _options.LogBackupCount,
            _options.MaxLogLineCharacters);

        try
        {
            _managedProcess = await _processHost.StartProcessAsync(
                    spec,
                    exit => OnManagedProcessExited(generation, exit),
                    cancellationToken)
                .ConfigureAwait(false);

            DateTimeOffset deadline = _platform.UtcNow + _options.StartupTimeout;
            while (_platform.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_managedProcess.HasExited)
                    throw new InvalidOperationException("Managed llama-server exited during startup.");

                EndpointOwnershipObservation ownership = DiscoverOwnedEndpoint(install, _managedProcess);
                if (!ownership.IsComplete)
                {
                    return await FailStartupAsync(
                            LocalAiRuntimeState.Conflict,
                            "TCP listener ownership could not be determined.",
                            retainedRoute is null ? null : install)
                        .ConfigureAwait(false);
                }
                if (ownership.ConflictDetail is not null)
                {
                    return await FailStartupAsync(
                            LocalAiRuntimeState.Conflict,
                            ownership.ConflictDetail,
                            retainedRoute is null ? null : install)
                        .ConfigureAwait(false);
                }
                if (ownership.Endpoint is not null)
                {
                    LlamaServerRouterProbeResult probe = await _client.ProbeManagedModelAsync(
                            ownership.Endpoint,
                            install.Manifest.ModelAlias,
                            install.ModelPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (probe.IsReadyForManagedModel(install.ModelPath))
                    {
                        // The router bound a different port than the retained route
                        // advertises, so that route is now stale and must be withdrawn
                        // before the new one is published.
                        if (retainedRoute is not null && retainedRoute != ownership.Endpoint)
                        {
                            LocalAiEndpointLifecycleResult withdrawn = await _options.EndpointLifecycle
                                .QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle, cancellationToken)
                                .ConfigureAwait(false);
                            if (!withdrawn.Success)
                            {
                                return await FailStartupAsync(
                                        LocalAiRuntimeState.Failed,
                                        withdrawn.Detail ?? "The stale Local AI route could not be withdrawn.",
                                        install)
                                    .ConfigureAwait(false);
                            }
                        }

                        LocalAiInstallManifest verifiedManifest = install.Manifest with
                        {
                            Endpoint = ownership.Endpoint.AbsoluteUri,
                        };
                        await _manifestStore.SaveAsync(verifiedManifest, cancellationToken).ConfigureAwait(false);
                        _install = _manifestStore.ResolveAndValidate(verifiedManifest);

                        LocalAiEndpointLifecycleResult published = await _options.EndpointLifecycle
                            .PublishAsync(_install, cancellationToken)
                            .ConfigureAwait(false);
                        if (!published.Success)
                        {
                            return await FailStartupAsync(
                                    LocalAiRuntimeState.Failed,
                                    published.Detail ?? "The Local AI gateway provider could not be safely published.",
                                    _install)
                                .ConfigureAwait(false);
                        }

                        return PublishHealthy(probe);
                    }
                }

                await _platform.DelayAsync(_options.HealthPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return await FailStartupAsync(
                    LocalAiRuntimeState.Failed,
                    "The local AI router did not become healthy before the startup timeout.",
                    retainedRoute is null ? null : install)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(LocalAiRuntimeState.Stopped, LocalAiOwnership.None, "Local AI startup was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            ++_generation;
            await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.Error("Managed llama-server startup failed.", ex);
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }
    }

    private async Task<LocalAiRuntimeSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        try
        {
            ValidateInstalledFiles(_install!);
        }
        catch (InvalidDataException ex)
        {
            return Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
        }

        if (_managedProcess is null || _managedProcess.HasExited)
        {
            if (_install!.Endpoint is { } persistedEndpoint)
            {
                WindowsTcpListenerSnapshotResult snapshot = _platform.CaptureListeners();
                if (!snapshot.Ipv4Complete)
                    return Publish(LocalAiRuntimeState.Conflict, LocalAiOwnership.None, "TCP listener ownership could not be determined.");
                if (FindEndpointListeners(snapshot, persistedEndpoint.Port).Count > 0)
                {
                    return Publish(
                        LocalAiRuntimeState.Conflict,
                        LocalAiOwnership.None,
                        "A process not owned by this companion is using the last verified Local AI endpoint.");
                }
            }

            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }

        LocalAiResolvedInstall install = _install!;
        EndpointOwnershipObservation ownership = DiscoverOwnedEndpoint(install, _managedProcess);
        if (!ownership.IsComplete)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Conflict,
                LocalAiOwnership.None,
                "TCP listener ownership could not be determined.");
        }
        if (ownership.ConflictDetail is not null)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Conflict,
                LocalAiOwnership.None,
                ownership.ConflictDetail);
        }
        if (ownership.Endpoint is null)
        {
            LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                    install,
                    cancellationToken)
                .ConfigureAwait(false);
            return failure ?? Publish(
                LocalAiRuntimeState.Starting,
                LocalAiOwnership.CompanionManaged,
                "The local AI router has not opened its endpoint yet.",
                _managedProcess.ProcessId,
                _managedProcess.StartedAtUtc);
        }

        LlamaServerRouterProbeResult probe = await _client.ProbeManagedModelAsync(
                ownership.Endpoint,
                install.Manifest.ModelAlias,
                install.ModelPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.IsReadyForManagedModel(install.ModelPath))
        {
            bool endpointChanged = install.Endpoint != ownership.Endpoint;
            if (_snapshot.State != LocalAiRuntimeState.Healthy || endpointChanged)
            {
                if (endpointChanged && _snapshot.State == LocalAiRuntimeState.Healthy)
                {
                    LocalAiRuntimeSnapshot? failure = await QuiesceOrStopAsync(
                            install,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (failure is not null)
                        return failure;
                }

                install = await BindVerifiedEndpointAsync(
                        install,
                        ownership.Endpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                LocalAiEndpointLifecycleResult published = await _options.EndpointLifecycle
                    .PublishAsync(install, cancellationToken)
                    .ConfigureAwait(false);
                if (!published.Success)
                {
                    return Publish(
                        LocalAiRuntimeState.Failed,
                        LocalAiOwnership.CompanionManaged,
                        published.Detail ?? "The verified Local AI endpoint could not be republished.",
                        _managedProcess.ProcessId,
                        _managedProcess.StartedAtUtc);
                }
            }

            return PublishHealthy(probe);
        }

        LocalAiRuntimeSnapshot? quiesceFailure = await QuiesceOrStopAsync(
                install,
                cancellationToken)
            .ConfigureAwait(false);
        if (quiesceFailure is not null)
            return quiesceFailure;

        return Publish(
            LocalAiRuntimeState.Starting,
            LocalAiOwnership.CompanionManaged,
            probe.Detail ?? "The local AI router has not verified the managed model yet.",
            _managedProcess.ProcessId,
            _managedProcess.StartedAtUtc);
    }

    private async Task<LocalAiRuntimeSnapshot?> QuiesceOrStopAsync(
        LocalAiResolvedInstall install,
        CancellationToken cancellationToken)
    {
        LocalAiEndpointLifecycleResult? quiesced = null;
        try
        {
            quiesced = await _options.EndpointLifecycle
                .QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (quiesced is null)
            {
                ++_generation;
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    "The Local AI gateway provider withdrawal did not complete.");
            }
        }
        if (quiesced.Success)
            return null;

        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        return Publish(
            LocalAiRuntimeState.Failed,
            LocalAiOwnership.None,
            quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.");
    }

    private async Task<LocalAiResolvedInstall> BindVerifiedEndpointAsync(
        LocalAiResolvedInstall install,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        if (install.Endpoint == endpoint)
            return install;

        LocalAiInstallManifest verifiedManifest = install.Manifest with
        {
            Endpoint = endpoint.AbsoluteUri,
        };
        await _manifestStore.SaveAsync(verifiedManifest, cancellationToken).ConfigureAwait(false);
        _install = _manifestStore.ResolveAndValidate(verifiedManifest);
        return _install;
    }

    private async Task<bool> TryLoadInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            _install = await _manifestStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.Error("Could not load the local AI installation manifest.", ex);
            Publish(LocalAiRuntimeState.Failed, LocalAiOwnership.None, Sanitize(ex.Message));
            return false;
        }

        if (_install is not null)
            return true;
        Publish(LocalAiRuntimeState.NotInstalled, LocalAiOwnership.None, "Local AI is not installed.");
        return false;
    }

    private static void ValidateInstalledFiles(LocalAiResolvedInstall install)
    {
        if (!File.Exists(install.ExecutablePath))
            throw new InvalidDataException("The managed llama-server executable is missing.");
        var model = new FileInfo(install.ModelPath);
        if (!model.Exists || model.Length != install.Manifest.ModelAsset.SizeBytes)
            throw new InvalidDataException("The managed GGUF model is missing or has an unexpected size.");
    }

    private async Task WritePresetAtomicallyAsync(
        LlamaServerRouterLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        _options.Paths.EnsureDirectories();
        string temporaryPath = Path.Combine(
            _options.Paths.RootDirectory,
            $".{Path.GetFileName(plan.PresetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _ = _options.Paths.ResolveContainedPath(Path.GetFileName(temporaryPath), nameof(temporaryPath));
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(plan.PresetContent);
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            _ = _options.Paths.ResolveContainedPath(
                Path.GetRelativePath(_options.Paths.RootDirectory, plan.PresetPath),
                nameof(plan.PresetPath));
            File.Move(temporaryPath, plan.PresetPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { }
        }
    }

    private async Task<LocalAiRuntimeSnapshot> StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_install is null && !await TryLoadInstallAsync(cancellationToken).ConfigureAwait(false))
            return Snapshot;

        LocalAiEndpointLifecycleResult quiesced = await _options.EndpointLifecycle
            .QuiesceAsync(_install!, LocalAiQuiesceReason.Teardown, cancellationToken)
            .ConfigureAwait(false);
        if (!quiesced.Success)
        {
            return Publish(
                LocalAiRuntimeState.Failed,
                _managedProcess is null ? LocalAiOwnership.None : LocalAiOwnership.CompanionManaged,
                quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled.",
                _managedProcess?.ProcessId,
                _managedProcess?.StartedAtUtc);
        }

        if (_managedProcess is null)
        {
            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }

        _stopping = true;
        ++_generation;
        Publish(LocalAiRuntimeState.Stopping, LocalAiOwnership.CompanionManaged, "Stopping the local AI router.", _managedProcess.ProcessId, _managedProcess.StartedAtUtc);
        try
        {
            await DisposeManagedProcessAsync(cancellationToken).ConfigureAwait(false);
            return Publish(
                LocalAiRuntimeState.Stopped,
                LocalAiOwnership.None,
                null,
                modelState: LocalAiModelAvailabilityState.Verified);
        }
        finally
        {
            _stopping = false;
        }
    }

    /// <summary>
    /// Fails a startup attempt. When <paramref name="routeToWithdraw"/> is set, the
    /// gateway is still pointing at a route this attempt left standing (or just
    /// published) and no listener will answer it, so it must be withdrawn here.
    /// </summary>
    private async Task<LocalAiRuntimeSnapshot> FailStartupAsync(
        LocalAiRuntimeState state,
        string detail,
        LocalAiResolvedInstall? routeToWithdraw = null)
    {
        ++_generation;
        await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
        if (routeToWithdraw is not null)
        {
            try
            {
                LocalAiEndpointLifecycleResult withdrawn = await _options.EndpointLifecycle
                    .QuiesceAsync(routeToWithdraw, LocalAiQuiesceReason.Teardown, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!withdrawn.Success)
                    _logger.Warn(withdrawn.Detail ?? "The Local AI route could not be withdrawn after a failed start.");
            }
            catch (Exception ex)
            {
                _logger.Warn($"The Local AI route could not be withdrawn after a failed start: {Sanitize(ex.Message)}");
            }
        }
        return Publish(state, LocalAiOwnership.None, detail);
    }

    /// <summary>
    /// Picks the port the router should bind. An explicit port always wins. Otherwise
    /// the last verified port is reused when it is free, so the published gateway
    /// route survives a restart instead of having to be withdrawn and rewritten.
    /// </summary>
    private static int ResolvePlannedPort(
        LocalAiResolvedInstall install,
        WindowsTcpListenerSnapshotResult listeners)
    {
        int requestedPort = install.Manifest.RequestedPort;
        if (requestedPort != LocalAiPortPolicy.Automatic)
            return requestedPort;

        if (install.Endpoint is not { } lastVerified ||
            !LocalAiPortPolicy.TryValidate(lastVerified.Port, out _) ||
            lastVerified.Port == LocalAiPortPolicy.Automatic)
        {
            return LocalAiPortPolicy.Automatic;
        }

        return FindEndpointListeners(listeners, lastVerified.Port).Count == 0
            ? lastVerified.Port
            : LocalAiPortPolicy.Automatic;
    }

    private async Task DisposeManagedProcessAsync(CancellationToken cancellationToken)
    {
        ILocalAiManagedProcess? process = _managedProcess;
        _managedProcess = null;
        if (process is null)
            return;
        try
        {
            await process.StopAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnManagedProcessExited(long generation, LocalAiManagedProcessExit exit)
    {
        Task exitTask;
        lock (_exitTasksGate)
        {
            if (!_acceptExitTasks)
                return;
            exitTask = Task.Run(() => HandleManagedProcessExitedAsync(generation, exit));
            _exitTasks.Add(exitTask);
        }
        _ = RemoveCompletedExitTaskAsync(exitTask);
    }

    private async Task HandleManagedProcessExitedAsync(long generation, LocalAiManagedProcessExit exit)
    {
        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || _stopping || generation != _generation)
                    return;
                ILocalAiManagedProcess? exited = _managedProcess;
                _managedProcess = null;
                if (exited is not null)
                    await exited.DisposeAsync().ConfigureAwait(false);

                if (_install is not null)
                {
                    LocalAiEndpointLifecycleResult quiesced = await _options.EndpointLifecycle
                        .QuiesceAsync(_install, LocalAiQuiesceReason.EndpointCycle, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!quiesced.Success)
                    {
                        Publish(
                            LocalAiRuntimeState.Failed,
                            LocalAiOwnership.None,
                            quiesced.Detail ?? "The Local AI gateway provider could not be safely disabled after the router exited.");
                        return;
                    }
                }
                Publish(
                    LocalAiRuntimeState.Failed,
                    LocalAiOwnership.None,
                    $"Managed llama-server exited unexpectedly{(exit.ExitCode.HasValue ? $" with code {exit.ExitCode.Value}" : string.Empty)}.");
                if (_restartAttempts >= _options.MaxRestartAttempts)
                    return;
                _restartAttempts++;
            }
            finally
            {
                _operationGate.Release();
            }

            await _platform.DelayAsync(_options.RestartDelay, CancellationToken.None).ConfigureAwait(false);
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_disposed && !_stopping && generation == _generation)
                    await EnsureStartedCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Managed llama-server automatic restart failed.", ex);
        }
    }

    private async Task RemoveCompletedExitTaskAsync(Task exitTask)
    {
        await exitTask.ConfigureAwait(false);
        lock (_exitTasksGate)
            _exitTasks.Remove(exitTask);
    }

    private EndpointOwnershipObservation DiscoverOwnedEndpoint(
        LocalAiResolvedInstall install,
        ILocalAiManagedProcess process)
    {
        WindowsTcpListenerSnapshotResult snapshot = _platform.CaptureListeners();
        if (!snapshot.Ipv4Complete)
            return new(false, null, null);

        WindowsTcpListenerInfo[] loopbackListeners = snapshot.Listeners
            .Where(IsIpv4LoopbackListener)
            .ToArray();
        if (snapshot.Listeners.Any(listener =>
                listener.ProcessId == process.ProcessId &&
                listener.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IsIpv4LoopbackListener(listener)))
        {
            return new(
                true,
                null,
                "Managed llama-server opened an IPv4 listener outside the loopback interface.");
        }
        WindowsTcpListenerInfo[] processListeners = loopbackListeners
            .Where(listener => listener.ProcessId == process.ProcessId)
            .ToArray();
        WindowsTcpListenerInfo[] ownedListeners = processListeners
            .Where(listener => IsManagedListener(listener, process))
            .ToArray();
        if (processListeners.Length != ownedListeners.Length)
        {
            return new(
                true,
                null,
                "A llama-server listener was found, but its process start time could not be verified.");
        }

        int requestedPort = install.Manifest.RequestedPort;
        if (requestedPort != LocalAiPortPolicy.Automatic)
        {
            IReadOnlyList<WindowsTcpListenerInfo> requestedListeners = FindEndpointListeners(snapshot, requestedPort);
            if (requestedListeners.Any(listener => !IsManagedListener(listener, process)))
                return new(true, null, "Another process owns the configured llama-server endpoint.");
            if (ownedListeners.Any(listener => listener.Port != requestedPort))
                return new(true, null, "Managed llama-server did not bind the requested fixed port.");
            if (requestedListeners.Count == 0)
                return new(true, null, null);
            return new(true, BuildEndpoint(requestedPort), null);
        }

        int[] ownedPorts = ownedListeners.Select(listener => listener.Port).Distinct().ToArray();
        if (ownedPorts.Length == 0)
            return new(true, null, null);
        if (ownedPorts.Length != 1)
            return new(true, null, "Managed llama-server opened more than one candidate loopback endpoint.");

        int selectedPort = ownedPorts[0];
        if (FindEndpointListeners(snapshot, selectedPort).Any(listener => !IsManagedListener(listener, process)))
            return new(true, null, "Another process shares the managed llama-server endpoint.");
        return new(true, BuildEndpoint(selectedPort), null);
    }

    private static IReadOnlyList<WindowsTcpListenerInfo> FindEndpointListeners(
        WindowsTcpListenerSnapshotResult snapshot,
        int port) => snapshot.Listeners
            .Where(listener => listener.Port == port && IsIpv4EndpointListener(listener))
            .ToArray();

    private static bool IsIpv4EndpointListener(WindowsTcpListenerInfo listener) =>
        IsIpv4LoopbackListener(listener) || listener.Address.Equals(IPAddress.Any);

    private static bool IsIpv4LoopbackListener(WindowsTcpListenerInfo listener) =>
        listener.Address.Equals(IPAddress.Loopback);

    private static Uri BuildEndpoint(int port) =>
        new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, "/v1").Uri;

    private LocalAiRuntimeSnapshot PublishHealthy(LlamaServerRouterProbeResult probe) =>
        Publish(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            probe.Detail,
            _managedProcess?.ProcessId,
            _managedProcess?.StartedAtUtc,
            probe.ModelState);

    private static bool IsManagedListener(
        WindowsTcpListenerInfo listener,
        ILocalAiManagedProcess process) =>
        listener.ProcessId == process.ProcessId &&
            listener.ProcessStartTimeUtc is { } started &&
            Math.Abs((started - process.StartedAtUtc.UtcDateTime).TotalSeconds) < 1;

    private LocalAiRuntimeSnapshot Publish(
        LocalAiRuntimeState state,
        LocalAiOwnership ownership,
        string? detail,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null,
        LocalAiModelAvailabilityState modelState = LocalAiModelAvailabilityState.Unknown)
    {
        DateTimeOffset now = _platform.UtcNow;
        if (state == LocalAiRuntimeState.NotInstalled)
            modelState = LocalAiModelAvailabilityState.NotInstalled;
        LocalAiModelEvidence evidence = BuildModelEvidence(modelState, now);
        var value = new LocalAiRuntimeSnapshot(
            state,
            ownership,
            _install?.Endpoint ?? _options.InitialEndpoint,
            _install?.Manifest.EngineVersion,
            _install?.Manifest.ModelCatalogId,
            evidence,
            processId,
            processStartedAtUtc,
            detail,
            now,
            _install?.Manifest.ContextLength,
            _install?.Manifest.KeyCachePrecision,
            _install?.Manifest.ValueCachePrecision,
            _install?.Manifest.DraftKeyCachePrecision,
            _install?.Manifest.DraftValueCachePrecision);
        lock (_snapshotGate)
            _snapshot = value;

        EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? handler = StateChanged;
        if (handler is not null)
        {
            foreach (EventHandler<LocalAiRuntimeSnapshotChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try { subscriber(this, new(value)); }
                catch (Exception ex) { _logger.Warn($"A local AI state observer failed: {Sanitize(ex.Message)}"); }
            }
        }
        return value;
    }

    private LocalAiModelEvidence BuildModelEvidence(
        LocalAiModelAvailabilityState state,
        DateTimeOffset now) => state switch
        {
            LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
            LocalAiModelAvailabilityState.Verified when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes),
            LocalAiModelAvailabilityState.Loaded when _install is not null => new(
                state,
                now,
                _install.Manifest.ModelAsset.Sha256,
                _install.Manifest.ModelAsset.SizeBytes,
                _install.Manifest.ModelAlias),
            _ => LocalAiModelEvidence.Unknown(now),
        };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        Task[] exitTasks;
        lock (_exitTasksGate)
        {
            _acceptExitTasks = false;
            exitTasks = [.. _exitTasks];
        }

        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return;
                _stopping = true;
                ++_generation;
                if (_install is not null)
                {
                    try
                    {
                        LocalAiEndpointLifecycleResult quiesced = await _options.EndpointLifecycle
                            .QuiesceAsync(_install, LocalAiQuiesceReason.Teardown, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!quiesced.Success)
                            _logger.Warn(quiesced.Detail ?? "The Local AI gateway provider could not be disabled during shutdown.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"The Local AI gateway provider could not be disabled during shutdown: {Sanitize(ex.Message)}");
                    }
                }
                await DisposeManagedProcessAsync(CancellationToken.None).ConfigureAwait(false);
                _disposed = true;
                _client.Dispose();
            }
            finally
            {
                _stopping = false;
                _operationGate.Release();
            }
        }
        finally
        {
            await Task.WhenAll(exitTasks).ConfigureAwait(false);
            _operationGate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static LlamaServerRuntimeOptions ValidateOptions(LlamaServerRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Paths);
        ArgumentNullException.ThrowIfNull(options.EndpointLifecycle);
        if (!options.InitialEndpoint.IsAbsoluteUri ||
            options.InitialEndpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(options.InitialEndpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            options.InitialEndpoint.Port is <= 0 or > 65535 ||
            options.InitialEndpoint.Port == 80 ||
            !string.Equals(options.InitialEndpoint.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Query) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.Fragment) ||
            !string.IsNullOrEmpty(options.InitialEndpoint.UserInfo))
        {
            throw new ArgumentException("The initial local AI endpoint must use an explicit IPv4 loopback port.", nameof(options));
        }
        if (options.StartupTimeout <= TimeSpan.Zero ||
            options.HealthPollInterval <= TimeSpan.Zero ||
            options.ShutdownTimeout <= TimeSpan.Zero ||
            options.RestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentException("Runtime timeouts must be positive.", nameof(options));
        }
        if (options.MaxRestartAttempts < 0 ||
            options.MaxLogBytes <= 0 ||
            options.LogBackupCount < 0 ||
            options.MaxLogLineCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Runtime limits are invalid.");
        }
        return options;
    }

    private static string Sanitize(string value) => TokenSanitizer.SanitizeLogMessage(value);

    private sealed record EndpointOwnershipObservation(
        bool IsComplete,
        Uri? Endpoint,
        string? ConflictDetail);
}
