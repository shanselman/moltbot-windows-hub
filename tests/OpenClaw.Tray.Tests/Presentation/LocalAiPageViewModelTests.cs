using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Presentation;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    [Fact]
    public async Task UnsupportedHardware_KeepsExistingRuntimeManagementAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Idle,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanChangeModel);

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.IsSetupAvailable);
        Assert.Equal(LocalInferenceUnavailableReasonKind.NoNvidiaGpu, viewModel.LocalAiUnavailableReason?.Kind);
        Assert.False(viewModel.CanStart);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.False(viewModel.CanRetrySetup);
        Assert.False(viewModel.CanChangeModel);
        Assert.True(viewModel.CanRepairConnection);
        Assert.False(viewModel.CanOpenChat);
        Assert.True(await viewModel.StopAsync());
        Assert.True(await viewModel.RestartAsync());
        Assert.True(viewModel.OpenLogs());
        Assert.False(viewModel.RetrySetup());
        Assert.False(viewModel.ChangeModel());
        Assert.True(viewModel.RepairConnection());
        Assert.False(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(0, commands.ShowOnboardingCount);
        Assert.Equal(1, commands.ReconnectCount);
        Assert.Equal(0, commands.ShowChatCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.RestartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsChatAvailableForHealthyConnectedRuntime()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsInstalledStoppedRuntimeStartAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot(LocalAiRuntimeState.Stopped));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanStart);
        Assert.True(await viewModel.StartAsync());
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_BlocksFreshSetupRetry()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanRetrySetup);
    }

    /// <summary>
    /// A stale/unknown selected model ID (for example, a model removed from the catalog since
    /// it was configured) must not report qualified hardware as unavailable and block the user
    /// from retrying setup with a different, compatible catalog model.
    /// </summary>
    [Fact]
    public async Task UnknownSelectedModel_StillReportsQualifiedHardwareAsAvailable()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow) with
        {
            ModelId = "no-longer-in-catalog",
        });
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(CreateQualifiedHardware()));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanRetrySetup);
        Assert.Null(viewModel.LocalAiUnavailableReason);
    }

    /// <summary>
    /// Incomplete hardware facts (a partial/transient CUDA read, e.g. a GPU present without a
    /// stable ID or CUDA version) are inconclusive, not a definitive "unsupported device", so
    /// the page must classify this the same as a thrown probe failure: an error state that keeps
    /// recheck available, instead of a permanent IsLocalAiAvailable=false.
    /// </summary>
    [Fact]
    public async Task IncompleteHardwareFacts_IsTreatedAsRetryableProbeErrorNotDefinitiveUnavailable()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        HostHardwareInfo qualified = CreateQualifiedHardware();
        HostHardwareInfo incomplete = qualified with
        {
            Gpus = [qualified.Gpus[0] with { CudaMajorVersion = null }],
        };
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(incomplete));

        await ActivateAndWaitForAvailabilityResultAsync(viewModel);

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.HasAvailabilityProbeError);
        Assert.True(viewModel.CanRecheckAvailability);
        Assert.Equal(LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete, viewModel.LocalAiUnavailableReason?.Kind);
    }

    [Fact]
    public async Task ProbeFailure_UsesUnknownStateUntilRecheckSucceeds()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new SequencedHardwareProbe(
                () => throw new InvalidOperationException("probe failed"),
                CreateQualifiedHardware));

        Assert.False(viewModel.RecheckAvailability());

        await ActivateAndWaitForAvailabilityResultAsync(viewModel);

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.HasAvailabilityProbeError);
        Assert.True(viewModel.ShowAvailabilityInfoBar);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanRetrySetup);
        Assert.True(viewModel.CanRecheckAvailability);
        Assert.Equal(LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete, viewModel.LocalAiUnavailableReason?.Kind);

        Assert.True(viewModel.RecheckAvailability());
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.False(viewModel.ShowAvailabilityInfoBar);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.False(viewModel.CanRecheckAvailability);
        Assert.Null(viewModel.LocalAiUnavailableReason);

        // Availability is now a known success (no probe error), so a further recheck request is
        // not meaningful and must be refused rather than silently re-running the probe.
        Assert.False(viewModel.RecheckAvailability());
    }

    /// <summary>
    /// While an availability probe (initial check or a recheck) is in flight, the Hub must show
    /// explicit checking progress rather than silently hiding all availability chrome; the
    /// coordinating InfoBar visibility must stay up until a result (success or failure) applies.
    /// </summary>
    [Fact]
    public async Task IsCheckingAvailability_IsTrueWhileProbeInFlightAndClearsAfterResult()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var probeGate = new TaskCompletionSource();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new GatedHardwareProbe(probeGate.Task, CreateQualifiedHardware));

        Assert.False(viewModel.IsCheckingAvailability);

        viewModel.Activate(null);

        await WaitForConditionAsync(() => viewModel.IsCheckingAvailability, TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsCheckingAvailability);
        Assert.True(viewModel.ShowAvailabilityInfoBar);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.False(viewModel.CanRecheckAvailability);

        probeGate.TrySetResult();
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown);

        Assert.False(viewModel.IsCheckingAvailability);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.ShowAvailabilityInfoBar);
    }

    /// <summary>
    /// StartAvailabilityRefresh() must assign the new probe slot before raising its
    /// PropertyChanged notification, so a UI bound to that event (like the Hub page) reliably
    /// observes IsCheckingAvailability already true the moment a recheck starts, instead of only
    /// after the probe has already completed (which could hide the checking/recheck progress UI
    /// entirely for a fast probe).
    /// </summary>
    [Fact]
    public async Task RecheckAvailability_SynchronouslyNotifiesIsCheckingAvailability()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new SequencedHardwareProbe(
                () => throw new InvalidOperationException("probe failed"),
                CreateQualifiedHardware));

        await ActivateAndWaitForAvailabilityResultAsync(viewModel);
        Assert.True(viewModel.HasAvailabilityProbeError);

        bool observedCheckingInNotification = false;
        viewModel.PropertyChanged += (_, _) =>
        {
            if (viewModel.IsCheckingAvailability)
                observedCheckingInNotification = true;
        };

        Assert.True(viewModel.RecheckAvailability());

        // StartAvailabilityRefresh() runs synchronously up to its own notification (the probe
        // itself is fire-and-forget), so this must already be true by the time RecheckAvailability
        // returns -- not just eventually true once the probe happens to finish.
        Assert.True(observedCheckingInNotification);

        // A generous polling wait (rather than the shared WaitForAsync's fixed 5s event-driven
        // wait) tolerates thread-pool warm-up jitter from adjacent tests that briefly block a
        // worker thread (e.g. GatedHardwareProbe/BlockingFirstHardwareProbe); the assertion above
        // already proves the notification-ordering fix, so this is only draining the recheck's
        // background probe before the ViewModel is disposed.
        await WaitForConditionAsync(() => viewModel.IsAvailabilityKnown, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ProbeFailure_PublishesChangeWhenRecheckBecomesAvailable()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new SequencedHardwareProbe(
                () => throw new InvalidOperationException("probe failed")));

        var observed = new List<(bool HasError, bool CanRecheck)>();
        var recheckAvailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, _) =>
        {
            var snapshot = (viewModel.HasAvailabilityProbeError, viewModel.CanRecheckAvailability);
            observed.Add(snapshot);
            if (snapshot is (true, true))
                recheckAvailable.TrySetResult();
        };

        viewModel.Activate(null);
        await recheckAvailable.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(observed, state => state is (true, true));

        // The failed-probe result and the cancellation clear that unlocks recheck are applied
        // together in one property-changed notification, so an error-but-not-yet-rechecked
        // transient state must never be observed.
        Assert.DoesNotContain(observed, state => state is (true, false));
    }

    [Fact]
    public async Task QualifiedHardware_EnablesApplicableOptionsAndRoutesActions()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(CreateQualifiedHardware()));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.Null(viewModel.LocalAiUnavailableReason);
        Assert.Equal("256K", viewModel.ContextLengthText);
        Assert.Equal("Q8_0 target + MTP draft", viewModel.KvCacheText);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenLogs());
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task StaleAvailabilityProbe_DoesNotOverwriteNewerResult()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var probe = new BlockingFirstHardwareProbe(CreateQualifiedHardware);
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            probe);

        viewModel.Activate(null);
        await probe.FirstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.Deactivate();
        viewModel.Activate(null);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown && viewModel.IsLocalAiAvailable);

        probe.ReleaseFirstProbe();
        await Task.Delay(100);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.Null(viewModel.LocalAiUnavailableReason);
    }

    /// <summary>
    /// Guards against a probe-completion race: if the dispatcher defers the queued UI callback
    /// (a real WinUI <c>DispatcherQueue.TryEnqueue</c> callback does not run inline), the
    /// eventual callback must still see itself as the current probe and apply the successful
    /// result, instead of a premature field clear making it drop the result.
    /// </summary>
    [Fact]
    public async Task DelayedDispatch_AppliesSuccessfulProbeResultWhenQueuedCallbackRunsLater()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            dispatcher,
            new FixedHardwareProbe(CreateQualifiedHardware()));

        viewModel.Activate(null);

        // Activation starts both the runtime snapshot refresh and the availability probe; both
        // complete on a background thread and each queues one UI callback that the dispatcher
        // holds back instead of running inline.
        await WaitForConditionAsync(() => dispatcher.EnqueuedCount >= 2, TimeSpan.FromSeconds(5));

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.False(viewModel.CanRecheckAvailability);

        dispatcher.FlushPending();

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.Null(viewModel.LocalAiUnavailableReason);
        Assert.False(viewModel.CanRecheckAvailability);
    }

    /// <summary>
    /// Same race as <see cref="DelayedDispatch_AppliesSuccessfulProbeResultWhenQueuedCallbackRunsLater"/>,
    /// but for a failed probe: the deferred callback must still mark the probe error and leave
    /// recheck available once it finally runs.
    /// </summary>
    [Fact]
    public async Task DelayedDispatch_AppliesFailedProbeResultWhenQueuedCallbackRunsLater()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RunEnqueuedImmediately = false };
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            dispatcher,
            new SequencedHardwareProbe(() => throw new InvalidOperationException("probe failed")));

        viewModel.Activate(null);

        await WaitForConditionAsync(() => dispatcher.EnqueuedCount >= 2, TimeSpan.FromSeconds(5));

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.HasAvailabilityProbeError);

        dispatcher.FlushPending();

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.HasAvailabilityProbeError);
        Assert.True(viewModel.CanRecheckAvailability);
        Assert.Equal(LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete, viewModel.LocalAiUnavailableReason?.Kind);
    }

    /// <summary>
    /// If the dispatcher refuses the enqueue entirely (e.g. it is shutting down),
    /// <c>ApplyAvailabilityResultOnUiThread</c> must relinquish the current-probe slot before
    /// disposing the token, not just dispose it. Otherwise the ViewModel is stuck reporting
    /// <see cref="LocalAiPageViewModel.IsCheckingAvailability"/> forever, and a later
    /// deactivate/dispose tries to cancel the already-disposed token and throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task RejectedEnqueue_ReleasesProbeSlotAndDoesNotThrowOnLaterDeactivate()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var dispatcher = new RecordingUiDispatcher { HasThreadAccess = false, RejectEnqueue = true };
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            dispatcher,
            new FixedHardwareProbe(CreateQualifiedHardware()));

        viewModel.Activate(null);

        await WaitForConditionAsync(() => !viewModel.IsCheckingAvailability, TimeSpan.FromSeconds(5));

        // The probe result was never applied (the dispatcher refused it), but the probe slot
        // must be released so nothing is stuck "checking" forever.
        Assert.False(viewModel.IsCheckingAvailability);
        Assert.False(viewModel.IsAvailabilityKnown);

        // Deactivating must not try to Cancel() an already-disposed token.
        var exception = Record.Exception(() => viewModel.Deactivate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Verified)]
    [InlineData(LocalAiModelAvailabilityState.Loaded)]
    public void InstalledModel_OffersChangeModelThroughExistingSetupRoute(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.True(harness.ViewModel.CanChangeModel);
        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanRetrySetup);

        Assert.True(harness.ViewModel.ChangeModel());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Unknown)]
    [InlineData(LocalAiModelAvailabilityState.NotInstalled)]
    public void MissingModel_KeepsRetrySetupAndDoesNotOfferChangeModel(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.HasInstalledModel);
        Assert.True(harness.ViewModel.CanRetrySetup);

        Assert.False(harness.ViewModel.ChangeModel());
        Assert.True(harness.ViewModel.RetrySetup());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Fact]
    public async Task InstalledModel_RemainsVisibleButCannotOpenSetupDuringRuntimeAction()
    {
        using var harness = new LocalAiHarness(
            LocalAiModelAvailabilityState.Verified,
            LocalAiRuntimeState.Stopped);
        harness.Runtime.BlockStart();

        Task<bool> startTask = harness.ViewModel.StartAsync();

        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.ChangeModel());
        Assert.Equal(0, harness.Commands.ShowOnboardingCount);

        harness.Runtime.CompleteStart();
        Assert.True(await startTask);
    }

    [Fact]
    public void LocalAiPage_ChangeModelActionIsLocalizedAccessibleAndWired()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string pageDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages");
        string xaml = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml.cs"));

        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelButton\"", xaml);
        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelDescription\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiChangeModel\"", xaml);
        Assert.Contains("Click=\"OnChangeModel\"", xaml);
        Assert.Contains("_viewModel?.ChangeModel()", codeBehind);
    }

    private static async Task ActivateAndWaitForAvailabilityAsync(LocalAiPageViewModel viewModel)
    {
        await ActivateAndWaitForAvailabilityResultAsync(viewModel);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown);
    }

    private static async Task ActivateAndWaitForAvailabilityResultAsync(LocalAiPageViewModel viewModel)
    {
        viewModel.Activate(null);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown || viewModel.HasAvailabilityProbeError);
    }

    private static async Task WaitForAsync(LocalAiPageViewModel viewModel, Func<bool> condition)
    {
        if (condition())
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!condition())
                return;
            viewModel.PropertyChanged -= handler;
            completion.TrySetResult();
        };
        viewModel.PropertyChanged += handler;
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    private static HostHardwareInfo CreateQualifiedHardware() =>
        new(
            Architecture.X64,
            TotalPhysicalMemoryBytes: 256_000_000_000,
            AvailablePhysicalMemoryBytes: 128_000_000_000,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA Test GPU",
                    GpuVisibleMemoryBytes: 128_000_000_000,
                    FreeGpuVisibleMemoryBytes: 128_000_000_000,
                    DriverVersion: "620.0",
                    CudaMajorVersion: 13,
                    StableId: "GPU-test"),
            ],
            VulkanAvailable: false);

    private static LocalAiRuntimeSnapshot CreateInstalledSnapshot(
        LocalAiRuntimeState state = LocalAiRuntimeState.Healthy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string modelId = LocalModelCatalog.Models[0].Id;
        return new LocalAiRuntimeSnapshot(
            state,
            LocalAiOwnership.CompanionManaged,
            new Uri("http://127.0.0.1:18080"),
            "test",
            modelId,
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Verified,
                now,
                new string('0', 64),
                sizeBytes: 1),
            ProcessId: 1234,
            ProcessStartedAtUtc: now,
            Detail: null,
            UpdatedAtUtc: now,
            ContextLength: LocalModelCatalog.NativeContextTokens,
            KeyCachePrecision: KvCachePrecision.Q8_0,
            ValueCachePrecision: KvCachePrecision.Q8_0,
            DraftKeyCachePrecision: KvCachePrecision.Q8_0,
            DraftValueCachePrecision: KvCachePrecision.Q8_0);
    }

    private sealed class FixedHardwareProbe(HostHardwareInfo hardware) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => hardware;
    }

    private sealed class SequencedHardwareProbe(params Func<HostHardwareInfo>[] attempts) : IHostHardwareProbe
    {
        private readonly Queue<Func<HostHardwareInfo>> _attempts = new(attempts);

        public HostHardwareInfo Probe()
        {
            if (_attempts.Count == 0)
                throw new InvalidOperationException("No probe attempts configured.");
            return _attempts.Dequeue().Invoke();
        }
    }

    private sealed class BlockingFirstHardwareProbe(Func<HostHardwareInfo> secondAttempt) : IHostHardwareProbe
    {
        private readonly TaskCompletionSource _firstProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstProbe =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public TaskCompletionSource FirstProbeStarted => _firstProbeStarted;

        public HostHardwareInfo Probe()
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                _firstProbeStarted.TrySetResult();
                _releaseFirstProbe.Task.GetAwaiter().GetResult();
                return HostHardwareInfo.Unknown;
            }

            return secondAttempt();
        }

        public void ReleaseFirstProbe() => _releaseFirstProbe.TrySetResult();
    }

    /// <summary>Blocks inside <see cref="Probe"/> until <paramref name="release"/> completes, so a
    /// test can observe the ViewModel's "checking" state before letting the probe resolve.</summary>
    private sealed class GatedHardwareProbe(Task release, Func<HostHardwareInfo> result) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe()
        {
            release.GetAwaiter().GetResult();
            return result();
        }
    }

    /// <summary>Harness for the "installed model" (change-model / retry-setup) scenarios, which
    /// exercise runtime/model state independent of the hardware-availability probe.</summary>
    private sealed class LocalAiHarness : IDisposable
    {
        private readonly FakeLocalAiRuntime _runtime;
        private readonly PermissionsPageRuntimeSource _gatewaySource;
        private readonly RecordingUiDispatcher _dispatcher;

        public LocalAiHarness(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState = LocalAiRuntimeState.Healthy)
        {
            _runtime = new FakeLocalAiRuntime(CreateSnapshot(modelState, runtimeState));
            var gatewayHost = new FakePermissionsPageRuntimeHost();
            _gatewaySource = new PermissionsPageRuntimeSource(gatewayHost);
            Commands = new FakeAppCommands();
            _dispatcher = new RecordingUiDispatcher();
            ViewModel = new LocalAiPageViewModel(
                _runtime,
                _gatewaySource,
                Commands,
                _dispatcher,
                new FixedHardwareProbe(HostHardwareInfo.Unknown));
        }

        public FakeAppCommands Commands { get; }
        public FakeLocalAiRuntime Runtime => _runtime;
        public LocalAiPageViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            Commands.Dispose();
            _gatewaySource.Dispose();
            _dispatcher.Dispose();
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static LocalAiRuntimeSnapshot CreateSnapshot(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LocalAiModelEvidence evidence = modelState switch
            {
                LocalAiModelAvailabilityState.Verified => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024),
                LocalAiModelAvailabilityState.Loaded => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024,
                    "test-model"),
                LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
                _ => LocalAiModelEvidence.Unknown(now),
            };

            return new LocalAiRuntimeSnapshot(
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? runtimeState
                    : LocalAiRuntimeState.NotInstalled,
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? LocalAiOwnership.CompanionManaged
                    : LocalAiOwnership.None,
                new Uri("http://127.0.0.1:11983"),
                "test",
                "test-model",
                evidence,
                null,
                null,
                null,
                now);
        }
    }

    private sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        private TaskCompletionSource<LocalAiRuntimeSnapshot>? _startCompletion;

        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int RestartCount { get; private set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        /// <summary>Makes <see cref="EnsureStartedAsync"/> await until <see cref="CompleteStart"/> is
        /// called, so tests can observe view-model state while a start is still in flight.</summary>
        public void BlockStart() =>
            _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteStart() => _startCompletion?.TrySetResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return _startCompletion?.Task ?? Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
