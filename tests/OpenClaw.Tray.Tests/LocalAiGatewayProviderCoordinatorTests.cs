using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.Collections.Immutable;
using System.Text.Json;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiGatewayProviderCoordinatorTests
{
    [Fact]
    public async Task Quiesce_RemovesExactManagedRouteWhenNoFallbackExists()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(
            expected,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_AcceptsCliRedactedManagedApiKey()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string observed = RedactApiKey(LocalAiGatewayProviderDefinition.BuildProviderJson(install));
        var commands = new FakeWslCommandRunner(
            observed,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
    }

    [Fact]
    public async Task Quiesce_PreservesProviderDriftAndFailsClosed()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string drifted = LocalAiGatewayProviderDefinition.BuildProviderJson(install)
            .Replace("28765", "39876", StringComparison.Ordinal);
        var commands = new FakeWslCommandRunner(
            drifted,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Equal(drifted, commands.ProviderJson);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_Succeeds()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_ExistingProviderWithoutEndpoint_PreservesProviderAndFailsClosed()
    {
        LocalAiResolvedInstall runningInstall = Install(28_765);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(runningInstall);
        var commands = new FakeWslCommandRunner(
            provider,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(runningInstall));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(
            InstallWithoutEndpoint(28_765));

        Assert.False(result.Success);
        Assert.Contains("verified Local AI endpoint is required", result.Detail, StringComparison.Ordinal);
        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(runningInstall), commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quiesce_NoProviderWithUnqualifiedManagedModel_PreservesPrimaryAndFailsClosed(
        bool unknownCatalog)
    {
        LocalAiResolvedInstall valid = InstallWithoutEndpoint(28_765);
        LocalAiResolvedInstall tampered = valid with
        {
            Manifest = valid.Manifest with
            {
                ModelCatalogId = unknownCatalog ? "missing-model" : valid.Manifest.ModelCatalogId,
                ModelAlias = unknownCatalog ? valid.Manifest.ModelAlias : "tampered-model",
            },
        };
        string primary = $"llamacpp/{tampered.Manifest.ModelAlias}";
        var commands = new FakeWslCommandRunner(providerJson: null, primary);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(tampered);

        Assert.False(result.Success);
        Assert.Contains("qualified", result.Detail, StringComparison.Ordinal);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains("/bin/sh"));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_UnsetsManagedPrimary()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765);
        var commands = new FakeWslCommandRunner(
            providerJson: null,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call =>
            call.Contains("unset") &&
            call.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_RestoresFallbackPrimary()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(
            providerJson: null,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            PrimaryAfterApply = "openai/gpt-5",
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("/bin/sh"));
        Assert.DoesNotContain(commands.Calls, call =>
            call.Contains("unset") &&
            call.Contains(LocalAiGatewayProviderDefinition.ProviderPath));
    }

    [Fact]
    public async Task Publish_UsesVerifiedEndpointAndNonDefaultManagedDistro()
    {
        LocalAiResolvedInstall install = Install(28_766);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = RedactApiKey(expected),
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
        };
        var coordinator = CreateCoordinator(commands, "CustomGateway");

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.True(result.Success);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, install));
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), commands.PrimaryModel);
        IReadOnlyList<string> apply = Assert.Single(commands.Calls, call => call.Contains("/bin/sh"));
        string script = apply[^1];
        Assert.Contains("--dry-run", script, StringComparison.Ordinal);
        Assert.DoesNotContain('$', script);
        Assert.All(commands.Distros, distro => Assert.Equal("CustomGateway", distro));
    }

    [Fact]
    public async Task Publish_VerificationFailureRemovesExactJustWrittenProvider()
    {
        LocalAiResolvedInstall install = Install(28_768);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            FailedReadCalls = [2],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("was removed", result.Detail, StringComparison.Ordinal);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Publish_VerificationFailureSurfacesCleanupFailure()
    {
        LocalAiResolvedInstall install = Install(28_769);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            FailedReadCalls = [2, 3],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("Cleanup also failed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(expected, commands.ProviderJson);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_DoesNotMistakeWslFailureForMissingProvider()
    {
        LocalAiResolvedInstall install = Install(28_767);
        var commands = new FakeWslCommandRunner(providerJson: null) { FailReads = true };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Quiesce_RestoresFallbackBeforeRemovingProvider()
    {
        LocalAiResolvedInstall install = Install(28_770, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            PrimaryAfterApply = "openai/gpt-5",
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
        int restoreIndex = commands.Calls.FindIndex(call => call.Contains("/bin/sh"));
        int unsetIndex = commands.Calls.FindIndex(call =>
            call.Contains("unset") && call.Contains(LocalAiGatewayProviderDefinition.ProviderPath));
        Assert.True(restoreIndex >= 0 && unsetIndex > restoreIndex);
    }

    [Fact]
    public async Task Publish_PreservesUnexpectedPrimaryAndFailsClosed()
    {
        LocalAiResolvedInstall install = Install(28_771, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(providerJson: null, primaryModel: "anthropic/claude");
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("anthropic/claude", commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("/bin/sh"));
    }

    [Fact]
    public async Task Publish_MissingManagedGatewayOwner_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_772);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinatorForRegistry(commands, CreateRegistry());

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("No explicit setup-managed WSL gateway", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public async Task Publish_AmbiguousManagedGatewayOwners_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_773);
        var commands = new FakeWslCommandRunner(providerJson: null);
        GatewayRegistry registry = CreateRegistry(
            ManagedRecord("managed-a", "GatewayA"),
            ManagedRecord("managed-b", "GatewayB"));
        var coordinator = CreateCoordinatorForRegistry(commands, registry);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("Multiple explicit setup-managed WSL gateways", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public async Task Quiesce_RegistryUnavailable_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_774);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinatorForRegistry(commands, registry: null);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Contains("registry is unavailable", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public async Task Quiesce_OwnerDriftsAfterInspection_BlocksFirstMutation()
    {
        LocalAiResolvedInstall install = Install(28_775);
        GatewayRegistry registry = CreateRegistry(ManagedRecord("managed", "CustomGateway"));
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(provider, primary)
        {
            CommandObserved = callCount =>
            {
                if (callCount == 2)
                {
                    registry.Update(
                        "managed",
                        record => record with { SetupManagedDistroName = "UnexpectedGateway" });
                }
            },
        };
        var coordinator = CreateCoordinatorForRegistry(commands, registry);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Contains("owner changed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryModel);
        Assert.Equal(2, commands.Calls.Count);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    private static LocalAiGatewayProviderCoordinator CreateCoordinator(
        FakeWslCommandRunner commands,
        string distroName = "OpenClawGateway") =>
        CreateCoordinatorForRegistry(
            commands,
            CreateRegistry(ManagedRecord("managed", distroName)));

    private static LocalAiGatewayProviderCoordinator CreateCoordinatorForRegistry(
        FakeWslCommandRunner commands,
        GatewayRegistry? registry) =>
        new(
            commands,
            new LocalAiGatewayDistroResolver(registry),
            NullLogger.Instance);

    private static GatewayRegistry CreateRegistry(params GatewayRecord[] records)
    {
        var registry = new GatewayRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        foreach (GatewayRecord record in records)
            registry.AddOrUpdate(record);
        return registry;
    }

    private static GatewayRecord ManagedRecord(string id, string distroName) => new()
    {
        Id = id,
        Url = "ws://localhost:18789",
        IsLocal = true,
        SetupManagedDistroName = distroName,
    };

    private static LocalAiResolvedInstall Install(int port, string? fallbackModel = null)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            HardwareProfileId = "rtx-spark-n1x",
            RuntimeId = "b10488-cuda13-arm64",
            ModelCatalogId = LocalModelCatalog.Qwen35BModelId,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = "engines/llama-server.exe",
            RuntimeAssets = ImmutableArray<LocalAiAssetReceipt>.Empty,
            ModelPath =
                @"C:\hf-cache\models--owner--model\snapshots\0123456789abcdef0123456789abcdef01234567\model.gguf",
            ModelCacheRoot = @"C:\hf-cache",
            ModelId = "owner/model@0123456789abcdef0123456789abcdef01234567",
            ModelAlias = LocalModelCatalog.Qwen35BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "model.gguf",
                SourceUrl = "https://huggingface.co/owner/model/resolve/0123456789abcdef0123456789abcdef01234567/model.gguf",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            },
            RequestedPort = 0,
            Endpoint = endpoint.AbsoluteUri,
            GatewayFallbackModel = fallbackModel,
            ContextLength = LocalModelCatalog.NativeContextTokens,
        };
        return new(manifest, "llama-server.exe", "model.gguf", endpoint);
    }

    private static LocalAiResolvedInstall InstallWithoutEndpoint(
        int port,
        string? fallbackModel = null)
    {
        LocalAiResolvedInstall install = Install(port, fallbackModel);
        return install with
        {
            Manifest = install.Manifest with { Endpoint = null },
            Endpoint = null,
        };
    }

    private static string RedactApiKey(string value) => value.Replace(
        "\"api\":\"openai-completions\",\"apiKey\":\"llama-local\"",
        $"\"apiKey\":\"{LocalAiGatewayProviderDefinition.CliRedactedApiKey}\",\"api\":\"openai-completions\"",
        StringComparison.Ordinal);

    private sealed class FakeWslCommandRunner(string? providerJson, string? primaryModel = null) : IWslCommandRunner
    {
        public string? ProviderJson { get; private set; } = providerJson;
        public string? PrimaryModel { get; private set; } = primaryModel;
        public string? ProviderAfterApply { get; init; }
        public string? PrimaryAfterApply { get; init; }
        public bool FailReads { get; init; }
        public HashSet<int> FailedReadCalls { get; init; } = [];
        public Action<int>? CommandObserved { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<string> Distros { get; } = [];
        private int _readCalls;

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Distros.Add(name);
            Calls.Add(command.ToArray());
            CommandObserved?.Invoke(Calls.Count);
            bool providerRead = command.Contains("get") &&
                command.Contains(LocalAiGatewayProviderDefinition.ProviderPath);
            if (providerRead && (FailReads || FailedReadCalls.Contains(++_readCalls)))
                return Result(1, string.Empty, "wsl.exe failed");
            if (command.Contains("/bin/sh"))
            {
                if (ProviderAfterApply is not null)
                    ProviderJson = ProviderAfterApply;
                PrimaryModel = PrimaryAfterApply;
                return Result(PrimaryModel is null ? 1 : 0, string.Empty);
            }
            if (command.Contains("unset"))
            {
                if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath))
                    ProviderJson = null;
                if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath))
                    PrimaryModel = null;
                return Result(0, string.Empty);
            }
            if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath))
                return ProviderJson is null
                    ? Result(
                        1,
                        string.Empty,
                        $"Config path not found: {LocalAiGatewayProviderDefinition.ProviderPath}")
                    : Result(0, ProviderJson);
            if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath))
                return PrimaryModel is null
                    ? Result(
                        1,
                        string.Empty,
                        $"Config path not found: {LocalAiGatewayProviderDefinition.PrimaryModelPath}")
                    : Result(0, JsonSerializer.Serialize(PrimaryModel));
            return Result(1, string.Empty);
        }

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => Result(1, string.Empty);

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WslDistroInfo>>([]);

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        private static Task<WslCommandResult> Result(
            int exitCode,
            string stdout,
            string stderr = "") =>
            Task.FromResult(new WslCommandResult(exitCode, stdout, stderr));
    }
}
