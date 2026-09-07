using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiGatewayUninstallTests
{
    [Fact]
    public async Task FreshProcessUninstall_RemovesExactManagedProviderAndPrimary()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
        Assert.Contains(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_AcceptsCliRedactedManagedApiKey()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install).Replace(
            "\"api\":\"openai-completions\",\"apiKey\":\"llama-local\"",
            $"\"apiKey\":\"{LocalAiGatewayProviderDefinition.CliRedactedApiKey}\",\"api\":\"openai-completions\"",
            StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
    }

    [Fact]
    public async Task FreshProcessUninstall_RestoresRecordedFallbackPrimary()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Equal(JsonSerializer.Serialize("openai/gpt-5"), commands.PrimaryJson);
        Assert.Contains(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_PRIMARY_RESTORED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_PreservesDriftAndFailsClosed()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string expectedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string driftedProvider = expectedProvider.Replace(
            "http://127.0.0.1:28765/v1",
            "http://127.0.0.1:39876/v1",
            StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(driftedProvider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None));

        Assert.Contains("preserving", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(driftedProvider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.DoesNotContain(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_PreservesStateWhenSnapshotFails()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary) { FailCapture = true };
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await Assert.ThrowsAsync<IOException>(() =>
            new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None));

        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.DoesNotContain(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_ReplacesExactManagedProviderAfterAutomaticPortChanges()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        LocalAiInstallManifest replacementManifest = original.Manifest with
        {
            Endpoint = "http://127.0.0.1:39876/v1",
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.True(context.LocalAiRecoveryProviderTransition);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            context.LocalAiResolvedInstall));
        Assert.Equal(primary, commands.PrimaryJson);
    }

    [Fact]
    public async Task Recovery_PreservesProviderThatMatchesNeitherEndpoint()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        string driftedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original)
            .Replace(
                "http://127.0.0.1:28765/v1",
                "http://127.0.0.1:45555/v1",
                StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(driftedProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        LocalAiInstallManifest replacementManifest = original.Manifest with
        {
            Endpoint = "http://127.0.0.1:39876/v1",
        };
        context.LocalAiResolvedInstall = new LocalAiResolvedInstall(
            replacementManifest,
            original.ExecutablePath,
            original.ModelPath,
            new Uri(replacementManifest.Endpoint!));

        StepResult result = await new ConfigureLocalAiGatewayStep()
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Equal(driftedProvider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
    }

    [Fact]
    public async Task Recovery_RollbackRestoresOriginalProviderAndReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        LocalAiInstallManifest replacementManifest = original.Manifest with
        {
            Endpoint = "http://127.0.0.1:39876/v1",
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_FailedProviderSwitchRestoresOriginalReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary)
        {
            FailConfiguredBatchOnce = true,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        LocalAiInstallManifest replacementManifest = original.Manifest with
        {
            Endpoint = "http://127.0.0.1:39876/v1",
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    private static SetupContext CreateContext(string localDataDirectory, ICommandRunner commands)
    {
        var config = new SetupConfig();
        var logger = new SetupLogger(filePath: null);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            commands,
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private static SetupContext CreateRecoveryContext(
        string localDataDirectory,
        ICommandRunner commands)
    {
        SetupContext context = CreateContext(localDataDirectory, commands);
        context.Config.LocalAi.Enabled = true;
        context.Config.LocalAiRecoveryGatewayId = "gateway-id";
        context.DistroName = "OpenClawGateway";
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        return context;
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

    private static async Task<LocalAiResolvedInstall> SaveManifestAsync(
        string localDataDirectory,
        string? fallbackModel = null)
    {
        var paths = new LocalAiPaths(localDataDirectory);
        const string revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            HardwareProfileId = "rtx-spark-n1x",
            RuntimeId = "b10488-cuda13-arm64",
            ModelCatalogId = LocalModelCatalog.Qwen35BModelId,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = Path.Combine("engines", "llama-b10488", "llama-server.exe"),
            RuntimeAssets =
            [
                new LocalAiAssetReceipt
                {
                    FileName = "llama-runtime.zip",
                    SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/llama-runtime.zip",
                    SizeBytes = 1,
                    Sha256 = new string('a', 64),
                },
            ],
            ModelPath = Path.Combine("models", "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
            ModelId = $"unsloth/Qwen3.6-35B-A3B-MTP-GGUF@{revision}",
            ModelAlias = LocalModelCatalog.Qwen35BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                SourceUrl = $"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/{revision}/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf?download=true",
                SizeBytes = 1,
                Sha256 = new string('b', 64),
            },
            RequestedPort = 0,
            Endpoint = "http://127.0.0.1:28765/v1",
            GatewayFallbackModel = fallbackModel,
            ContextLength = LocalModelCatalog.NativeContextTokens,
        };
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(manifest);
        return (await store.LoadAsync())!;
    }

    private sealed class GatewayStateCommandRunner(
        string? providerJson,
        string? primaryJson) : ICommandRunner
    {
        private const string ProviderMarker = "OPENCLAW_LOCAL_AI_PROVIDER_B64=";
        private const string PrimaryMarker = "OPENCLAW_LOCAL_AI_PRIMARY_B64=";

        public string? ProviderJson { get; private set; } = providerJson;
        public string? PrimaryJson { get; private set; } = primaryJson;
        public bool FailCapture { get; init; }
        public bool FailConfiguredBatchOnce { get; set; }
        public List<string> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) => throw new NotSupportedException();

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            ct.ThrowIfCancellationRequested();
            WslCalls.Add(command);
            if (environment is not null && environment.Count == 1)
            {
                if (FailConfiguredBatchOnce &&
                    command.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
                {
                    FailConfiguredBatchOnce = false;
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway configuration failed",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                string encoded = Assert.Single(environment).Value;
                string batch = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                using JsonDocument document = JsonDocument.Parse(batch);
                foreach (JsonElement operation in document.RootElement.EnumerateArray())
                {
                    string path = operation.GetProperty("path").GetString()!;
                    string value = operation.GetProperty("value").GetRawText();
                    if (path == LocalAiGatewayProviderDefinition.ProviderPath)
                        ProviderJson = value;
                    else if (path == LocalAiGatewayProviderDefinition.PrimaryModelPath)
                        PrimaryJson = value;
                }
                string marker = command.Contains(
                    "LOCAL_AI_GATEWAY_CONFIGURED",
                    StringComparison.Ordinal)
                    ? "LOCAL_AI_GATEWAY_CONFIGURED"
                    : "LOCAL_AI_PRIMARY_RESTORED";
                return Task.FromResult(new CommandResult(
                    0,
                    marker,
                    "",
                    TimeSpan.Zero,
                    TimedOut: false));
            }
            if (command.Contains("openclaw config unset", StringComparison.Ordinal))
            {
                if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath, StringComparison.Ordinal))
                    PrimaryJson = null;
                if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath, StringComparison.Ordinal))
                    ProviderJson = null;
                return Task.FromResult(new CommandResult(
                    0,
                    "LOCAL_AI_GATEWAY_UNSET",
                    "",
                    TimeSpan.Zero,
                    TimedOut: false));
            }
            if (FailCapture)
            {
                return Task.FromResult(new CommandResult(
                    1,
                    "",
                    "openclaw config get failed",
                    TimeSpan.Zero,
                    TimedOut: false));
            }

            string stdout =
                ProviderMarker + EncodeOrMissing(ProviderJson) + Environment.NewLine +
                PrimaryMarker + EncodeOrMissing(PrimaryJson) + Environment.NewLine;
            return Task.FromResult(new CommandResult(0, stdout, "", TimeSpan.Zero, TimedOut: false));
        }

        private static string EncodeOrMissing(string? value) => value is null
            ? "MISSING"
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
}
