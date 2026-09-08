using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Selects one qualified NVIDIA GPU/runtime/model plan before setup mutates WSL,
/// downloads artifacts, or changes gateway configuration.
/// </summary>
public sealed class PreflightLocalAiHardwareStep : SetupStep
{
    private readonly IHostHardwareProbe _hardwareProbe;

    public PreflightLocalAiHardwareStep()
        : this(new CudaHostHardwareProbe())
    {
    }

    internal PreflightLocalAiHardwareStep(IHostHardwareProbe hardwareProbe) =>
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));

    public override string Id => "preflight-local-ai-hardware";
    public override string DisplayName => "Checking Local AI compatibility";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        HostHardwareInfo hardware;
        try
        {
            hardware = _hardwareProbe.Probe();
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI hardware detection failed. No setup changes were made.",
                ex));
        }

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(
            hardware,
            ctx.Config.LocalAi.SelectedModelId);
        ctx.LocalAiHardware = hardware;
        ctx.LocalAiEligibility = eligibility;
        ctx.Config.LocalAi.SelectedProfileId = eligibility.Plan?.Profile.Id;

        if (eligibility.Status == LocalInferenceEligibilityStatus.Unsupported)
        {
            return Task.FromResult(StepResult.Terminal(
                $"This system does not meet the Local AI requirements " +
                $"({eligibility.FailureCode}, {eligibility.SelectionFailureCode})."));
        }

        if (eligibility.Status == LocalInferenceEligibilityStatus.EligibleButBusy)
        {
            long requiredMiB = eligibility.RequiredFreeMemoryBytes / (1024 * 1024);
            long availableMiB = (eligibility.AvailableFreeMemoryBytes ?? 0) / (1024 * 1024);
            return Task.FromResult(StepResult.Terminal(
                $"The selected GPU is supported but currently busy. Local AI needs {requiredMiB:N0} MiB free; " +
                $"{availableMiB:N0} MiB is available. Close GPU applications and retry."));
        }

        if (eligibility.Plan is null || eligibility.SelectedGpu is null)
        {
            return Task.FromResult(StepResult.Terminal(
                "Local AI compatibility was inconclusive. No setup changes were made."));
        }

        if (!LocalAiPortPolicy.TryValidate(ctx.Config.LocalAi.Port, out string? portError))
            return Task.FromResult(StepResult.Terminal(portError ?? "Local inference port selection failed."));

        ctx.LocalAiPort = ctx.Config.LocalAi.Port;
        ctx.Logger.Info(
            "Selected qualified Local AI plan",
            new
            {
                runtime = eligibility.Plan.Runtime.Id,
                model = eligibility.Plan.Model.Id,
                selection = eligibility.Plan.ModelSelectionOrigin.ToString(),
                gpu = eligibility.SelectedGpu.StableId,
                requestedPort = ctx.Config.LocalAi.Port,
            });

        return Task.FromResult(StepResult.Ok(
            $"Selected {eligibility.Plan.Model.DisplayName} for {eligibility.SelectedGpu.Name}."));
    }
}

/// <summary>
/// Enables mirrored WSL networking only with explicit consent. This is the
/// sole Local AI setup step allowed to issue a global WSL shutdown.
/// </summary>
public sealed class ConfigureLocalAiWslNetworkingStep : SetupStep
{
    private readonly Func<SetupContext, IWslGlobalConfigManager> _managerFactory;

    public ConfigureLocalAiWslNetworkingStep()
        : this(CreateManager)
    {
    }

    internal ConfigureLocalAiWslNetworkingStep(
        Func<SetupContext, IWslGlobalConfigManager> managerFactory) =>
        _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));

    public override string Id => "configure-local-ai-wsl-networking";
    public override string DisplayName => "Configuring Local AI access from WSL";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        IWslGlobalConfigManager manager = _managerFactory(ctx);
        WslGlobalConfigStatus status;
        try
        {
            status = manager.Inspect();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal(
                $"The WSL configuration could not be safely inspected: {ex.Message}",
                ex);
        }

        if (status.IsMirrored)
            return StepResult.Skip("WSL mirrored networking is already enabled.");

        if (!ctx.Config.LocalAi.WslMirroredNetworkingConsent)
        {
            return StepResult.Terminal(
                "Local AI requires WSL mirrored networking. Consent is required because applying it stops all running WSL distributions once; no distributions are deleted.");
        }

        WslGlobalConfigApplyResult apply;
        try
        {
            apply = manager.ApplyMirroredNetworking();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal(
                $"WSL mirrored networking could not be configured: {ex.Message}",
                ex);
        }

        if (!apply.Changed)
            return StepResult.Skip("WSL mirrored networking is already enabled.");

        try
        {
            CommandResult shutdown = await ShutdownWslAsync(ctx, ct);
            if (shutdown.ExitCode != 0 || shutdown.TimedOut)
            {
                RestoreAfterFailedApply(manager, ctx);
                return StepResult.Fail(
                    "WSL mirrored networking was restored because WSL could not be stopped to apply it.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (manager.RestoreIfUnchanged() == WslGlobalConfigRestoreResult.Restored)
                await ShutdownWslAsync(ctx, CancellationToken.None);
            throw;
        }

        return StepResult.Ok("WSL mirrored networking is enabled.");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        IWslGlobalConfigManager manager = _managerFactory(ctx);
        WslGlobalConfigRestoreResult restore = manager.RestoreIfUnchanged();
        switch (restore)
        {
            case WslGlobalConfigRestoreResult.NoBackup:
                return;
            case WslGlobalConfigRestoreResult.UserModified:
                ctx.Logger.Warn("Preserving the user's newer .wslconfig instead of restoring the setup backup.");
                return;
            case WslGlobalConfigRestoreResult.InvalidBackup:
                throw new InvalidDataException("The Local AI WSL configuration backup is invalid.");
            case WslGlobalConfigRestoreResult.Restored:
                CommandResult shutdown = await ShutdownWslAsync(ctx, ct);
                if (shutdown.ExitCode != 0 || shutdown.TimedOut)
                    throw new InvalidOperationException("WSL could not be stopped to apply the restored configuration.");
                return;
            default:
                throw new InvalidOperationException($"Unknown WSL configuration restore result: {restore}.");
        }
    }

    private static IWslGlobalConfigManager CreateManager(SetupContext ctx)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configPath = Path.Combine(userProfile, ".wslconfig");
        string backupDirectory = Path.Combine(
            new LocalAiPaths(ctx.LocalDataDir).RootDirectory,
            "wsl-networking");
        return new WslGlobalConfigManager(configPath, backupDirectory);
    }

    private static Task<CommandResult> ShutdownWslAsync(SetupContext ctx, CancellationToken ct) =>
        ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--shutdown"],
            TimeSpan.FromSeconds(60),
            ct: ct);

    private static void RestoreAfterFailedApply(IWslGlobalConfigManager manager, SetupContext ctx)
    {
        WslGlobalConfigRestoreResult restore = manager.RestoreIfUnchanged();
        if (restore != WslGlobalConfigRestoreResult.Restored)
        {
            ctx.Logger.Error(
                $"Failed to restore .wslconfig after WSL shutdown failed: {restore}.");
        }
    }
}

/// <summary>Reuses a complete manifest-owned installation after current catalog verification.</summary>
public sealed class ReconcileLocalAiInstallationStep : SetupStep
{
    private readonly LocalAiInstallReconciler _reconciler;

    public ReconcileLocalAiInstallationStep()
        : this(new LocalAiInstallReconciler())
    {
    }

    internal ReconcileLocalAiInstallationStep(LocalAiInstallReconciler reconciler) =>
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));

    public override string Id => "reconcile-local-ai-installation";
    public override string DisplayName => "Checking for an existing Local AI installation";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiEligibility?.Plan is not { } plan ||
            ctx.LocalAiEligibility.SelectedGpu?.StableId is not { Length: > 0 } selectedGpuId)
        {
            return StepResult.Terminal(
                "Local AI installation recovery requires a qualified hardware plan.");
        }

        try
        {
            LocalAiReconcileResult result = await _reconciler
                .ReconcileAsync(ctx.LocalDataDir, plan, selectedGpuId, ct)
                .ConfigureAwait(false);
            if (!result.Reused)
                return StepResult.Skip("No completed managed Local AI installation was found.");

            ctx.LocalAiResolvedInstall = result.ResolvedInstall;
            ctx.LocalAiRuntimeInstall = result.RuntimeInstall;
            ctx.LocalAiModelInstall = result.ModelInstall;
            ctx.LocalAiPort = result.ResolvedInstall!.Manifest.RequestedPort;
            return StepResult.Ok("Reused the verified managed Local AI installation.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Terminal(
                $"The existing Local AI installation could not be reused safely: {ex.Message} " +
                "Run uninstall to remove it before retrying setup.",
                ex);
        }
    }
}

/// <summary>Installs the two pinned llama.cpp runtime archives as one atomic component.</summary>
public sealed class AcquireLocalAiRuntimeStep : SetupStep
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly ILlamaRuntimeAcquirer _acquirer;

    public AcquireLocalAiRuntimeStep()
        : this(new LlamaRuntimeInstaller(s_httpClient))
    {
    }

    internal AcquireLocalAiRuntimeStep(ILlamaRuntimeAcquirer acquirer) =>
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));

    public override string Id => "acquire-local-ai-runtime";
    public override string DisplayName => "Installing llama-server";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiRuntimeInstall is { CreatedThisRun: false })
            return StepResult.Skip("Reusing the verified managed llama-server runtime.");
        if (ctx.LocalAiEligibility?.Plan is not { } plan)
            return StepResult.Terminal("Local AI runtime installation requires a qualified hardware plan.");
        if (ctx.Config.LocalAi.AcquisitionTimeoutSeconds <= 0)
            return StepResult.Terminal("The Local AI acquisition timeout must be greater than zero.");

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ctx.Config.LocalAi.AcquisitionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var progress = new SynchronousProgress<LocalAiArtifactInstallProgress>(value =>
            {
                string archive = string.IsNullOrWhiteSpace(value.ArchiveFileName)
                    ? "llama-server runtime"
                    : value.ArchiveFileName;
                string detail = value.ArchiveCount > 1
                    ? $"{value.Phase}: {archive} ({value.ArchiveNumber}/{value.ArchiveCount})"
                    : $"{value.Phase}: {archive}";
                ctx.DetailProgress?.Report(new SetupDetailProgressEvent(
                    Id,
                    detail,
                    value.Completed,
                    value.Total,
                    value.Unit == LocalAiArtifactProgressUnit.Bytes
                        ? SetupDetailProgressUnit.Bytes
                        : value.Unit == LocalAiArtifactProgressUnit.Entries
                            ? SetupDetailProgressUnit.Items
                            : SetupDetailProgressUnit.None));
            });
            LlamaRuntimeInstallResult install = await _acquirer.InstallAsync(
                ctx.LocalDataDir,
                plan.Runtime,
                progress,
                linked.Token);
            ctx.LocalAiRuntimeInstall = install;
            return StepResult.Ok($"Installed llama-server {LlamaRuntimeCatalog.ReleaseTag}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return StepResult.Fail("The llama-server download timed out.", ex);
        }
        catch (Exception ex) when (
            ex is LocalAiArtifactInstallException
            or IOException
            or UnauthorizedAccessException
            or HttpRequestException)
        {
            return StepResult.Fail($"llama-server installation failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ctx.LocalAiRuntimeInstall is { } install)
        {
            _acquirer.RemoveInstalledRuntime(ctx.LocalDataDir, install);
            ctx.LocalAiRuntimeInstall = null;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Downloads one immutable, recipe-selected GGUF directly from Hugging Face.</summary>
public sealed class AcquireLocalAiModelStep : SetupStep
{
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly IHuggingFaceModelAcquirer _acquirer;

    public AcquireLocalAiModelStep()
        : this(new HuggingFaceModelInstaller(s_httpClient))
    {
    }

    internal AcquireLocalAiModelStep(IHuggingFaceModelAcquirer acquirer) =>
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));

    public override string Id => "acquire-local-ai-model";
    public override string DisplayName => "Downloading Local AI model from Hugging Face";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiModelInstall is { CreatedThisRun: false })
            return StepResult.Skip("Reusing the verified managed Local AI model.");
        if (ctx.LocalAiEligibility?.Plan is not { } plan)
            return StepResult.Terminal("Local AI model download requires a qualified hardware plan.");
        if (ctx.LocalAiRuntimeInstall is null)
            return StepResult.Terminal("Local AI model download requires the pinned llama-server runtime.");
        if (ctx.Config.LocalAi.AcquisitionTimeoutSeconds <= 0)
            return StepResult.Terminal("The Local AI acquisition timeout must be greater than zero.");

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ctx.Config.LocalAi.AcquisitionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var progress = new SynchronousProgress<HuggingFaceModelInstallProgress>(value =>
                ctx.DetailProgress?.Report(new SetupDetailProgressEvent(
                    Id,
                    value.Phase == HuggingFaceModelInstallPhase.Verifying
                        ? $"Verifying {plan.Model.Weights.RelativePath}"
                        : $"Downloading {plan.Model.Weights.RelativePath}",
                    value.CompletedBytes,
                    value.TotalBytes,
                    SetupDetailProgressUnit.Bytes)));
            HuggingFaceModelInstallResult install = await _acquirer.InstallAsync(
                ctx.LocalDataDir,
                LlamaRuntimeInstaller.Component(plan.Runtime),
                plan.Model,
                progress,
                linked.Token);
            ctx.LocalAiModelInstall = install;
            string action = install.Disposition == HuggingFaceModelInstallDisposition.ReusedVerified
                ? "Verified existing"
                : "Downloaded";
            return StepResult.Ok($"{action} {plan.Model.DisplayName} from its pinned Hugging Face revision.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return StepResult.Fail("The Hugging Face model download timed out.", ex);
        }
        catch (Exception ex) when (
            ex is HuggingFaceModelInstallException
            or IOException
            or UnauthorizedAccessException
            or HttpRequestException)
        {
            return StepResult.Fail($"Hugging Face model installation failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // A model already recorded here has already been downloaded (or reused) and
        // passed its pinned size/SHA-256 check: this step itself never sets
        // LocalAiModelInstall on failure, so rollback only reaches this branch because a
        // *later*, unrelated step failed (e.g. GPU/inference verification). Deleting a
        // multi-gigabyte, digest-verified file at that point would force a needless
        // re-download on retry, so it is left in the shared hub cache for the next run's
        // reuse-by-digest check to pick back up. Only the context handoff is undone.
        if (ctx.LocalAiModelInstall is not null)
            ctx.LocalAiModelInstall = null;
        if (ctx.LocalAiEligibility?.Plan is { } plan)
        {
            _acquirer.RemovePartialModel(
                ctx.LocalDataDir,
                LlamaRuntimeInstaller.Component(plan.Runtime),
                plan.Model);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Persists one immutable ownership and qualification receipt.</summary>
public sealed class PersistLocalAiManifestStep : SetupStep
{
    public override string Id => "persist-local-ai-manifest";
    public override string DisplayName => "Recording Local AI installation";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is not null && !ctx.LocalAiManifestCreatedThisRun)
            return StepResult.Skip("Reusing the matching managed Local AI installation receipt.");
        if (ctx.LocalAiEligibility?.Plan is not { } plan ||
            ctx.LocalAiEligibility.SelectedGpu is not { StableId: { Length: > 0 } gpuId } ||
            ctx.LocalAiPort is not { } requestedPort ||
            ctx.LocalAiRuntimeInstall is not { } runtimeInstall ||
            ctx.LocalAiModelInstall is not { } modelInstall)
        {
            return StepResult.Terminal(
                "The Local AI installation receipt requires completed hardware, runtime, and model steps.");
        }

        if (plan.Model.Weights.Source is not HuggingFaceRevisionSource modelSource)
            return StepResult.Terminal("The selected Local AI model does not have immutable Hugging Face provenance.");
        if (!LocalAiPortPolicy.TryValidate(requestedPort, out string? portError))
            return StepResult.Terminal(portError ?? "The requested Local AI port is invalid.");

        var paths = new LocalAiPaths(ctx.LocalDataDir);
        if (File.Exists(paths.ManifestPath))
            return StepResult.Terminal("A managed Local AI installation receipt already exists.");

        ImmutableArray<LocalAiAssetReceipt> runtimeAssets;
        try
        {
            runtimeAssets = BuildRuntimeReceipts(plan.Runtime, runtimeInstall);
        }
        catch (InvalidDataException ex)
        {
            return StepResult.Terminal(ex.Message, ex);
        }

        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = plan.Runtime.Architecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidDataException("The selected Local AI runtime architecture is unsupported."),
            },
            RuntimeId = plan.Runtime.Id,
            ModelCatalogId = plan.Model.Id,
            SelectedGpuId = gpuId,
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory, runtimeInstall.ExecutablePath),
            RuntimeAssets = runtimeAssets,
            ModelPath = modelInstall.ModelPath,
            ModelCacheRoot = modelInstall.CacheRoot,
            ModelId = $"{modelSource.RepositoryId}@{modelSource.RevisionSha}",
            ModelAlias = plan.Model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(plan.Model.Weights.RelativePath),
                SourceUrl = plan.Model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = plan.Model.Weights.SizeBytes,
                Sha256 = plan.Model.Weights.Sha256.Value,
            },
            RequestedPort = requestedPort,
            Endpoint = null,
            ContextLength = plan.Profile.ContextTokens,
            KeyCachePrecision = plan.Profile.KeyCachePrecision,
            ValueCachePrecision = plan.Profile.ValueCachePrecision,
            DraftKeyCachePrecision = plan.Profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = plan.Profile.DraftValueCachePrecision,
        };

        var store = new LocalAiManifestStore(paths);
        try
        {
            await store.SaveAsync(manifest, ct);
            ctx.LocalAiResolvedInstall = store.ResolveAndValidate(manifest);
            ctx.LocalAiManifestCreatedThisRun = true;
            return StepResult.Ok("Recorded the verified llama-server and Hugging Face installation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return StepResult.Fail($"The Local AI installation receipt could not be saved: {ex.Message}", ex);
        }
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.IsUninstalling)
        {
            ct.ThrowIfCancellationRequested();
            string root = new LocalAiPaths(ctx.LocalDataDir).RootDirectory;
            if (!LocalAiPathPolicy.TryDeleteManagedTree(
                    ctx.LocalDataDir,
                    root,
                    allowRoot: true,
                    out string error))
            {
                throw new InvalidDataException(
                    $"Managed Local AI files could not be removed safely: {error} " +
                    "Close the OpenClaw companion and retry uninstall.");
            }

            ctx.LocalAiRuntimeInstall = null;
            ctx.LocalAiModelInstall = null;
            ctx.LocalAiResolvedInstall = null;
            ctx.LocalAiManifestCreatedThisRun = false;
            return;
        }

        if (!ctx.LocalAiManifestCreatedThisRun)
            return;

        var paths = new LocalAiPaths(ctx.LocalDataDir);
        await new LocalAiManifestStore(paths).DeleteAsync(ct);
        ct.ThrowIfCancellationRequested();
        File.Delete(paths.RouterPresetPath);
        ctx.LocalAiResolvedInstall = null;
        ctx.LocalAiManifestCreatedThisRun = false;
    }

    private static ImmutableArray<LocalAiAssetReceipt> BuildRuntimeReceipts(
        LlamaRuntimeVariant runtime,
        LlamaRuntimeInstallResult install)
    {
        if (install.VerifiedArchives.Count != runtime.Artifacts.Count)
            throw new InvalidDataException("The installed llama-server archive receipt set is incomplete.");

        var receipts = ImmutableArray.CreateBuilder<LocalAiAssetReceipt>(runtime.Artifacts.Count);
        foreach (PinnedArtifact artifact in runtime.Artifacts)
        {
            string fileName = Path.GetFileName(artifact.RelativePath);
            LocalAiVerifiedArchive verified = install.VerifiedArchives.SingleOrDefault(
                candidate => string.Equals(candidate.FileName, fileName, StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' is missing.");
            if (verified.SizeBytes != artifact.SizeBytes ||
                !string.Equals(verified.Sha256, artifact.Sha256.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The installed llama-server archive receipt for '{fileName}' does not match its pin.");
            }

            receipts.Add(new LocalAiAssetReceipt
            {
                FileName = fileName,
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = verified.SizeBytes,
                Sha256 = verified.Sha256,
            });
        }

        return receipts.MoveToImmutable();
    }
}

/// <summary>Starts the companion-owned llama-server router without preloading a model.</summary>
public sealed class StartLocalAiRuntimeStep : SetupStep
{
    private readonly Func<SetupContext, ILocalAiRuntime> _runtimeFactory;

    public StartLocalAiRuntimeStep()
        : this(CreateRuntime)
    {
    }

    internal StartLocalAiRuntimeStep(Func<SetupContext, ILocalAiRuntime> runtimeFactory) =>
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));

    public override string Id => "start-local-ai-runtime";
    public override string DisplayName => "Starting llama-server router";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is null)
            return StepResult.Terminal("llama-server startup requires a verified installation receipt.");
        if (ctx.LocalAiRuntime is not null)
            return StepResult.Terminal("A Local AI runtime is already attached to this setup transaction.");

        ILocalAiRuntime runtime = _runtimeFactory(ctx);
        ctx.LocalAiRuntime = runtime;
        try
        {
            LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync(ct);
            if (snapshot.State != LocalAiRuntimeState.Healthy ||
                snapshot.Ownership != LocalAiOwnership.CompanionManaged ||
                snapshot.ProcessId is null ||
                snapshot.ModelEvidence.State != LocalAiModelAvailabilityState.Verified)
            {
                await DisposeRuntimeAsync(ctx);
                return StepResult.Fail(
                    snapshot.Detail ?? "The managed llama-server router did not become healthy.");
            }

            LocalAiResolvedInstall? verifiedInstall = await new LocalAiManifestStore(
                    new LocalAiPaths(ctx.LocalDataDir))
                .LoadAsync(ct);
            if (verifiedInstall?.Endpoint is null || verifiedInstall.Endpoint != snapshot.Endpoint)
            {
                await DisposeRuntimeAsync(ctx);
                return StepResult.Fail(
                    "llama-server became healthy without committing its verified endpoint receipt.");
            }
            ctx.LocalAiResolvedInstall = verifiedInstall;

            return StepResult.Ok(
                "The companion-owned llama-server router is healthy. The model remains unloaded until the first request.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeRuntimeAsync(ctx);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeRuntimeAsync(ctx);
            return StepResult.Fail($"llama-server startup failed: {ex.Message}", ex);
        }
    }

    public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) =>
        DisposeRuntimeAsync(ctx).AsTask();

    private static ILocalAiRuntime CreateRuntime(SetupContext ctx)
    {
        _ = ctx.LocalAiResolvedInstall
            ?? throw new InvalidOperationException("The Local AI installation receipt is unavailable.");
        return new LlamaServerRuntimeService(new LlamaServerRuntimeOptions
        {
            Paths = new LocalAiPaths(ctx.LocalDataDir),
            StartupTimeout = TimeSpan.FromSeconds(ctx.Config.LocalAi.HealthTimeoutSeconds),
        });
    }

    private static async ValueTask DisposeRuntimeAsync(SetupContext ctx)
    {
        if (ctx.LocalAiRuntime is null)
            return;

        ILocalAiRuntime runtime = ctx.LocalAiRuntime;
        ctx.LocalAiRuntime = null;
        await runtime.DisposeAsync();
    }
}

/// <summary>
/// Sends the setup-time first request and proves the exact model loaded. The
/// following GPU verification step restarts the router empty after collecting evidence.
/// </summary>
public sealed class VerifyLocalAiInferenceStep : SetupStep
{
    private readonly Func<ILlamaServerInferenceClient> _clientFactory;

    public VerifyLocalAiInferenceStep()
        : this(() => new LlamaServerInferenceClient())
    {
    }

    internal VerifyLocalAiInferenceStep(Func<ILlamaServerInferenceClient> clientFactory) =>
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

    public override string Id => "verify-local-ai-inference";
    public override string DisplayName => "Verifying Local AI model load";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiRuntime is not { } runtime ||
            ctx.LocalAiResolvedInstall is not { Endpoint: { } endpoint } install ||
            ctx.LocalAiEligibility?.Plan is not { } plan)
        {
            return StepResult.Terminal(
                "Local AI inference verification requires the managed router and qualified installation.");
        }
        if (runtime.Snapshot.State != LocalAiRuntimeState.Healthy ||
            runtime.Snapshot.Ownership != LocalAiOwnership.CompanionManaged)
        {
            return StepResult.Terminal("The managed llama-server router is not healthy.");
        }
        if (ctx.Config.LocalAi.InferenceTimeoutSeconds <= 0)
            return StepResult.Terminal("The Local AI inference timeout must be greater than zero.");

        using ILlamaServerInferenceClient client = _clientFactory();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ctx.Config.LocalAi.InferenceTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        LlamaServerInferenceVerification verification;
        LocalAiRuntimeSnapshot loaded;
        try
        {
            verification = await client.VerifyAsync(
                endpoint,
                plan.Model.Id,
                linked.Token);
            loaded = await runtime.RefreshAsync(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ResetRouterAsync(runtime);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // A stalled CUDA model load presents as a timeout, so this path needs the same
            // llama-server evidence as an explicit failure.
            LocalAiFailureDetail detail = await CaptureFailureDetailAsync(ctx, runtime);
            return StepResult.Fail("The first Local AI model load timed out.", ex, detail);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or IOException
            or InvalidDataException)
        {
            LocalAiFailureDetail detail = await CaptureFailureDetailAsync(ctx, runtime);
            return StepResult.Fail($"Local AI inference verification failed: {ex.Message}", ex, detail);
        }

        if (loaded.State != LocalAiRuntimeState.Healthy ||
            loaded.Ownership != LocalAiOwnership.CompanionManaged ||
            loaded.ModelEvidence.State != LocalAiModelAvailabilityState.Loaded ||
            !string.Equals(loaded.ModelEvidence.ServerModelId, plan.Model.Id, StringComparison.Ordinal))
        {
            return StepResult.Fail("llama-server completed a request but did not report the selected model as loaded.");
        }
        ctx.LocalAiInferenceVerification = verification;
        return StepResult.Ok(
            $"Verified {verification.CompletionTokens} generated tokens with the selected model.");
    }

    /// <summary>
    /// Reads llama-server's own failure lines, then resets the router. The order matters: the reset
    /// restarts llama-server, which appends a fresh startup banner and can rotate the failing lines
    /// out of the bounded log. Uses <see cref="CancellationToken.None"/> so a timed-out verification
    /// still yields evidence.
    /// </summary>
    private static async Task<LocalAiFailureDetail> CaptureFailureDetailAsync(
        SetupContext ctx,
        ILocalAiRuntime runtime)
    {
        var paths = new LocalAiPaths(ctx.LocalDataDir);
        IReadOnlyList<string> diagnostics =
            await LocalAiLogTail.ReadDiagnosticLinesAsync(paths, CancellationToken.None);
        await ResetRouterAsync(runtime);
        // Echo into the setup log the UI already links, so the root cause remains available if
        // the router restart rotates the managed llama-server logs.
        foreach (string line in diagnostics)
            ctx.Logger.Warn($"llama-server: {line}");
        return new LocalAiFailureDetail(diagnostics, paths.LogsDirectory);
    }

    internal static async Task<LocalAiRuntimeSnapshot> ResetRouterAsync(ILocalAiRuntime runtime)
    {
        try
        {
            return await runtime.RestartAsync(CancellationToken.None);
        }
        catch
        {
            return runtime.Snapshot;
        }
    }
}

/// <summary>Proves the app-owned WSL distro can reach the native loopback router.</summary>
public sealed class VerifyLocalAiWslStep : SetupStep
{
    private const string HealthMarker = "OPENCLAW_LOCAL_AI_HEALTH_B64=";
    private const string ModelsMarker = "OPENCLAW_LOCAL_AI_MODELS_B64=";
    private const int MaximumEvidenceBytes = 1024 * 1024;

    public override string Id => "verify-local-ai-wsl";
    public override string DisplayName => "Verifying Local AI access from WSL";

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiResolvedInstall is not { Endpoint: { } endpoint } install ||
            ctx.LocalAiRuntime is not { Snapshot.State: LocalAiRuntimeState.Healthy } ||
            ctx.LocalAiEligibility?.Plan is not { } plan ||
            string.IsNullOrWhiteSpace(ctx.DistroName))
        {
            return StepResult.Terminal(
                "WSL Local AI verification requires the healthy managed router and app-owned distro.");
        }

        string script = BuildProbeScript(endpoint.Port);
        CommandResult result = await ctx.Commands.RunInWslAsync(
            ctx.DistroName,
            script,
            TimeSpan.FromSeconds(45),
            ct: ct,
            user: ctx.Config.Wsl.User,
            inputViaStdin: true);
        if (result.TimedOut)
            return StepResult.Fail("The WSL Local AI reachability check timed out.");
        if (result.ExitCode != 0)
            return StepResult.Fail("The app-owned WSL distro could not reach the native llama-server router.");

        try
        {
            using JsonDocument health = DecodeMarker(result.Stdout, HealthMarker);
            using JsonDocument models = DecodeMarker(result.Stdout, ModelsMarker);
            ValidateHealth(health.RootElement);
            ValidateModel(models.RootElement, plan.Model.Id, install.ModelPath);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            return StepResult.Fail($"The WSL Local AI evidence was invalid: {ex.Message}", ex);
        }

        return StepResult.Ok(
            $"The app-owned WSL distro can reach llama-server on 127.0.0.1:{endpoint.Port}.");
    }

    internal static string BuildProbeScript(int port)
    {
        if (port is <= 0 or > 65_535 || port == 80)
            throw new ArgumentOutOfRangeException(nameof(port));

        return $$"""
            set -euo pipefail
            base_url='http://127.0.0.1:{{port}}'
            health_json="$(curl --fail --silent --show-error --max-time 15 "$base_url/health")"
            models_json="$(curl --fail --silent --show-error --max-time 15 "$base_url/models?autoload=false")"
            printf '{{HealthMarker}}%s\n' "$(printf '%s' "$health_json" | base64 -w0)"
            printf '{{ModelsMarker}}%s\n' "$(printf '%s' "$models_json" | base64 -w0)"
            """;
    }

    private static JsonDocument DecodeMarker(string stdout, string marker)
    {
        string? encoded = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))?
            [marker.Length..];
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumEvidenceBytes * 2)
            throw new InvalidDataException($"Missing or oversized evidence marker '{marker}'.");

        byte[] payload = Convert.FromBase64String(encoded);
        if (payload.Length > MaximumEvidenceBytes)
            throw new InvalidDataException($"Evidence marker '{marker}' exceeded the size limit.");
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
    }

    private static void ValidateHealth(JsonElement health)
    {
        if (health.ValueKind != JsonValueKind.Object ||
            !health.TryGetProperty("status", out JsonElement status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("llama-server did not report healthy status to WSL.");
        }
    }

    private static void ValidateModel(JsonElement root, string alias, string expectedPath)
    {
        if (LlamaServerModelStatusParser.Parse(root, alias, expectedPath) is null)
            throw new InvalidDataException("llama-server did not expose the selected managed model to WSL.");
    }
}
