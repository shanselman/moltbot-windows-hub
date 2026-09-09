using System.Diagnostics;

namespace OpenClaw.SetupEngine;

// ─── Abstract Step Base ───

public abstract class SetupStep
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public abstract Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct);

    public virtual Task RollbackAsync(SetupContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual bool CanSkip(SetupContext ctx) => false;
    public virtual bool CanRetry => true;
    public virtual RetryPolicy Retry => RetryPolicy.Default;
}

// ─── Pipeline Result ───

public enum PipelineOutcome { Success, Failed, Cancelled }

public sealed record PipelineResult(
    PipelineOutcome Outcome,
    string? FailedStepId = null,
    string? Message = null,
    GatewayCompatibilityFailureKind? CompatibilityFailure = null,
    LocalAiFailureDetail? Detail = null)
{
    public bool RequiresRestart { get; init; }

    public int ExitCode => Outcome switch
    {
        PipelineOutcome.Success => 0,
        PipelineOutcome.Failed => 1,
        PipelineOutcome.Cancelled => 3,
        _ => 1
    };
}

// ─── Pipeline Events ───

public sealed record StepProgressEvent(string StepId, string DisplayName, StepOutcome? Outcome, TimeSpan? Elapsed);

public static class SetupStepFactory
{
    public static List<SetupStep> BuildWizardOnlySteps() =>
    [
        new RunGatewayWizardStep(),
        new WindowsNodeBootstrapContextStep(),
    ];

    public static List<SetupStep> BuildDefaultSteps()
    {
        return
        [
            new ValidateDistroInstallPathStep(),
            new PreflightOsStep(),
            new PreflightLocalAiHardwareStep(),
            new PreflightWslStep(),
            new PreflightWindowsTailscaleStep(),
            new EnsureWslPlatformStep(reusePreflightResult: true),
            new ReconcileLocalAiInstallationStep(),
            new AcquireLocalAiRuntimeStep(),
            new AcquireLocalAiModelStep(),
            new PersistLocalAiManifestStep(),
            new StartLocalAiRuntimeStep(),
            new CaptureLocalAiGpuBaselineStep(),
            new VerifyLocalAiInferenceStep(),
            new VerifyLocalAiGpuLoadStep(),
            new ConfigureLocalAiWslNetworkingStep(),
            new CleanupStaleDistroStep(),
            new CleanupStaleGatewayStep(),
            new PreflightPortStep(),
            new CreateWslInstanceStep(),
            new ConfigureWslInstanceStep(),
            new ValidateWslLockdownStep(),
            new InstallCliStep(),
            new VerifyLocalAiWslStep(),
            new InstallTailscaleStep(),
            new AuthorizeTailscaleStep(),
            new ConfigureGatewayStep(),
            new ConfigureLocalAiGatewayStep(),
            new InstallGatewayServiceStep(),
            new StartGatewayStep(),
            new FinalizeTailscaleServeStep(),
            new MintBootstrapTokenStep(),
            new PairOperatorStep(),
            new PairNodeStep(),
            new VerifyEndToEndStep(),
            new RunGatewayWizardStep(),
            new WindowsNodeBootstrapContextStep(),
            new StartKeepaliveStep(),
        ];
    }
}

// ─── Setup Pipeline ───

public sealed class SetupPipeline
{
    private readonly List<SetupStep> _steps;
    private readonly List<SetupStep> _completedSteps = new();
    private readonly bool? _rollbackOnFailureOverride;

    public event EventHandler<StepProgressEvent>? StepProgress;

    public SetupPipeline(IEnumerable<SetupStep> steps, bool? rollbackOnFailureOverride = null)
    {
        _steps = steps.ToList();
        _rollbackOnFailureOverride = rollbackOnFailureOverride;
    }

    internal static bool ShouldRunTrayArtifactCleanup(PipelineResult result, bool dryRun)
        => !dryRun &&
           !string.Equals(
               result.FailedStepId,
               ValidateDistroInstallPathStep.StepId,
               StringComparison.Ordinal);

    public async Task<PipelineResult> RunAsync(SetupContext ctx)
    {
        _completedSteps.Clear();
        var ct = ctx.CancellationToken;
        ctx.Journal.RecordPipelineEvent("pipeline_started", $"steps={_steps.Count}");
        ctx.Logger.Info($"Pipeline starting with {_steps.Count} steps", new { run_id = ctx.Logger.RunId });

        var pipelineSw = Stopwatch.StartNew();

        foreach (var step in _steps)
        {
            if (ct.IsCancellationRequested)
            {
                await RollbackCancelledSetupAsync(null, ctx);
                ctx.Journal.RecordPipelineEvent("pipeline_cancelled");
                return new PipelineResult(PipelineOutcome.Cancelled);
            }

            // Check if step can be skipped
            if (step.CanSkip(ctx))
            {
                ctx.Logger.Info($"Skipping step: {step.DisplayName}");
                ctx.Journal.RecordStepCompleted(step.Id, StepOutcome.Skipped, TimeSpan.Zero, "precondition met");
                StepProgress?.Invoke(this, new(step.Id, step.DisplayName, StepOutcome.Skipped, null));
                continue;
            }

            // Execute with retry
            ctx.Logger.StepStarted(step.Id, step.DisplayName);
            ctx.Journal.RecordStepStarted(step.Id);
            StepProgress?.Invoke(this, new(step.Id, step.DisplayName, null, null));

            var sw = Stopwatch.StartNew();
            StepResult result;

            if (step.CanRetry && step.Retry.MaxAttempts > 1)
            {
                try
                {
                    result = await RetryExecutor.ExecuteWithRetry(
                        () => step.ExecuteAsync(ctx, ct),
                        step.Retry,
                        ctx.Logger,
                        step.Id,
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await RollbackCancelledSetupAsync(step, ctx);
                    ctx.Journal.RecordPipelineEvent("pipeline_cancelled", $"during step {step.Id}");
                    return new PipelineResult(PipelineOutcome.Cancelled);
                }
            }
            else
            {
                try
                {
                    result = await step.ExecuteAsync(ctx, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await RollbackCancelledSetupAsync(step, ctx);
                    ctx.Journal.RecordPipelineEvent("pipeline_cancelled", $"during step {step.Id}");
                    return new PipelineResult(PipelineOutcome.Cancelled);
                }
                catch (Exception ex)
                {
                    result = StepResult.Fail($"Unhandled exception: {ex.Message}", ex);
                }
            }

            sw.Stop();
            ctx.Logger.StepCompleted(step.Id, result, sw.Elapsed);
            ctx.Journal.RecordStepCompleted(step.Id, result.Outcome, sw.Elapsed, result.Message);
            StepProgress?.Invoke(this, new(step.Id, step.DisplayName, result.Outcome, sw.Elapsed));

            if (result.IsSuccess)
            {
                _completedSteps.Add(step);
                continue;
            }

            // Step failed — handle rollback if configured.
            // When result.Error is set, StepCompleted already emitted an Error log for the
            // exception, so we downgrade the summary to Warn to avoid duplicate Errors.
            // When there is no exception (StepResult.Fail(message)), StepCompleted does NOT
            // emit Error, so we keep this summary at Error so failed steps remain visible on
            // Error-filtered dashboards.
            if (result.Error is null)
                ctx.Logger.Error($"SetupPipeline: Step '{step.Id}' failed: {result.Message}");
            else
                ctx.Logger.Warn($"SetupPipeline: Step '{step.Id}' failed: {result.Message}");

            if (_rollbackOnFailureOverride ?? ctx.Config.RollbackOnFailure)
            {
                await RollbackFailedStep(step, ctx);
                await RollbackCompletedSteps(ctx);
            }

            ctx.Journal.RecordPipelineEvent("pipeline_failed", $"step={step.Id}, message={result.Message}");
            return new PipelineResult(
                PipelineOutcome.Failed,
                step.Id,
                result.Message,
                (result.Error as GatewayCompatibilityException)?.Kind,
                result.Detail)
            {
                RequiresRestart = result.RequiresRestart,
            };
        }

        pipelineSw.Stop();
        ctx.Journal.RecordPipelineEvent("pipeline_completed", $"elapsed={pipelineSw.Elapsed.TotalSeconds:F1}s");
        ctx.Logger.Info($"Pipeline completed successfully in {pipelineSw.Elapsed.TotalSeconds:F1}s");
        return new PipelineResult(PipelineOutcome.Success);
    }

    private async Task RollbackCompletedSteps(SetupContext ctx)
        => await RollbackCompletedSteps(ctx, ctx.CancellationToken);

    private async Task RollbackCompletedSteps(SetupContext ctx, CancellationToken rollbackCancellationToken)
    {
        ctx.Logger.Warn($"Rolling back {_completedSteps.Count} completed steps");
        for (int i = _completedSteps.Count - 1; i >= 0; i--)
        {
            var step = _completedSteps[i];
            try
            {
                ctx.Logger.Info($"Rolling back: {step.DisplayName}");
                await RunRollbackWithTimeout(step, ctx, rollbackCancellationToken);
                ctx.Journal.RecordRollback(step.Id, success: true);
            }
            catch (OperationCanceledException) when (rollbackCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.Error($"Rollback failed for {step.Id}: {ex.Message}");
                ctx.Journal.RecordRollback(step.Id, success: false);
            }
        }
    }

    private static async Task RollbackFailedStep(SetupStep step, SetupContext ctx)
        => await RollbackFailedStep(step, ctx, ctx.CancellationToken);

    private static async Task RollbackFailedStep(
        SetupStep step,
        SetupContext ctx,
        CancellationToken rollbackCancellationToken)
    {
        ctx.Logger.Warn($"Attempting cleanup for failed step: {step.DisplayName}");

        try
        {
            await RunRollbackWithTimeout(step, ctx, rollbackCancellationToken);
            ctx.Journal.RecordRollback(step.Id, success: true);
        }
        catch (OperationCanceledException) when (rollbackCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.Error($"Cleanup failed for failed step {step.Id}: {ex.Message}");
            ctx.Journal.RecordRollback(step.Id, success: false);
        }
    }

    private async Task RollbackCancelledSetupAsync(SetupStep? interruptedStep, SetupContext ctx)
    {
        if (!(_rollbackOnFailureOverride ?? ctx.Config.RollbackOnFailure))
            return;

        // The setup token is already cancelled, so rollback needs its own bounded
        // lifetime. Each step still receives the configured rollback timeout.
        if (interruptedStep is not null)
            await RollbackFailedStep(interruptedStep, ctx, CancellationToken.None);
        await RollbackCompletedSteps(ctx, CancellationToken.None);
    }

    /// <summary>
    /// Runs all step rollbacks in reverse order (full teardown / uninstall).
    /// Unlike RollbackCompletedSteps, this runs ALL steps regardless of install state.
    /// Continues past individual failures to ensure maximum cleanup.
    /// </summary>
    public async Task<PipelineResult> UninstallAsync(SetupContext ctx)
    {
        _completedSteps.Clear();
        var ct = ctx.CancellationToken;

        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(
                ctx.LocalDataDir,
                ctx.DistroName,
                out _,
                out var pathError))
        {
            ctx.Logger.Error($"Uninstall refused unsafe WSL distro path: {pathError}");
            return new PipelineResult(
                PipelineOutcome.Failed,
                FailedStepId: ValidateDistroInstallPathStep.StepId,
                Message: pathError);
        }

        if (!ctx.Config.ConfirmDestructive && !ctx.Config.DryRun)
        {
            ctx.Logger.Error("Uninstall requires --confirm-destructive flag");
            return new PipelineResult(PipelineOutcome.Failed, Message: "Safety gate: --confirm-destructive required for live uninstall");
        }

        ctx.IsUninstalling = true;

        ctx.Journal.RecordPipelineEvent("uninstall_started", $"steps={_steps.Count}, dry_run={ctx.Config.DryRun}");
        ctx.Logger.Info($"Uninstall starting — {_steps.Count} steps in reverse order (dry_run={ctx.Config.DryRun})");

        var pipelineSw = Stopwatch.StartNew();
        var failures = new List<(string StepId, string Message)>();

        // Run rollbacks in reverse order
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            if (ct.IsCancellationRequested)
            {
                ctx.Journal.RecordPipelineEvent("uninstall_cancelled");
                return new PipelineResult(PipelineOutcome.Cancelled);
            }

            var step = _steps[i];
            ctx.Logger.Info($"Uninstalling: {step.DisplayName}");
            StepProgress?.Invoke(this, new(step.Id, $"Uninstall: {step.DisplayName}", null, null));

            var sw = Stopwatch.StartNew();
            try
            {
                if (ctx.Config.DryRun)
                {
                    ctx.Logger.Info($"[DRY RUN] Would rollback: {step.Id}");
                    ctx.Journal.RecordRollback(step.Id, success: true);
                }
                else
                {
                    await RunRollbackWithTimeout(step, ctx, ct);
                    ctx.Journal.RecordRollback(step.Id, success: true);
                }
                sw.Stop();
                StepProgress?.Invoke(this, new(step.Id, $"Uninstall: {step.DisplayName}", StepOutcome.Success, sw.Elapsed));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ctx.Journal.RecordPipelineEvent("uninstall_cancelled", $"during rollback of {step.Id}");
                return new PipelineResult(PipelineOutcome.Cancelled);
            }
            catch (Exception ex)
            {
                sw.Stop();
                ctx.Logger.Error($"Rollback failed for {step.Id}: {ex.Message}");
                ctx.Journal.RecordRollback(step.Id, success: false);
                failures.Add((step.Id, ex.Message));
                StepProgress?.Invoke(this, new(step.Id, $"Uninstall: {step.DisplayName}", StepOutcome.Failed, sw.Elapsed));
                // Continue past failures — best-effort cleanup
            }
        }

        pipelineSw.Stop();

        if (failures.Count > 0)
        {
            var failMsg = $"{failures.Count} rollback(s) failed: {string.Join(", ", failures.Select(f => f.StepId))}";
            ctx.Journal.RecordPipelineEvent("uninstall_completed_with_errors", failMsg);
            ctx.Logger.Warn($"Uninstall completed with errors in {pipelineSw.Elapsed.TotalSeconds:F1}s — {failMsg}");
            return new PipelineResult(PipelineOutcome.Failed, Message: failMsg);
        }

        ctx.Journal.RecordPipelineEvent("uninstall_completed", $"elapsed={pipelineSw.Elapsed.TotalSeconds:F1}s");
        ctx.Logger.Info($"Uninstall completed successfully in {pipelineSw.Elapsed.TotalSeconds:F1}s");
        return new PipelineResult(PipelineOutcome.Success);
    }

    private static async Task RunRollbackWithTimeout(SetupStep step, SetupContext ctx, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, ctx.Config.RollbackTimeoutSeconds)));

        try
        {
            await step.RollbackAsync(ctx, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Rollback for step '{step.Id}' exceeded {ctx.Config.RollbackTimeoutSeconds}s.");
        }
    }
}
