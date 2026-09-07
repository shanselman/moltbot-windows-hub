using System.Collections.Immutable;
using System.Runtime.InteropServices;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiGpuRestartEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GpuVerification_RefreshesDynamicEndpointBeforeWslVerification(
        bool durableReceiptUsesRestartedEndpoint)
    {
        using var temp = new TempDirectory("local-ai-gpu-restart-");
        HostHardwareInfo hardware = CreateSparkHardware();
        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
        LocalModelInfo model = eligibility.Plan!.Model;
        Uri originalEndpoint = new("http://127.0.0.1:31001/v1");
        Uri restartedEndpoint = new("http://127.0.0.1:31002/v1");
        LocalAiResolvedInstall originalInstall = CreateInstall(temp.Path, model, originalEndpoint);
        LocalAiResolvedInstall restartedInstall = CreateInstall(temp.Path, model, restartedEndpoint);
        var runtime = new FakeRuntime(
            CreateSnapshot(model, originalEndpoint, LocalAiModelAvailabilityState.Loaded),
            CreateSnapshot(model, restartedEndpoint, LocalAiModelAvailabilityState.Verified));
        var context = CreateContext(temp.Path);
        context.LocalAiEligibility = eligibility;
        context.LocalAiHardware = hardware;
        context.LocalAiGpuBaseline = hardware;
        context.LocalAiResolvedInstall = originalInstall;
        context.LocalAiRuntime = runtime;
        context.LocalAiInferenceVerification = new(model.Id, 8, 32, 1, 1);

        string cudaPath = Path.Combine(
            Path.GetDirectoryName(originalInstall.ExecutablePath)!,
            "ggml-cuda.dll");
        var step = new VerifyLocalAiGpuLoadStep(
            new FakeGpuProbe(new LocalAiGpuLoadEvidence(
                ProcessId: 4242,
                SelectedGpuId: "GPU-SPARK",
                CudaModulePath: cudaPath,
                OffloadedLayers: 42,
                TotalLayers: 42,
                TotalGpuVisibleBytes: 48L * 1024 * 1024 * 1024,
                FreeGpuVisibleBytesBeforeLoad: 40L * 1024 * 1024 * 1024,
                FreeGpuVisibleBytesAfterLoad: 16L * 1024 * 1024 * 1024,
                CudaModelBufferBytes: null)),
            (_, _) => Task.FromResult<LocalAiResolvedInstall?>(
                durableReceiptUsesRestartedEndpoint ? restartedInstall : originalInstall));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, runtime.RestartCalls);
        if (durableReceiptUsesRestartedEndpoint)
        {
            Assert.Equal(StepOutcome.Success, result.Outcome);
            Assert.Equal(restartedEndpoint, context.LocalAiResolvedInstall!.Endpoint);
        }
        else
        {
            Assert.Equal(StepOutcome.Failed, result.Outcome);
            Assert.Contains("current endpoint receipt", result.Message, StringComparison.Ordinal);
            Assert.Equal(originalEndpoint, context.LocalAiResolvedInstall!.Endpoint);
        }
    }

    private static SetupContext CreateContext(string localDataDirectory)
    {
        var config = new SetupConfig { LocalAi = new LocalAiConfig { Enabled = true } };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private static HostHardwareInfo CreateSparkHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                GpuVisibleMemoryBytes: 48L * 1024 * 1024 * 1024,
                FreeGpuVisibleMemoryBytes: 40L * 1024 * 1024 * 1024,
                DriverVersion: "616.00",
                CudaMajorVersion: 13,
                StableId: "GPU-SPARK"),
        ],
        VulkanAvailable: false);

    private static LocalAiResolvedInstall CreateInstall(
        string localDataDirectory,
        LocalModelInfo model,
        Uri endpoint)
    {
        string engineDirectory = Path.Combine(
            localDataDirectory,
            "LocalAI",
            "engines",
            "llama-server",
            "b10488",
            "win-arm64");
        string executable = Path.Combine(engineDirectory, "llama-server.exe");
        string modelPath = Path.Combine(localDataDirectory, "LocalAI", "models", model.Weights.RelativePath);
        var receipt = new LocalAiAssetReceipt
        {
            FileName = "artifact.bin",
            SourceUrl = "https://example.invalid/artifact.bin",
            SizeBytes = 1,
            Sha256 = new string('a', 64),
        };
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            RuntimeId = "llama-server-b10488-win-arm64-cuda13",
            ModelCatalogId = model.Id,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = Path.GetRelativePath(Path.Combine(localDataDirectory, "LocalAI"), executable),
            RuntimeAssets = ImmutableArray.Create(receipt),
            ModelPath = Path.GetRelativePath(Path.Combine(localDataDirectory, "LocalAI"), modelPath),
            ModelId = model.Id,
            ModelAlias = model.Id,
            ModelAsset = receipt with { FileName = Path.GetFileName(modelPath) },
            RequestedPort = 0,
            Endpoint = endpoint.AbsoluteUri,
            ContextLength = LocalModelCatalog.GetProfiles(model)[0].ContextTokens,
        };
        return new LocalAiResolvedInstall(manifest, executable, modelPath, endpoint);
    }

    private static LocalAiRuntimeSnapshot CreateSnapshot(
        LocalModelInfo model,
        Uri endpoint,
        LocalAiModelAvailabilityState state) =>
        new(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            endpoint,
            "b10488",
            model.Id,
            new LocalAiModelEvidence(
                state,
                DateTimeOffset.UtcNow,
                model.Weights.Sha256.Value,
                model.Weights.SizeBytes,
                state == LocalAiModelAvailabilityState.Loaded ? model.Id : null),
            ProcessId: 4242,
            ProcessStartedAtUtc: DateTimeOffset.UtcNow,
            Detail: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private sealed class FakeGpuProbe(LocalAiGpuLoadEvidence evidence) : ILocalAiGpuEvidenceProbe
    {
        public Task<LocalAiGpuLoadEvidence> CaptureAsync(
            int processId,
            string selectedGpuId,
            HostHardwareInfo baseline,
            LocalAiPaths paths,
            CancellationToken cancellationToken) =>
            Task.FromResult(evidence);
    }

    private sealed class FakeRuntime(
        LocalAiRuntimeSnapshot initial,
        LocalAiRuntimeSnapshot restarted) : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = initial;
        public int RestartCalls { get; private set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            Snapshot = restarted;
            StateChanged?.Invoke(this, new LocalAiRuntimeSnapshotChangedEventArgs(Snapshot));
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
