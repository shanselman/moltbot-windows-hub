using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

/// <summary>
/// A failing inference verification must report why llama-server failed, not just the HTTP status.
/// The root cause only exists in llama-server's own logs, which live outside the setup log directory.
/// </summary>
public sealed class LocalAiInferenceFailureDiagnosticsTests
{
    private const string ServerError = "model name=qwen3.6-27b-mtp-q4-k-m failed to load";

    private const string CudaFailureLog =
        """
        [54881] cmn  common_init_: warming up the model with an empty run - please wait ...
        [54881] CUDA error: shared object initialization failed
        [54881] D:\a\llama.cpp\llama.cpp\ggml\src\ggml-cuda\ggml-cuda.cu:107: CUDA error
        srv    operator(): instance name=qwen3.6-27b-mtp-q4-k-m exited with status -1073740791
        """;

    [Fact]
    public async Task ExecuteAsync_IncludesLlamaServerRootCauseAndLogDirectory()
    {
        using var temp = new TempDirectory("local-ai-inference-diagnostics-");
        var paths = new LocalAiPaths(temp.Path);
        WriteServerLogs(paths, CudaFailureLog);
        (SetupContext context, FakeRuntime runtime) = CreateScenario(temp.Path);
        var step = new VerifyLocalAiInferenceStep(() => new ThrowingInferenceClient(
            new LlamaServerInferenceException(
                $"llama-server inference returned HTTP 500 (InternalServerError): {ServerError}",
                500,
                ServerError)));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains(ServerError, result.Message, StringComparison.Ordinal);
        LocalAiFailureDetail detail = Assert.IsType<LocalAiFailureDetail>(result.Detail);
        Assert.Equal(paths.LogsDirectory, detail.LogDirectory);
        Assert.Contains(detail.Diagnostics, line => line.Contains("CUDA error", StringComparison.Ordinal));
        Assert.Contains(detail.Diagnostics, line => line.Contains("exited with status", StringComparison.Ordinal));
        Assert.Equal(1, runtime.RestartCalls);
    }

    /// <summary>
    /// Regression guard: the router reset restarts llama-server, which truncates or rotates the
    /// failing lines away. Diagnostics must be captured before the reset, not after.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReadsDiagnosticsBeforeRouterReset()
    {
        using var temp = new TempDirectory("local-ai-inference-reset-order-");
        var paths = new LocalAiPaths(temp.Path);
        WriteServerLogs(paths, CudaFailureLog);
        (SetupContext context, FakeRuntime runtime) = CreateScenario(temp.Path);
        runtime.OnRestart = () => WriteServerLogs(paths, "srv init: running without SSL");
        var step = new VerifyLocalAiInferenceStep(() => new ThrowingInferenceClient(
            new LlamaServerInferenceException("llama-server inference returned HTTP 500.", 500, null)));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, runtime.RestartCalls);
        LocalAiFailureDetail detail = Assert.IsType<LocalAiFailureDetail>(result.Detail);
        Assert.Contains(detail.Diagnostics, line => line.Contains("CUDA error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_StillFailsCleanlyWhenLlamaLogsAreMissing()
    {
        using var temp = new TempDirectory("local-ai-inference-no-logs-");
        (SetupContext context, _) = CreateScenario(temp.Path);
        var step = new VerifyLocalAiInferenceStep(() => new ThrowingInferenceClient(
            new LlamaServerInferenceException("llama-server inference returned HTTP 500.", 500, null)));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Contains("HTTP 500", result.Message, StringComparison.Ordinal);
        LocalAiFailureDetail detail = Assert.IsType<LocalAiFailureDetail>(result.Detail);
        Assert.Empty(detail.Diagnostics);
        Assert.Equal(new LocalAiPaths(temp.Path).LogsDirectory, detail.LogDirectory);
    }

    /// <summary>
    /// Security-boundary regression, end to end: a response body that is not llama-server's
    /// recognized <c>{"error": ...}</c> shape must never reach <see cref="StepResult.Message"/>,
    /// which the setup log and completion UI render verbatim. Drives the real
    /// <see cref="LlamaServerInferenceClient"/> (not a hand-built exception) so the assertion
    /// covers the actual HTTP response parsing path, not just the step's plumbing.
    /// <see cref="LocalAiFailureDetail.Diagnostics"/> and <see cref="LocalAiFailureDetail.LogDirectory"/>
    /// are sourced from local log files and app-controlled paths, never from the HTTP body, so this
    /// asserts they are unaffected (empty, and exactly the app-controlled directory) rather than
    /// re-proving sentinel absence in data the HTTP body cannot reach.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NeverSurfacesUnrecognizedResponseBodyInStepResultOrDetail()
    {
        const string sentinel = "SENTINEL-UNRECOGNIZED-BODY";
        using var temp = new TempDirectory("local-ai-inference-unrecognized-body-");
        (SetupContext context, _) = CreateScenario(temp.Path);
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(
                $$"""{"detail":"{{sentinel}}"}""",
                Encoding.UTF8,
                "application/json"),
        }));
        var step = new VerifyLocalAiInferenceStep(() => new LlamaServerInferenceClient(handler));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(sentinel, result.Message, StringComparison.Ordinal);
        LocalAiFailureDetail detail = Assert.IsType<LocalAiFailureDetail>(result.Detail);
        Assert.Empty(detail.Diagnostics);
        Assert.Equal(new LocalAiPaths(temp.Path).LogsDirectory, detail.LogDirectory);
    }

    private static void WriteServerLogs(LocalAiPaths paths, string content)
    {
        Directory.CreateDirectory(paths.LogsDirectory);
        File.WriteAllText(paths.StandardOutputLogPath, content);
        File.WriteAllText(paths.StandardErrorLogPath, string.Empty);
    }

    private static (SetupContext Context, FakeRuntime Runtime) CreateScenario(string localDataDirectory)
    {
        HostHardwareInfo hardware = CreateSparkHardware();
        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
        LocalModelInfo model = eligibility.Plan!.Model;
        Uri endpoint = new("http://127.0.0.1:31001/v1");
        var runtime = new FakeRuntime(CreateSnapshot(model, endpoint));
        SetupContext context = CreateContext(localDataDirectory);
        context.LocalAiEligibility = eligibility;
        context.LocalAiHardware = hardware;
        context.LocalAiResolvedInstall = CreateInstall(localDataDirectory, model, endpoint, eligibility.Plan.Profile.ContextTokens);
        context.LocalAiRuntime = runtime;
        return (context, runtime);
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
            dataDir: localDataDirectory,
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
        Uri endpoint,
        int contextTokens)
    {
        string root = Path.Combine(localDataDirectory, "LocalAI");
        string executable = Path.Combine(root, "engines", "llama-server", "b10488", "win-arm64", "llama-server.exe");
        string modelPath = Path.Combine(root, "models", model.Weights.RelativePath);
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
            ExecutablePath = Path.GetRelativePath(root, executable),
            RuntimeAssets = ImmutableArray.Create(receipt),
            ModelPath = Path.GetRelativePath(root, modelPath),
            ModelId = model.Id,
            ModelAlias = model.Id,
            ModelAsset = receipt with { FileName = Path.GetFileName(modelPath) },
            RequestedPort = 0,
            ContextLength = contextTokens,
        };
        return new LocalAiResolvedInstall(manifest, executable, modelPath, endpoint);
    }

    private static LocalAiRuntimeSnapshot CreateSnapshot(LocalModelInfo model, Uri endpoint) =>
        new(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            endpoint,
            "b10488",
            model.Id,
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Verified,
                DateTimeOffset.UtcNow,
                model.Weights.Sha256.Value,
                model.Weights.SizeBytes,
                null),
            ProcessId: 4242,
            ProcessStartedAtUtc: DateTimeOffset.UtcNow,
            Detail: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    private sealed class ThrowingInferenceClient(Exception failure) : ILlamaServerInferenceClient
    {
        public Task<LlamaServerInferenceVerification> VerifyAsync(
            Uri endpoint,
            string modelAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromException<LlamaServerInferenceVerification>(failure);

        public void Dispose()
        {
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class FakeRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot;
        public int RestartCalls { get; private set; }
        public Action? OnRestart { get; set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged;

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCalls++;
            OnRestart?.Invoke();
            StateChanged?.Invoke(this, new LocalAiRuntimeSnapshotChangedEventArgs(Snapshot));
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
