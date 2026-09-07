using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiGpuLoadEvidence(
    int ProcessId,
    string SelectedGpuId,
    string CudaModulePath,
    int OffloadedLayers,
    int TotalLayers,
    long TotalGpuVisibleBytes,
    long FreeGpuVisibleBytesBeforeLoad,
    long? FreeGpuVisibleBytesAfterLoad,
    long? CudaModelBufferBytes)
{
    public long? UsedGpuVisibleBytesAfterLoad =>
        FreeGpuVisibleBytesAfterLoad is { } freeBytes ? TotalGpuVisibleBytes - freeBytes : null;
    public long? LoadDeltaBytes =>
        FreeGpuVisibleBytesAfterLoad is { } freeBytes ? FreeGpuVisibleBytesBeforeLoad - freeBytes : null;
}

internal interface ILocalAiGpuEvidenceProbe
{
    Task<LocalAiGpuLoadEvidence> CaptureAsync(
        int processId,
        string selectedGpuId,
        HostHardwareInfo baseline,
        LocalAiPaths paths,
        CancellationToken cancellationToken);
}

internal sealed partial class WindowsLocalAiGpuEvidenceProbe : ILocalAiGpuEvidenceProbe
{
    private readonly IHostHardwareProbe _hardwareProbe;

    public WindowsLocalAiGpuEvidenceProbe()
        : this(new CudaHostHardwareProbe())
    {
    }

    internal WindowsLocalAiGpuEvidenceProbe(IHostHardwareProbe hardwareProbe) =>
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));

    public async Task<LocalAiGpuLoadEvidence> CaptureAsync(
        int processId,
        string selectedGpuId,
        HostHardwareInfo baseline,
        LocalAiPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedGpuId);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(paths);

        string cudaModule = FindCudaModule(processId);
        LocalAiGpuLogEvidence logEvidence = await ReadGpuLoadEvidenceAsync(paths, cancellationToken);
        (long totalBytes, long freeBeforeBytes, long? freeAfterBytes) = ResolveGpuMemoryEvidence(
            selectedGpuId,
            baseline,
            logEvidence,
            _hardwareProbe.Probe);

        return new LocalAiGpuLoadEvidence(
            processId,
            selectedGpuId,
            cudaModule,
            logEvidence.OffloadedLayers,
            logEvidence.TotalLayers,
            totalBytes,
            freeBeforeBytes,
            freeAfterBytes,
            logEvidence.CudaModelBufferBytes);
    }

    internal static (long TotalBytes, long FreeBeforeBytes, long? FreeAfterBytes) ResolveGpuMemoryEvidence(
        string selectedGpuId,
        HostHardwareInfo baseline,
        LocalAiGpuLogEvidence logEvidence,
        Func<HostHardwareInfo> currentProbe)
    {
        GpuInfo before = FindGpu(baseline, selectedGpuId);
        if (before.GpuVisibleMemoryBytes is not > 0 || before.FreeGpuVisibleMemoryBytes is not >= 0)
            throw new InvalidDataException("The selected GPU baseline memory evidence was incomplete.");

        if (logEvidence.CudaModelBufferBytes is > 0)
        {
            return (
                before.GpuVisibleMemoryBytes.Value,
                before.FreeGpuVisibleMemoryBytes.Value,
                null);
        }

        GpuInfo after = FindGpu(currentProbe(), selectedGpuId);
        if (after.GpuVisibleMemoryBytes != before.GpuVisibleMemoryBytes ||
            after.FreeGpuVisibleMemoryBytes is not >= 0)
        {
            throw new InvalidDataException("The selected GPU memory evidence was incomplete or changed during model loading.");
        }

        return (
            after.GpuVisibleMemoryBytes.Value,
            before.FreeGpuVisibleMemoryBytes.Value,
            after.FreeGpuVisibleMemoryBytes.Value);
    }

    internal static (int Offloaded, int Total) ParseFullOffloadEvidence(string log)
    {
        LocalAiGpuLogEvidence evidence = ParseGpuLoadEvidence(log);
        return (evidence.OffloadedLayers, evidence.TotalLayers);
    }

    internal static LocalAiGpuLogEvidence ParseGpuLoadEvidence(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        MatchCollection matches = FullOffloadPattern().Matches(log);
        foreach (Match match in matches.Cast<Match>().Reverse())
        {
            if (int.TryParse(match.Groups[1].Value, out int offloaded) &&
                int.TryParse(match.Groups[2].Value, out int total) &&
                offloaded > 0 && offloaded == total)
            {
                return new LocalAiGpuLogEvidence(
                    offloaded,
                    total,
                    ParseCudaModelBufferBytes(log));
            }
        }
        throw new InvalidDataException("llama-server did not report full GPU layer offload.");
    }

    private static string FindCudaModule(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(module.ModuleName, "ggml-cuda.dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(module.FileName) &&
                    Path.IsPathFullyQualified(module.FileName))
                {
                    return module.FileName;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("The managed llama-server CUDA module could not be inspected.", ex);
        }
        throw new InvalidDataException("The managed llama-server process did not load ggml-cuda.dll.");
    }

    private static async Task<LocalAiGpuLogEvidence> ReadGpuLoadEvidenceAsync(
        LocalAiPaths paths,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        InvalidDataException? lastFailure = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            string log = await LocalAiLogTail.ReadCombinedTailAsync(
                paths, LocalAiLogTail.GpuEvidenceTailBytes, cancellationToken);
            try
            {
                return ParseGpuLoadEvidence(log);
            }
            catch (InvalidDataException ex)
            {
                lastFailure = ex;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);
        throw lastFailure ?? new InvalidDataException("llama-server GPU offload evidence was unavailable.");
    }

    private static GpuInfo FindGpu(HostHardwareInfo hardware, string selectedGpuId) =>
        hardware.Gpus.SingleOrDefault(gpu =>
            string.Equals(gpu.StableId, selectedGpuId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("The selected GPU was not present in the verification probe.");

    [GeneratedRegex(@"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers\s+to\s+GPU", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullOffloadPattern();

    [GeneratedRegex(@"CUDA\d+\s+model buffer size\s*=\s*([0-9]+(?:\.[0-9]+)?)\s+MiB", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CudaModelBufferPattern();

    private static long? ParseCudaModelBufferBytes(string log)
    {
        Match? match = CudaModelBufferPattern().Matches(log).Cast<Match>().LastOrDefault();
        if (match is null ||
            !double.TryParse(
                match.Groups[1].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double mebibytes) ||
            mebibytes <= 0)
        {
            return null;
        }

        double bytes = mebibytes * 1024 * 1024;
        return bytes <= long.MaxValue ? (long)bytes : null;
    }
}

internal sealed record LocalAiGpuLogEvidence(
    int OffloadedLayers,
    int TotalLayers,
    long? CudaModelBufferBytes);

public sealed class CaptureLocalAiGpuBaselineStep : SetupStep
{
    private readonly IHostHardwareProbe _probe;
    public CaptureLocalAiGpuBaselineStep() : this(new CudaHostHardwareProbe()) { }
    internal CaptureLocalAiGpuBaselineStep(IHostHardwareProbe probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public override string Id => "capture-local-ai-gpu-baseline";
    public override string DisplayName => "Capturing GPU baseline";
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;
    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            ctx.LocalAiGpuBaseline = _probe.Probe();
            return Task.FromResult(StepResult.Ok("Captured the selected GPU memory baseline"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(StepResult.Fail("The selected GPU baseline could not be captured.", ex));
        }
    }
}

public sealed class VerifyLocalAiGpuLoadStep : SetupStep
{
    private readonly ILocalAiGpuEvidenceProbe _probe;
    private readonly Func<SetupContext, CancellationToken, Task<LocalAiResolvedInstall?>> _installLoader;

    public VerifyLocalAiGpuLoadStep()
        : this(new WindowsLocalAiGpuEvidenceProbe())
    {
    }

    internal VerifyLocalAiGpuLoadStep(
        ILocalAiGpuEvidenceProbe probe,
        Func<SetupContext, CancellationToken, Task<LocalAiResolvedInstall?>>? installLoader = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _installLoader = installLoader ?? LoadResolvedInstallAsync;
    }

    public override string Id => "verify-local-ai-gpu-load";
    public override string DisplayName => "Verifying Local AI GPU placement";
    public override bool CanRetry => false;
    public override RetryPolicy Retry => RetryPolicy.None;
    public override bool CanSkip(SetupContext ctx) => !ctx.Config.LocalAi.Enabled;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        if (ctx.LocalAiRuntime is not { } runtime ||
            ctx.LocalAiResolvedInstall is not { } install ||
            ctx.LocalAiGpuBaseline is not { } baseline ||
            ctx.LocalAiEligibility?.Plan is not { } plan ||
            ctx.LocalAiEligibility.SelectedGpu?.StableId is not { Length: > 0 } gpuId ||
            ctx.LocalAiInferenceVerification is null ||
            runtime.Snapshot is not { State: LocalAiRuntimeState.Healthy, Ownership: LocalAiOwnership.CompanionManaged,
                ModelEvidence.State: LocalAiModelAvailabilityState.Loaded, ProcessId: not null })
        {
            return StepResult.Terminal("GPU verification requires a loaded managed model and selected GPU baseline.");
        }

        LocalAiGpuLoadEvidence? evidence = null;
        Exception? failure = null;
        try
        {
            evidence = await _probe.CaptureAsync(
                runtime.Snapshot.ProcessId.Value,
                gpuId,
                baseline,
                new LocalAiPaths(ctx.LocalDataDir),
                ct);
            string engineDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetDirectoryName(install.ExecutablePath)!));
            string cudaModule = Path.GetFullPath(evidence.CudaModulePath);
            if (!cudaModule.StartsWith(
                    engineDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "llama-server loaded CUDA from outside the managed runtime directory.");
            }
            long minimumDelta = Math.Max(512L * 1024 * 1024, plan.Model.Weights.SizeBytes / 2);
            if (!HasRequiredGpuLoadEvidence(evidence, minimumDelta))
            {
                throw new InvalidDataException(
                    "The selected model did not produce the required full-offload GPU memory evidence.");
            }
            ctx.LocalAiGpuLoadEvidence = evidence;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await VerifyLocalAiInferenceStep.ResetRouterAsync(runtime);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        LocalAiRuntimeSnapshot reset = await VerifyLocalAiInferenceStep.ResetRouterAsync(runtime);
        if (failure is not null)
            return StepResult.Fail($"Local AI GPU verification failed: {failure.Message}", failure);
        if (reset.State != LocalAiRuntimeState.Healthy ||
            reset.Ownership != LocalAiOwnership.CompanionManaged ||
            reset.ModelEvidence.State != LocalAiModelAvailabilityState.Verified)
        {
            return StepResult.Fail("llama-server could not return to on-demand loading after GPU verification.");
        }

        LocalAiResolvedInstall? restartedInstall;
        try
        {
            restartedInstall = await _installLoader(ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return StepResult.Fail(
                "llama-server restarted without a readable durable endpoint receipt.",
                ex);
        }
        if (restartedInstall?.Endpoint is null || restartedInstall.Endpoint != reset.Endpoint)
        {
            return StepResult.Fail(
                "llama-server restarted without committing its current endpoint receipt.");
        }
        ctx.LocalAiResolvedInstall = restartedInstall;

        string memoryEvidence = evidence!.LoadDeltaBytes is { } loadDeltaBytes
            ? $"{loadDeltaBytes} bytes of allocator-reported load growth"
            : $"{evidence.CudaModelBufferBytes.GetValueOrDefault()} bytes in CUDA model buffers";
        return StepResult.Ok(
            $"Verified {evidence.OffloadedLayers}/{evidence.TotalLayers} GPU layers and {memoryEvidence}; on-demand loading remains enabled.");
    }

    private static Task<LocalAiResolvedInstall?> LoadResolvedInstallAsync(
        SetupContext ctx,
        CancellationToken cancellationToken) =>
        new LocalAiManifestStore(new LocalAiPaths(ctx.LocalDataDir)).LoadAsync(cancellationToken);

    internal static bool HasRequiredGpuLoadEvidence(
        LocalAiGpuLoadEvidence evidence,
        long minimumDeltaBytes)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (minimumDeltaBytes <= 0 || evidence.OffloadedLayers != evidence.TotalLayers)
            return false;

        if (evidence.LoadDeltaBytes is { } loadDeltaBytes && loadDeltaBytes >= minimumDeltaBytes)
            return true;

        return evidence.CudaModelBufferBytes is { } cudaModelBufferBytes &&
            cudaModelBufferBytes >= minimumDeltaBytes;
    }
}
