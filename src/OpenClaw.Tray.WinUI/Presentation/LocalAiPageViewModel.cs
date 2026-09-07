using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal enum LocalAiEnginePresentationState { Running, Starting, Stopped, Error }
internal enum LocalAiModelPresentationState { Unknown, NotInstalled, Verified, Loaded }
internal enum LocalAiGatewayPresentationState { Connected, Connecting, NeedsAttention, Disconnected, Error }

/// <summary>WinUI-free presentation and action owner for the Local AI Hub page.</summary>
internal sealed class LocalAiPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private readonly ILocalAiRuntime _runtime;
    private readonly IPermissionsPageRuntimeSource _gatewaySource;
    private readonly IAppCommands _appCommands;
    private readonly IUiDispatcher _dispatcher;
    private readonly IHostHardwareProbe _hardwareProbe;
    private LocalAiRuntimeSnapshot _runtimeSnapshot;
    private GatewayConnectionSnapshot _gatewaySnapshot;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _availabilityCancellation;
    private bool _subscribed;
    private bool _disposed;
    private bool _isBusy;
    private string? _actionError;
    private bool _isAvailabilityKnown;
    private bool _isLocalAiAvailable;
    private bool _hasAvailabilityProbeError;
    private LocalInferenceUnavailableReason? _localAiUnavailableReason;

    public LocalAiPageViewModel(
        ILocalAiRuntime runtime,
        IPermissionsPageRuntimeSource gatewaySource,
        IAppCommands appCommands,
        IUiDispatcher dispatcher,
        IHostHardwareProbe hardwareProbe)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _runtimeSnapshot = runtime.Snapshot;
        _gatewaySnapshot = gatewaySource.Current.ConnectionSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool IsActive { get; private set; }
    internal bool IsDisposed => _disposed;

    public LocalAiEnginePresentationState EngineState => _runtimeSnapshot.State switch
    {
        LocalAiRuntimeState.Healthy => LocalAiEnginePresentationState.Running,
        LocalAiRuntimeState.Starting or LocalAiRuntimeState.Stopping => LocalAiEnginePresentationState.Starting,
        LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed => LocalAiEnginePresentationState.Error,
        _ => LocalAiEnginePresentationState.Stopped,
    };

    public string EngineStatusResourceKey => EngineState switch
    {
        LocalAiEnginePresentationState.Running => "LocalAiPage_Engine_Running",
        LocalAiEnginePresentationState.Starting => "LocalAiPage_Engine_Starting",
        LocalAiEnginePresentationState.Error => "LocalAiPage_Engine_Error",
        _ => "LocalAiPage_Engine_Stopped",
    };

    public string EngineOwnershipResourceKey => HasManagedInstall
        ? "LocalAiPage_Engine_Managed"
        : "LocalAiPage_Engine_NotInstalled";
    public string? EngineVersion => _runtimeSnapshot.EngineVersion;
    public string Endpoint => _runtimeSnapshot.Endpoint.ToString();
    public string? ProcessId => _runtimeSnapshot.ProcessId?.ToString();
    public string? EngineDetail => _runtimeSnapshot.Detail;
    public string? ModelName => LocalModelCatalog.FindInstalled(_runtimeSnapshot.ModelId)?.DisplayName ??
        _runtimeSnapshot.ModelId;
    public string ContextLengthText => _runtimeSnapshot.ContextLength is { } tokens
        ? FormatContext(tokens)
        : "Unknown";
    public string KvCacheText => FormatKvCache(_runtimeSnapshot);

    public LocalAiModelPresentationState ModelState => _runtimeSnapshot.ModelEvidence.State switch
    {
        LocalAiModelAvailabilityState.NotInstalled => LocalAiModelPresentationState.NotInstalled,
        LocalAiModelAvailabilityState.Verified => LocalAiModelPresentationState.Verified,
        LocalAiModelAvailabilityState.Loaded => LocalAiModelPresentationState.Loaded,
        _ => LocalAiModelPresentationState.Unknown,
    };

    public string ModelStatusResourceKey => ModelState switch
    {
        LocalAiModelPresentationState.NotInstalled => "LocalAiPage_Model_NotInstalled",
        LocalAiModelPresentationState.Verified => "LocalAiPage_Model_Verified",
        LocalAiModelPresentationState.Loaded => "LocalAiPage_Model_Loaded",
        _ => "LocalAiPage_Model_Unknown",
    };

    public LocalAiGatewayPresentationState GatewayState => _gatewaySnapshot.OperatorState switch
    {
        RoleConnectionState.Connected => LocalAiGatewayPresentationState.Connected,
        RoleConnectionState.Connecting => LocalAiGatewayPresentationState.Connecting,
        RoleConnectionState.PairingRequired => LocalAiGatewayPresentationState.NeedsAttention,
        RoleConnectionState.Error or RoleConnectionState.PairingRejected or RoleConnectionState.RateLimited =>
            LocalAiGatewayPresentationState.Error,
        _ => LocalAiGatewayPresentationState.Disconnected,
    };

    public string GatewayStatusResourceKey => GatewayState switch
    {
        LocalAiGatewayPresentationState.Connected => "LocalAiPage_Gateway_Connected",
        LocalAiGatewayPresentationState.Connecting => "LocalAiPage_Gateway_Connecting",
        LocalAiGatewayPresentationState.NeedsAttention => "LocalAiPage_Gateway_NeedsAttention",
        LocalAiGatewayPresentationState.Error => "LocalAiPage_Gateway_Error",
        _ => "LocalAiPage_Gateway_Disconnected",
    };

    public string? GatewayDetail => _gatewaySnapshot.GatewayName ?? _gatewaySnapshot.GatewayUrl;
    public string? ActionError => _actionError;
    public bool IsBusy => _isBusy;
    public bool IsAvailabilityKnown => _isAvailabilityKnown;
    public bool IsLocalAiAvailable => _isAvailabilityKnown && _isLocalAiAvailable;
    public bool HasAvailabilityProbeError => _hasAvailabilityProbeError;
    /// <summary>An availability probe (initial check or recheck) is currently in flight.</summary>
    public bool IsCheckingAvailability => _availabilityCancellation is not null;
    public bool ShowAvailabilityInfoBar =>
        (_isAvailabilityKnown && !_isLocalAiAvailable) || _hasAvailabilityProbeError || IsCheckingAvailability;
    public bool IsSetupAvailable => !_isAvailabilityKnown || _isLocalAiAvailable;
    public bool CanRecheckAvailability => _hasAvailabilityProbeError && _availabilityCancellation is null && !IsBusy;
    public LocalInferenceUnavailableReason? LocalAiUnavailableReason => _localAiUnavailableReason;
    public bool CanStart => !IsBusy && HasManagedInstall &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Stopped or LocalAiRuntimeState.Failed;
    public bool CanStop => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Healthy;
    public bool CanRestart => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy;
    public bool CanOpenLogs => !IsBusy && HasManagedInstall;
    public bool CanRetrySetup => IsSetupAvailable && !IsBusy && ModelState is
        LocalAiModelPresentationState.NotInstalled or LocalAiModelPresentationState.Unknown;
    public bool HasInstalledModel => ModelState is
        LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;
    public bool CanChangeModel => IsSetupAvailable && !IsBusy && HasInstalledModel;
    public bool CanRepairConnection => !IsBusy && GatewayState is not
        (LocalAiGatewayPresentationState.Connected or LocalAiGatewayPresentationState.Connecting);
    public bool CanOpenChat => !IsBusy &&
        GatewayState == LocalAiGatewayPresentationState.Connected &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy &&
        ModelState is LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;

    private bool HasManagedInstall =>
        _runtimeSnapshot.ModelEvidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded ||
        (_runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
         _runtimeSnapshot.State != LocalAiRuntimeState.NotInstalled);

    public void Activate(object? parameter)
    {
        ThrowIfDisposed();
        IsActive = true;
        if (!_subscribed)
        {
            _runtime.StateChanged += OnRuntimeStateChanged;
            _gatewaySource.Changed += OnGatewayChanged;
            _subscribed = true;
        }
        ApplyRuntimeSnapshot(_runtime.Snapshot);
        ApplyGatewaySnapshot(_gatewaySource.Current.ConnectionSnapshot);
        StartRuntimeRefresh();
        StartAvailabilityRefresh();
    }

    public void Deactivate()
    {
        CancelRuntimeRefresh();
        CancelAvailabilityRefresh();
        if (_subscribed)
        {
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _gatewaySource.Changed -= OnGatewayChanged;
            _subscribed = false;
        }
        IsActive = false;
    }

    public Task<bool> StartAsync() => RunRuntimeActionAsync(CanStart, _runtime.EnsureStartedAsync);
    public Task<bool> StopAsync() => RunRuntimeActionAsync(CanStop, _runtime.StopAsync);
    public Task<bool> RestartAsync() => RunRuntimeActionAsync(CanRestart, _runtime.RestartAsync);
    public bool OpenLogs() => RunCommand(CanOpenLogs, _appCommands.OpenLocalAiLogs);
    public bool RetrySetup() => RunCommand(CanRetrySetup, _appCommands.ShowLocalAiSetup);
    public bool ChangeModel() => RunCommand(CanChangeModel, _appCommands.ShowOnboarding);
    public bool RepairConnection() => RunCommand(CanRepairConnection, _appCommands.Reconnect);
    public bool OpenChat() => RunCommand(CanOpenChat, _appCommands.ShowChat);
    public bool RecheckAvailability()
    {
        ThrowIfDisposed();
        if (!IsActive || !CanRecheckAvailability)
            return false;
        StartAvailabilityRefresh();
        return true;
    }

    private static bool RunCommand(bool allowed, Action command)
    {
        if (!allowed)
            return false;
        command();
        return true;
    }

    private void StartRuntimeRefresh()
    {
        CancelRuntimeRefresh();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _ = RefreshRuntimeSnapshotAsync(cancellation);
    }

    private void CancelRuntimeRefresh()
    {
        // Same atomic grab-and-clear as CancelAvailabilityRefresh(): RefreshRuntimeSnapshotAsync's
        // finally block clears/disposes this token from a worker thread after ConfigureAwait(false),
        // so a plain read-then-write here could race it the same way.
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _refreshCancellation, null);
        cancellation?.Cancel();
    }

    private void StartAvailabilityRefresh()
    {
        CancelAvailabilityRefresh();
        _isAvailabilityKnown = false;
        _isLocalAiAvailable = false;
        _hasAvailabilityProbeError = false;
        _localAiUnavailableReason = null;
        var cancellation = new CancellationTokenSource();
        // Assign the new probe slot before notifying, so IsCheckingAvailability (which reads
        // _availabilityCancellation) already reports true in this notification. Notifying first
        // would report the pre-refresh "not checking" state, so a UI bound to PropertyChanged
        // (like the Hub page) would not reliably show checking/recheck progress until the probe
        // had already completed.
        _availabilityCancellation = cancellation;
        OnPropertyChanged(null);
        _ = RefreshAvailabilityAsync(cancellation);
    }

    private void CancelAvailabilityRefresh()
    {
        // Interlocked.Exchange atomically grabs and clears the field together, so this can
        // never race the worker-thread rejected-enqueue path in
        // ApplyAvailabilityResultOnUiThread: whichever side's atomic operation the CPU
        // actually applies first "wins" the token, and the other side observes the field
        // already null and does nothing further with it. A plain read-then-null-then-Cancel()
        // sequence would let this method capture the token into a local variable, lose the
        // field-ownership race to the worker thread's CompareExchange, and then still call
        // Cancel() on the copy the worker already disposed, throwing ObjectDisposedException.
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _availabilityCancellation, null);
        cancellation?.Cancel();
    }

    /// <summary>
    /// Locale-neutral placeholder used when the probe itself failed to produce facts (thrown
    /// exception, or a successful read that came back incomplete). The View resolves this kind
    /// into localized text; no language-specific text lives on the ViewModel.
    /// </summary>
    private static readonly LocalInferenceUnavailableReason ProbeFailureReason = new(
        LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete,
        ModelDisplayName: null,
        RequiredGigabytes: 0,
        DetectedGigabytes: null,
        DetectedDriverVersion: null,
        MinimumDriverVersion: string.Empty);

    private async Task RefreshAvailabilityAsync(CancellationTokenSource cancellation)
    {
        // The probe result is applied and the cancellation is released together, inside the
        // single dispatched callback below. Clearing _availabilityCancellation here (before the
        // dispatched callback runs) would make a queued-but-not-yet-run callback's own
        // IsCurrentAvailabilityProbe guard fail against itself, silently dropping a real
        // asynchronous DispatcherQueue completion.
        try
        {
            HostHardwareInfo hardware = await Task.Run(
                _hardwareProbe.Probe,
                cancellation.Token).ConfigureAwait(false);
            // Evaluate device-level eligibility (the best catalog model this hardware can run),
            // not the currently selected/installed model. A selection-specific failure (unknown,
            // deprecated, or oversized model) must not report the device itself as unavailable
            // and block retry-setup from switching to a compatible catalog model.
            LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
            if (eligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            {
                // Incomplete facts (a CUDA read that came back partial or transient) are
                // inconclusive, not a definitive "this device cannot run Local AI". Report it the
                // same way as a thrown probe failure below so recheck stays available instead of
                // permanently disabling Local AI on this device.
                ApplyAvailabilityResultOnUiThread(
                    cancellation,
                    isAvailabilityKnown: false,
                    isLocalAiAvailable: false,
                    hasAvailabilityProbeError: true,
                    ProbeFailureReason);
                return;
            }
            bool isAvailable = eligibility.CanInstall;
            LocalInferenceUnavailableReason? unavailableReason = isAvailable
                ? null
                : LocalInferenceEligibilityDiagnostics.GetUnavailableReason(eligibility);
            ApplyAvailabilityResultOnUiThread(
                cancellation,
                isAvailabilityKnown: true,
                isLocalAiAvailable: isAvailable,
                hasAvailabilityProbeError: false,
                unavailableReason);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer probe already replaced (or cleared) _availabilityCancellation, so this
            // probe is stale by definition. Just release the token; do not touch shared state.
            cancellation.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Local AI availability probe failed: {ex}");
            ApplyAvailabilityResultOnUiThread(
                cancellation,
                isAvailabilityKnown: false,
                isLocalAiAvailable: false,
                hasAvailabilityProbeError: true,
                ProbeFailureReason);
        }
    }

    /// <summary>
    /// Dispatches a completed availability probe's result to the UI thread, guaranteeing
    /// <paramref name="cancellation"/> is disposed exactly once no matter which path runs: applied
    /// inline, applied from a genuinely deferred dispatcher callback, or never applied at all
    /// because the ViewModel is already disposed/inactive or the dispatcher refused the enqueue.
    /// A plain <see cref="ApplyOnUiThread"/> call would silently leak the token on those last two
    /// paths, since its own no-op guard runs before (or instead of) the wrapped action.
    /// </summary>
    private void ApplyAvailabilityResultOnUiThread(
        CancellationTokenSource cancellation,
        bool isAvailabilityKnown,
        bool isLocalAiAvailable,
        bool hasAvailabilityProbeError,
        LocalInferenceUnavailableReason? unavailableReason)
    {
        if (_disposed || !IsActive)
        {
            // This check and the dispose below run on the probing worker thread (this whole
            // method is reached from RefreshAvailabilityAsync after ConfigureAwait(false)), so
            // it must not assume Deactivate()/Dispose() having flipped these plain, unsynchronized
            // flags means CancelAvailabilityRefresh() has already fully claimed and released this
            // exact token; that assumption depends on cross-thread visibility ordering these plain
            // fields don't guarantee. Route through the same atomic release path as the
            // rejected-enqueue branch below instead of disposing unconditionally.
            ReleaseAbandonedAvailabilityProbe(cancellation);
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            ApplyAvailabilityResult(
                cancellation, isAvailabilityKnown, isLocalAiAvailable, hasAvailabilityProbeError, unavailableReason);
            return;
        }

        bool enqueued = _dispatcher.TryEnqueue(() =>
        {
            // This callback runs on the UI thread (that is what TryEnqueue guarantees when it
            // returns true), the same thread CancelAvailabilityRefresh() runs on, so the two are
            // already serialized here; no atomic coordination is needed for this plain check.
            if (_disposed || !IsActive)
            {
                cancellation.Dispose();
                return;
            }
            ApplyAvailabilityResult(
                cancellation, isAvailabilityKnown, isLocalAiAvailable, hasAvailabilityProbeError, unavailableReason);
        });
        if (!enqueued)
        {
            // The dispatcher refused the enqueue (e.g. it is shutting down). This runs on the
            // probing worker thread, not the UI thread, so it must not call OnPropertyChanged
            // (WinUI's page code-behind touches controls from that notification and would throw
            // on the wrong thread) either.
            ReleaseAbandonedAvailabilityProbe(cancellation);
        }
    }

    /// <summary>
    /// Releases an availability-probe token from a worker thread when it will never reach
    /// <see cref="ApplyAvailabilityResult"/> (the ViewModel is disposed/inactive, or the
    /// dispatcher refused the enqueue). Interlocked.CompareExchange atomically claims the token:
    /// only the side that actually swaps it out for null may dispose it, so this can never
    /// dispose a token CancelAvailabilityRefresh() is concurrently (or has already) called
    /// Cancel() on. CancelAvailabilityRefresh() uses the matching Interlocked.Exchange to
    /// grab-and-clear the field atomically, so exactly one of the two atomic operations "wins"
    /// any given token; the loser observes the field already null and leaves its local copy of
    /// the token untouched. A rare undisposed CancellationTokenSource when the loser is this
    /// method is an acceptable trade-off for never risking ObjectDisposedException, since this
    /// class never registers a timeout that would give the token an unmanaged resource to leak.
    /// </summary>
    private void ReleaseAbandonedAvailabilityProbe(CancellationTokenSource cancellation)
    {
        if (Interlocked.CompareExchange(ref _availabilityCancellation, null, cancellation) == cancellation)
            cancellation.Dispose();
    }

    /// <summary>
    /// Applies a completed availability probe's result and releases its cancellation together,
    /// so the currency check and the state clear cannot race a real asynchronous DispatcherQueue
    /// callback. Always disposes <paramref name="cancellation"/>: a stale probe (superseded by a
    /// newer one) is no longer referenced by <see cref="_availabilityCancellation"/> by
    /// definition, so nothing else can still cancel or dispose this exact instance.
    /// </summary>
    private void ApplyAvailabilityResult(
        CancellationTokenSource cancellation,
        bool isAvailabilityKnown,
        bool isLocalAiAvailable,
        bool hasAvailabilityProbeError,
        LocalInferenceUnavailableReason? unavailableReason)
    {
        if (!IsCurrentAvailabilityProbe(cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _availabilityCancellation = null;
        _isAvailabilityKnown = isAvailabilityKnown;
        _isLocalAiAvailable = isLocalAiAvailable;
        _hasAvailabilityProbeError = hasAvailabilityProbeError;
        _localAiUnavailableReason = unavailableReason;
        OnPropertyChanged(null);
        cancellation.Dispose();
    }

    private bool IsCurrentAvailabilityProbe(CancellationTokenSource cancellation) =>
        ReferenceEquals(_availabilityCancellation, cancellation);

    private async Task RefreshRuntimeSnapshotAsync(CancellationTokenSource cancellation)
    {
        try
        {
            LocalAiRuntimeSnapshot snapshot = await _runtime.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            ApplyOnUiThread(() => ApplyRuntimeSnapshot(snapshot));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyOnUiThread(() =>
            {
                _actionError = ex.Message;
                OnPropertyChanged(null);
            });
        }
        finally
        {
            // Interlocked.CompareExchange pairs with CancelRuntimeRefresh()'s
            // Interlocked.Exchange: only the side that atomically wins clearing the field may
            // dispose this token, so a concurrent CancelRuntimeRefresh() on the UI thread can
            // never call Cancel() on an instance this worker thread already disposed. If
            // CancelRuntimeRefresh() already claimed the field first, this side leaves the
            // token undisposed rather than risk disposing it out from under an in-flight
            // Cancel() call; CancellationTokenSource has no unmanaged resource to leak unless a
            // timeout was registered, which this class never does.
            if (Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation) == cancellation)
                cancellation.Dispose();
        }
    }

    private async Task<bool> RunRuntimeActionAsync(
        bool allowed,
        Func<CancellationToken, Task<LocalAiRuntimeSnapshot>> action)
    {
        ThrowIfDisposed();
        if (!allowed || _isBusy)
            return false;
        _isBusy = true;
        _actionError = null;
        OnPropertyChanged(null);
        try
        {
            ApplyRuntimeSnapshot(await action(CancellationToken.None));
            return _runtimeSnapshot.State is not (LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed);
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
            OnPropertyChanged(null);
            return false;
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(null);
        }
    }

    private void OnRuntimeStateChanged(object? sender, LocalAiRuntimeSnapshotChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyRuntimeSnapshot(e.Snapshot));
    private void OnGatewayChanged(object? sender, PermissionsRuntimeSourceChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyGatewaySnapshot(e.Snapshot.ConnectionSnapshot));

    private void ApplyOnUiThread(Action action)
    {
        if (_disposed || !IsActive)
            return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => { if (!_disposed && IsActive) action(); });
    }

    private void ApplyRuntimeSnapshot(LocalAiRuntimeSnapshot snapshot)
    {
        _runtimeSnapshot = snapshot;
        OnPropertyChanged(null);
    }
    private void ApplyGatewaySnapshot(GatewayConnectionSnapshot snapshot)
    {
        _gatewaySnapshot = snapshot;
        OnPropertyChanged(null);
    }

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K"
            : tokens % 1000 == 0
                ? $"{tokens / 1000}K"
                : $"{tokens:N0} tokens";

    private static string FormatKvCache(LocalAiRuntimeSnapshot snapshot)
    {
        if (snapshot.KeyCachePrecision is not { } targetPrecision ||
            snapshot.DraftKeyCachePrecision is not { } draftPrecision)
        {
            return "Unknown";
        }

        string target = LocalModelCatalog.ToDisplayCacheType(targetPrecision);
        string draft = LocalModelCatalog.ToDisplayCacheType(draftPrecision);
        return target == draft ? $"{target} target + MTP draft" : $"{target} target + {draft} MTP draft";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Deactivate();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
