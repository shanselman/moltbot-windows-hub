namespace OpenClaw.SetupEngine.Tests;

public class SetupPipelineTests
{
    private SetupLogger CreateLogger() => new(filePath: null, LogLevel.Trace);

    private SetupContext CreateContext(SetupConfig? config = null, CancellationToken ct = default)
    {
        var cfg = config ?? new SetupConfig();
        var logger = CreateLogger();
        var journal = new TransactionJournal(filePath: null);
        var commands = new CommandRunner(logger);
        return new SetupContext(cfg, logger, journal, commands, ct);
    }

    // A mock step for testing
    private sealed class MockStep : SetupStep
    {
        private readonly Func<SetupContext, CancellationToken, Task<StepResult>> _execute;
        private readonly Func<SetupContext, CancellationToken, Task>? _rollback;
        private readonly bool _canSkip;

        public override string Id { get; }
        public override string DisplayName { get; }
        public override bool CanRetry => false;

        public MockStep(string id, Func<SetupContext, CancellationToken, Task<StepResult>> execute,
            Func<SetupContext, CancellationToken, Task>? rollback = null,
            bool canSkip = false)
        {
            Id = id;
            DisplayName = id;
            _execute = execute;
            _rollback = rollback;
            _canSkip = canSkip;
        }

        public override bool CanSkip(SetupContext ctx) => _canSkip;
        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) => _execute(ctx, ct);
        public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) =>
            _rollback?.Invoke(ctx, ct) ?? Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_AllStepsSucceed_ReturnsSuccess()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CompatibilityFailure_PreservesTypedTerminalReason()
    {
        var compatibilityError = new GatewayCompatibilityException(
            GatewayCompatibilityFailureKind.ProtocolMismatch,
            "Expected protocol v4.");
        var pipeline = new SetupPipeline([
            new MockStep(
                "compatibility",
                (_, _) => Task.FromResult(
                    StepResult.Terminal(compatibilityError.Message, compatibilityError))),
        ]);

        var result = await pipeline.RunAsync(CreateContext());

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(GatewayCompatibilityFailureKind.ProtocolMismatch, result.CompatibilityFailure);
    }

    [Fact]
    public async Task RunAsync_RestartRequired_PreservesTypedTerminalReason()
    {
        var pipeline = new SetupPipeline([
            new MockStep(
                "restart",
                (_, _) => Task.FromResult(StepResult.RestartRequired("restart Windows"))),
        ]);

        var result = await pipeline.RunAsync(CreateContext());
        var (outcome, failedStepId, message, compatibilityFailure, detail) = result;

        Assert.Equal(PipelineOutcome.Failed, outcome);
        Assert.Equal("restart", failedStepId);
        Assert.Equal("restart Windows", message);
        Assert.Null(compatibilityFailure);
        Assert.Null(detail);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public void BuildDefaultSteps_IncludesCurrentSetupFlow()
    {
        var steps = SetupStepFactory.BuildDefaultSteps();

        Assert.Equal(37, steps.Count);
        Assert.IsType<ValidateDistroInstallPathStep>(steps[0]);
        Assert.IsType<PreflightOsStep>(steps[1]);
        Assert.IsType<PreflightLocalAiHardwareStep>(steps[2]);
        Assert.IsType<PreflightWslStep>(steps[3]);
        Assert.IsType<PreflightWindowsTailscaleStep>(steps[4]);
        Assert.IsType<EnsureWslPlatformStep>(steps[5]);
        Assert.IsType<ReconcileLocalAiInstallationStep>(steps[6]);
        Assert.IsType<AcquireLocalAiRuntimeStep>(steps[7]);
        Assert.IsType<AcquireLocalAiModelStep>(steps[8]);
        Assert.IsType<PersistLocalAiManifestStep>(steps[9]);
        Assert.IsType<StartLocalAiRuntimeStep>(steps[10]);
        Assert.IsType<CaptureLocalAiGpuBaselineStep>(steps[11]);
        Assert.IsType<VerifyLocalAiInferenceStep>(steps[12]);
        Assert.IsType<VerifyLocalAiGpuLoadStep>(steps[13]);
        Assert.IsType<ConfigureLocalAiWslNetworkingStep>(steps[14]);
        Assert.IsType<CleanupStaleDistroStep>(steps[15]);
        Assert.IsType<CleanupStaleGatewayStep>(steps[16]);
        Assert.Contains(steps, s => s is ValidateWslLockdownStep);
        var lockdownIndex = steps.FindIndex(s => s is ValidateWslLockdownStep);
        var cliInstallIndex = steps.FindIndex(s => s is InstallCliStep);
        Assert.Equal(lockdownIndex + 1, cliInstallIndex);
        Assert.IsType<VerifyLocalAiWslStep>(steps[cliInstallIndex + 1]);
        Assert.IsType<InstallTailscaleStep>(steps[cliInstallIndex + 2]);
        Assert.IsType<AuthorizeTailscaleStep>(steps[cliInstallIndex + 3]);
        var installServiceIndex = steps.FindIndex(s => s is InstallGatewayServiceStep);
        Assert.IsType<ConfigureLocalAiGatewayStep>(steps[installServiceIndex - 1]);
        Assert.IsType<StartGatewayStep>(steps[installServiceIndex + 1]);
        Assert.IsType<FinalizeTailscaleServeStep>(steps[installServiceIndex + 2]);
        Assert.Contains(steps, s => s is RunGatewayWizardStep);
        var pairNodeIndex = steps.FindIndex(s => s is PairNodeStep);
        Assert.IsType<VerifyEndToEndStep>(steps[pairNodeIndex + 1]);
        var wizardIndex = steps.FindIndex(s => s is RunGatewayWizardStep);
        Assert.IsType<WindowsNodeBootstrapContextStep>(steps[wizardIndex + 1]);
        Assert.IsType<StartKeepaliveStep>(steps[^1]);

        var ensureWslIndex = steps.FindIndex(step => step is EnsureWslPlatformStep);
        var preflightWslIndex = steps.FindIndex(step => step is PreflightWslStep);
        var localAiHardwareIndex = steps.FindIndex(step => step is PreflightLocalAiHardwareStep);
        var runtimeDownloadIndex = steps.FindIndex(step => step is AcquireLocalAiRuntimeStep);
        var modelDownloadIndex = steps.FindIndex(step => step is AcquireLocalAiModelStep);
        Assert.True(localAiHardwareIndex < preflightWslIndex);
        Assert.True(preflightWslIndex < ensureWslIndex);
        Assert.True(ensureWslIndex < runtimeDownloadIndex);
        Assert.True(ensureWslIndex < modelDownloadIndex);
    }

    [Fact]
    public void BuildLocalAiRecoverySteps_PreservesExistingWslGateway()
    {
        var steps = SetupStepFactory.BuildLocalAiRecoverySteps();

        Assert.DoesNotContain(steps, step => step is ValidateDistroInstallPathStep);
        Assert.Equal(2, steps.Count(step => step is ValidateLocalAiRecoveryGatewayStep));
        Assert.Contains(steps, step => step is PreserveLocalAiRecoveryGatewayStep);
        Assert.DoesNotContain(steps, step => step is CleanupStaleDistroStep);
        Assert.DoesNotContain(steps, step => step is CleanupStaleGatewayStep);
        Assert.DoesNotContain(steps, step => step is CreateWslInstanceStep);
        Assert.DoesNotContain(steps, step => step is ConfigureWslInstanceStep);
        Assert.DoesNotContain(steps, step => step is InstallCliStep);
        Assert.Contains(steps, step => step is AcquireLocalAiRuntimeStep);
        Assert.Contains(steps, step => step is AcquireLocalAiModelStep);
        Assert.Contains(steps, step => step is VerifyLocalAiWslStep);
        Assert.IsType<ConfigureLocalAiGatewayStep>(steps[^2]);
        Assert.IsType<RestartGatewayStep>(steps[^1]);
        Assert.True(
            steps.FindIndex(step => step is ValidateLocalAiRecoveryGatewayStep) <
            steps.FindIndex(step => step is AcquireLocalAiRuntimeStep));
        Assert.True(
            steps.FindIndex(step => step is PreserveLocalAiRecoveryGatewayStep) <
            steps.FindIndex(step => step is ConfigureLocalAiWslNetworkingStep));
        Assert.IsType<ValidateLocalAiRecoveryGatewayStep>(
            steps[steps.FindIndex(step => step is ConfigureLocalAiWslNetworkingStep) - 1]);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_MissingDistro_BlocksBeforeRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: false, appOwned: false),
            _ => [ManagedGatewayRecord()]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("run full setup", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_AppOwnedDistro_AllowsRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: true, appOwned: true),
            _ => [ManagedGatewayRecord()]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task ValidateLocalAiRecoveryGateway_OwnerDrift_BlocksBeforeRecovery()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new ValidateLocalAiRecoveryGatewayStep(
            (_, _, _, _) => ExistingLocalAiGateway(hasDistro: true, appOwned: true),
            _ => [ManagedGatewayRecord() with { Id = "replacement-gateway" }]);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.FailedTerminal, result.Outcome);
        Assert.Contains("owner changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreserveLocalAiRecoveryGateway_RestartsAfterWslShutdown()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        context.LocalAiRecoveryStoppedWsl = true;
        var restartCalls = 0;
        var step = new PreserveLocalAiRecoveryGatewayStep((_, _) =>
        {
            restartCalls++;
            return Task.FromResult(StepResult.Ok("restarted"));
        });

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(1, restartCalls);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public async Task RestartGatewayStep_FailureArmsRecoveryRollbackRestart()
    {
        var context = CreateContext(LocalAiRecoveryConfig());
        var step = new RestartGatewayStep((_, _) =>
            Task.FromResult(StepResult.Fail("restart failed")));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.True(context.LocalAiRecoveryStoppedWsl);
    }

    [Fact]
    public void LocalAiDisabled_SkipsEveryLocalAiMutation()
    {
        var ctx = CreateContext(new SetupConfig
        {
            LocalAi = new LocalAiConfig { Enabled = false }
        });
        var steps = SetupStepFactory.BuildDefaultSteps();
        SetupStep[] localAiSteps =
        [
            steps.Single(step => step is PreflightLocalAiHardwareStep),
            steps.Single(step => step is ReconcileLocalAiInstallationStep),
            steps.Single(step => step is AcquireLocalAiRuntimeStep),
            steps.Single(step => step is AcquireLocalAiModelStep),
            steps.Single(step => step is PersistLocalAiManifestStep),
            steps.Single(step => step is StartLocalAiRuntimeStep),
            steps.Single(step => step is CaptureLocalAiGpuBaselineStep),
            steps.Single(step => step is VerifyLocalAiInferenceStep),
            steps.Single(step => step is VerifyLocalAiGpuLoadStep),
            steps.Single(step => step is ConfigureLocalAiWslNetworkingStep),
            steps.Single(step => step is VerifyLocalAiWslStep),
            steps.Single(step => step is ConfigureLocalAiGatewayStep),
        ];

        Assert.All(localAiSteps, step => Assert.True(step.CanSkip(ctx), step.Id));
        Assert.False(steps.Single(step => step is PreflightWslStep).CanSkip(ctx));
        Assert.False(steps.Single(step => step is EnsureWslPlatformStep).CanSkip(ctx));
    }

    private static ExistingConfigDetector.ExistingConfig ExistingLocalAiGateway(
        bool hasDistro,
        bool appOwned) =>
        new(
            HasLocalGateway: true,
            LocalGatewayId: "gateway-id",
            LocalGatewayUrl: "ws://127.0.0.1:18789",
            HasDistro: hasDistro,
            HasDistroDataDirectory: hasDistro,
            DistroIsAppOwned: appOwned,
            DistroName: hasDistro ? "OpenClawGateway" : null,
            HasIdentityFiles: true,
            PreservedGatewayCount: 0,
            PreservedGatewayNames: []);

    private static SetupConfig LocalAiRecoveryConfig() => new()
    {
        DistroName = "OpenClawGateway",
        GatewayPort = 18789,
        LocalAiRecoveryGatewayId = "gateway-id",
    };

    private static OpenClaw.Connection.GatewayRecord ManagedGatewayRecord() => new()
    {
        Id = "gateway-id",
        Url = "ws://127.0.0.1:18789",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TailscaleDisabled_FreshAndReplacementPipelinesSkipOnlyTailscaleSteps(bool replacement)
    {
        var executed = new List<string>();
        var config = new SetupConfig
        {
            CleanBeforeRun = replacement,
            Tailscale = new TailscaleConfig { Enabled = false }
        };
        var ctx = CreateContext(config);
        var baselineStepId = replacement ? "replace-gateway" : "create-gateway";
        var pipeline = new SetupPipeline([
            new MockStep(baselineStepId, (_, _) =>
            {
                executed.Add(baselineStepId);
                return Task.FromResult(StepResult.Ok());
            }),
            new PreflightWindowsTailscaleStep(),
            new InstallTailscaleStep(),
            new AuthorizeTailscaleStep(),
            new FinalizeTailscaleServeStep(),
            new MockStep("pair", (_, _) =>
            {
                executed.Add("pair");
                return Task.FromResult(StepResult.Ok());
            }),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal([baselineStepId, "pair"], executed);
    }

    [Fact]
    public void BuildWizardOnlySteps_FinalizesWindowsNodeContextAfterWizard()
    {
        var steps = SetupStepFactory.BuildWizardOnlySteps();

        Assert.Collection(
            steps,
            step => Assert.IsType<RunGatewayWizardStep>(step),
            step => Assert.IsType<WindowsNodeBootstrapContextStep>(step));
    }

    [Fact]
    public async Task RunAsync_StepFails_ReturnsFailed()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Fail("broken"))),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal("s2", result.FailedStepId);
        Assert.Equal("broken", result.Message);
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_CallsRollbackInReverseOrder()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s2"); return Task.CompletedTask; }),
            new MockStep("s3",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_CleansUpFailedStepFirst()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Fail("fail")),
                (_, _) => { rollbackOrder.Add("s2"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s2" && e.Event == "rollback_ok");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_ContinuesWhenOneRollbackFails()
    {
        var rollbackOrder = new List<string>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackOrder.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) =>
                {
                    rollbackOrder.Add("s2");
                    throw new InvalidOperationException("rollback failed");
                }),
            new MockStep("s3",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s2", "s1"], rollbackOrder);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s2" && e.Event == "rollback_failed");
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s1" && e.Event == "rollback_ok");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollback_TimesOutHungRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = true, RollbackTimeoutSeconds = 1 };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                async (_, ct) =>
                {
                    rollbackCalled = true;
                    // slopwatch-ignore: SW004 Test deliberately blocks until cancellation to exercise cancellation behavior deterministically.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(rollbackCalled);
        Assert.Contains(ctx.Journal.Entries, e => e.StepId == "s1" && e.Event == "rollback_failed");
    }

    [Fact]
    public async Task RunAsync_StepFails_WithoutRollbackConfig_NoRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = false };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
            new MockStep("s2",
                (_, _) => Task.FromResult(StepResult.Fail("fail"))),
        ]);

        await pipeline.RunAsync(ctx);
        Assert.False(rollbackCalled);
    }

    [Fact]
    public async Task RunAsync_StepFails_WithRollbackOverrideDisabled_NoRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep(
                "refresh",
                (_, _) => Task.FromResult(StepResult.Fail("refresh failed")),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
        ], rollbackOnFailureOverride: false);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.False(rollbackCalled);
        Assert.True(config.RollbackOnFailure);
    }

    [Fact]
    public async Task RunAsync_SkippableStep_IsSkipped()
    {
        var executed = false;
        var ctx = CreateContext();
        var stepEvents = new List<StepProgressEvent>();

        var pipeline = new SetupPipeline([
            new MockStep("s1",
                (_, _) => { executed = true; return Task.FromResult(StepResult.Ok()); },
                canSkip: true),
        ]);
        
        pipeline.StepProgress += (sender, e) => stepEvents.Add(e);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(executed, "Step should not have executed when canSkip is true");
        
        // Verify the step was actually skipped via progress events
        var stepEvent = Assert.Single(stepEvents);
        Assert.Equal(StepOutcome.Skipped, stepEvent.Outcome);
    }

    [Fact]
    public async Task RunAsync_Cancellation_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ctx = CreateContext(ct: cts.Token);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringStep_RollsBackInterruptedThenCompletedWithFreshTokens()
    {
        using var cts = new CancellationTokenSource();
        var rollbacks = new List<(string StepId, bool WasCancelled)>();
        var config = new SetupConfig { RollbackOnFailure = true };
        var ctx = CreateContext(config, cts.Token);

        Task RecordRollback(string stepId, CancellationToken ct)
        {
            rollbacks.Add((stepId, ct.IsCancellationRequested));
            return Task.CompletedTask;
        }

        var pipeline = new SetupPipeline([
            new MockStep(
                "s1",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, ct) => RecordRollback("s1", ct)),
            new MockStep(
                "s2",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, ct) => RecordRollback("s2", ct)),
            new MockStep(
                "s3",
                (_, ct) =>
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(StepResult.Ok());
                },
                (_, ct) => RecordRollback("s3", ct)),
        ]);

        var result = await pipeline.RunAsync(ctx);

        Assert.Equal(PipelineOutcome.Cancelled, result.Outcome);
        Assert.Equal(
            [("s3", false), ("s2", false), ("s1", false)],
            rollbacks);
    }

    [Fact]
    public async Task RunAsync_StepThrowsException_ReturnsFail()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => throw new InvalidOperationException("unexpected")),
        ]);

        var result = await pipeline.RunAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("unexpected", result.Message);
    }

    [Fact]
    public async Task RunAsync_EmitsStepProgress()
    {
        var events = new List<StepProgressEvent>();
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);
        pipeline.StepProgress += (_, e) => events.Add(e);

        await pipeline.RunAsync(ctx);

        Assert.Equal(2, events.Count); // started + completed
        Assert.Null(events[0].Outcome); // started event has no outcome
        Assert.Equal(StepOutcome.Success, events[1].Outcome);
    }

    [Fact]
    public async Task RunAsync_RecordsJournal()
    {
        var ctx = CreateContext();
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        await pipeline.RunAsync(ctx);

        // Should have pipeline_started, step started, step completed, pipeline_completed
        Assert.True(ctx.Journal.Entries.Count >= 3);
        Assert.Equal("pipeline_started", ctx.Journal.Entries[0].Event);
    }

    [Fact]
    public async Task UninstallAsync_RequiresConfirmDestructive()
    {
        var config = new SetupConfig { ConfirmDestructive = false };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains("confirm-destructive", result.Message);
    }

    [Fact]
    public async Task UninstallAsync_DryRun_DoesNotRequireConfirmDestructive()
    {
        var config = new SetupConfig { ConfirmDestructive = false, DryRun = true };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok())),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task UninstallAsync_RejectsUnsafeDistroBeforeRollbacks()
    {
        var rollbackCalled = false;
        var config = new SetupConfig
        {
            ConfirmDestructive = true,
            DistroName = @"..\..",
        };
        var ctx = CreateContext(config);
        var pipeline = new SetupPipeline(
        [
            new MockStep(
                "unsafe",
                (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) =>
                {
                    rollbackCalled = true;
                    return Task.CompletedTask;
                }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(ValidateDistroInstallPathStep.StepId, result.FailedStepId);
        Assert.Contains("Invalid managed WSL distro name", result.Message);
        Assert.False(rollbackCalled);
        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(result, dryRun: false));
    }

    [Fact]
    public void TrayArtifactCleanup_RunsOnlyAfterValidatedLiveUninstall()
    {
        var validationFailure = new PipelineResult(
            PipelineOutcome.Failed,
            ValidateDistroInstallPathStep.StepId,
            "unsafe");

        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(validationFailure, dryRun: false));
        Assert.False(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Success),
            dryRun: true));
        Assert.True(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Failed, "other-step", "failed"),
            dryRun: false));
        Assert.True(SetupPipeline.ShouldRunTrayArtifactCleanup(
            new PipelineResult(PipelineOutcome.Cancelled),
            dryRun: false));
    }

    [Fact]
    public async Task UninstallAsync_RunsRollbacksInReverse()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s2"); return Task.CompletedTask; }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_ContinuesPastFailures()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s2"); throw new Exception("rollback failed"); }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        // All three rollbacks should have been attempted despite s2 failure
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_RollbackTimeout_ContinuesPastFailure()
    {
        var order = new List<string>();
        var config = new SetupConfig { ConfirmDestructive = true, RollbackTimeoutSeconds = 1 };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s1"); return Task.CompletedTask; }),
            new MockStep("s2", (_, _) => Task.FromResult(StepResult.Ok()),
                async (_, ct) =>
                {
                    order.Add("s2");
                    // slopwatch-ignore: SW004 Test deliberately blocks until cancellation to exercise cancellation behavior deterministically.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }),
            new MockStep("s3", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { order.Add("s3"); return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(["s3", "s2", "s1"], order);
    }

    [Fact]
    public async Task UninstallAsync_DryRun_DoesNotCallRollback()
    {
        var rollbackCalled = false;
        var config = new SetupConfig { ConfirmDestructive = true, DryRun = true };
        var ctx = CreateContext(config);

        var pipeline = new SetupPipeline([
            new MockStep("s1", (_, _) => Task.FromResult(StepResult.Ok()),
                (_, _) => { rollbackCalled = true; return Task.CompletedTask; }),
        ]);

        var result = await pipeline.UninstallAsync(ctx);
        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(rollbackCalled);
    }
}
