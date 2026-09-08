using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Every test in this class runs against an isolated, per-test Hugging Face hub cache.
/// Without the class-level override the manifest paths would resolve into the developer's
/// real <c>~/.cache/huggingface/hub</c> -- which on a machine that has actually run Local
/// AI holds the very repository these manifests name.
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class LocalAiPortLifecycleTests : IDisposable
{
    private readonly TempDirectory _hubCache = new("local-ai-hub-cache-");
    private readonly EnvironmentScope _hubCacheScope;

    public LocalAiPortLifecycleTests() =>
        _hubCacheScope = new EnvironmentScope("HF_HUB_CACHE", _hubCache.Path);

    public void Dispose()
    {
        _hubCacheScope.Dispose();
        _hubCache.Dispose();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(80, false)]
    [InlineData(65_535, true)]
    [InlineData(65_536, false)]
    public void PortPolicy_IsConsistent(int port, bool accepted)
    {
        Assert.Equal(accepted, LocalAiPortPolicy.TryValidate(port, out _));
    }

    [Fact]
    public void LegacyRouterProbe_RemainsSourceCompatible()
    {
        using var client = new LlamaServerClient();
#pragma warning disable CS0618
        Func<Uri, string, string, CancellationToken, Task<LlamaServerRouterProbeResult>> legacyProbe =
            client.ProbeRouterAsync;
#pragma warning restore CS0618

        Assert.NotNull(legacyProbe);
    }

    [Fact]
    public async Task Manifest_RoundTripsValidatedGatewayFallbackModel()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await store.SaveAsync(ValidManifest() with { GatewayFallbackModel = "openai/gpt-5" });

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        Assert.Equal("openai/gpt-5", saved.Manifest.GatewayFallbackModel);
    }

    [SymbolicLinkFact]
    public async Task Manifest_RoundTripsStandardHubSnapshotSymlink()
    {
        using var temp = new TempDirectory("local-ai-manifest-link-");
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        using var env = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        LocalAiInstallManifest manifest = ValidManifest(cacheRoot);
        string snapshotDirectory = Path.GetDirectoryName(manifest.ModelPath)!;
        string repositoryDirectory = Directory.GetParent(
            Directory.GetParent(snapshotDirectory)!.FullName)!.FullName;
        string blobsDirectory = Path.Combine(repositoryDirectory, "blobs");
        string blobPath = Path.Combine(blobsDirectory, manifest.ModelAsset.Sha256);
        Directory.CreateDirectory(blobsDirectory);
        Directory.CreateDirectory(snapshotDirectory);
        await File.WriteAllTextAsync(blobPath, "verified model");
        SymbolicLinkSupport.CreateSymbolicLink(
            manifest.ModelPath,
            Path.GetRelativePath(snapshotDirectory, blobPath));

        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(manifest);
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        Assert.Equal(manifest.ModelPath, saved.ModelPath);
    }

    [Fact]
    public async Task Manifest_AcceptsAndIgnoresLegacyHardwareProfileId()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { HardwareProfileId = "retired-profile-id" });
        JsonObject legacyJson = (JsonNode.Parse(await File.ReadAllTextAsync(paths.ManifestPath)) as JsonObject)!;
        legacyJson.Remove("keyCachePrecision");
        legacyJson.Remove("valueCachePrecision");
        legacyJson.Remove("draftKeyCachePrecision");
        legacyJson.Remove("draftValueCachePrecision");
        await File.WriteAllTextAsync(paths.ManifestPath, legacyJson.ToJsonString());

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal("retired-profile-id", saved.Manifest.HardwareProfileId);
        Assert.Equal(KvCachePrecision.F16, saved.Manifest.KeyCachePrecision);
        Assert.Contains("cache-type-k = f16", launch.PresetContent);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, launch.ModelAlias);
    }

    [Fact]
    public async Task RouterPreset_BoundsOmittedGenerationAtGatewayMaximum()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { Endpoint = "http://127.0.0.1:28765/v1" });
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);
        using JsonDocument provider = JsonDocument.Parse(
            LocalAiGatewayProviderDefinition.BuildProviderJson(saved));
        int gatewayMaximum = provider.RootElement
            .GetProperty("models")[0]
            .GetProperty("maxTokens")
            .GetInt32();

        Assert.Equal(LocalAiGatewayProviderDefinition.MaximumOutputTokens, gatewayMaximum);
        Assert.Contains(
            $"n-predict = {gatewayMaximum}",
            launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("ctx-size = 262144", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k-draft = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v-draft = q8_0", launch.PresetContent.Split(Environment.NewLine));
    }

    [Fact]
    public async Task Manifest_OmitsLegacyHardwareProfileIdFromNewWrites()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(ValidManifest());

        string json = await File.ReadAllTextAsync(paths.ManifestPath);

        Assert.DoesNotContain("hardwareProfileId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"keyCachePrecision\": \"q8_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"draftValueCachePrecision\": \"q8_0\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_RejectsRuntimeArchitectureMismatchWithoutHardwareProfile()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { Architecture = "x64" });
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LlamaServerRouterConfiguration.Build(paths, saved));

        Assert.Contains("architecture and runtime", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("llamacpp/other-model")]
    [InlineData("missing-provider-separator")]
    [InlineData("provider/model/extra")]
    public async Task Manifest_RejectsUnsafeGatewayFallbackModel(string fallbackModel)
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { GatewayFallbackModel = fallbackModel }));
    }

    [Fact]
    public async Task AutomaticPort_IsBoundByChildAndPersistedOnlyAfterOwnedHealth()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        var client = new FakeClient(events);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(28_765, snapshot.Endpoint.Port);
        Assert.Equal("0", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        Assert.Equal(["quiesce", "start", "probe:28765", "publish:28765"], events);
        Assert.Equal([28_765], client.ProbedPorts);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Equal(0, saved!.Manifest.RequestedPort);
        Assert.Equal(28_765, saved.Endpoint!.Port);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Unknown, true)]
    [InlineData(LocalAiModelAvailabilityState.Loaded, false)]
    public async Task AutomaticPort_DoesNotPersistOrPublishWithoutReadyModelEvidence(
        LocalAiModelAvailabilityState modelState,
        bool pathMatches)
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_774);
        var client = new FakeClient(events, (expectedModelPath, _) => new(
            true,
            modelState,
            pathMatches ? expectedModelPath : expectedModelPath + ".other",
            "The managed model is not ready."));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            lifecycle,
            startupTimeout: TimeSpan.FromMilliseconds(2));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.DoesNotContain(events, value => value.StartsWith("publish:", StringComparison.Ordinal));
        Assert.True(host.Process!.StopCount > 0);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task Refresh_UpdatesPublicationWhenManagedModelReadinessChanges()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_773);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                true,
                LocalAiModelAvailabilityState.Loaded,
                expectedModelPath + ".other",
                "The managed model path changed.")
            : new(
                true,
                LocalAiModelAvailabilityState.Verified,
                expectedModelPath,
                "The managed model is ready."));
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Starting, refreshed.State);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, refreshed.ModelEvidence.State);
        Assert.Equal(["probe:28773", "quiesce"], events);
        events.Clear();

        LocalAiRuntimeSnapshot recovered = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(["probe:28773", "publish:28773"], events);
    }

    [Theory]
    [InlineData(RefreshOwnershipLoss.Incomplete, LocalAiRuntimeState.Conflict)]
    [InlineData(RefreshOwnershipLoss.Conflict, LocalAiRuntimeState.Conflict)]
    [InlineData(RefreshOwnershipLoss.MissingEndpoint, LocalAiRuntimeState.Starting)]
    public async Task Refresh_QuiescesPublishedRouteWhenEndpointOwnershipIsLost(
        RefreshOwnershipLoss ownershipLoss,
        LocalAiRuntimeState expectedState)
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_775);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        switch (ownershipLoss)
        {
            case RefreshOwnershipLoss.Incomplete:
                platform.Ipv4Complete = false;
                break;
            case RefreshOwnershipLoss.Conflict:
                platform.Listeners[0] = platform.Listeners[0] with { Address = IPAddress.Any };
                break;
            case RefreshOwnershipLoss.MissingEndpoint:
                platform.Listeners.Clear();
                break;
            default:
                throw new InvalidOperationException("Unknown ownership-loss case.");
        }

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(expectedState, refreshed.State);
        Assert.Equal(["quiesce"], events);
        Assert.False(host.Process!.HasExited);
    }

    [Fact]
    public async Task Refresh_QuiesceFailureStopsUnverifiedManagedProcess()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_776);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 1
            ? ReadyProbe(expectedModelPath)
            : new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready."));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        lifecycle.FailQuiesce = true;

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, refreshed.State);
        Assert.Equal(LocalAiOwnership.None, refreshed.Ownership);
        Assert.Null(refreshed.ProcessId);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(["probe:28776", "quiesce", "stop"], events);
    }

    [Fact]
    public async Task Refresh_QuiesceExceptionStopsManagedProcessBeforePropagating()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_779);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Listeners.Clear();
        lifecycle.QuiesceException = new IOException("route withdrawal failed");

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RefreshAsync());

        Assert.Equal("route withdrawal failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.Null(runtime.Snapshot.ProcessId);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(["quiesce", "stop"], events);
    }

    [Fact]
    public async Task Refresh_RecoveryRebindsFreshlyVerifiedEndpointBeforePublishing()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_777);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready.")
            : ReadyProbe(expectedModelPath));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        LocalAiRuntimeSnapshot unavailable = await runtime.RefreshAsync();
        Assert.Equal(LocalAiRuntimeState.Starting, unavailable.State);

        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:29999/v1" });
        events.Clear();

        LocalAiRuntimeSnapshot recovered = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(28_777, recovered.Endpoint.Port);
        Assert.Equal(["probe:28777", "publish:28777"], events);
        LocalAiResolvedInstall rebound = (await store.LoadAsync())!;
        Assert.Equal(28_777, rebound.Endpoint!.Port);
    }

    [Fact]
    public async Task Refresh_RecoveryPublishFailureRemainsFailedAndQuiesced()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_778);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready.")
            : ReadyProbe(expectedModelPath));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        LocalAiRuntimeSnapshot unavailable = await runtime.RefreshAsync();
        Assert.Equal(LocalAiRuntimeState.Starting, unavailable.State);
        events.Clear();
        lifecycle.FailPublish = true;

        LocalAiRuntimeSnapshot failed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, failed.Ownership);
        Assert.False(host.Process!.HasExited);
        Assert.Equal(["probe:28778", "publish:28778"], events);
    }

    [Fact]
    public async Task AutomaticPort_NeverProbesListenerWithoutMatchingProcessStartTime()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var platform = new FakePlatform();
        var host = new FakeProcessHost(
            platform,
            [],
            selectedPort: 28_766,
            listenerStartOffset: TimeSpan.FromMinutes(-1));
        var client = new FakeClient([]);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle([]));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Empty(client.ProbedPorts);
        Assert.True(host.Process!.StopCount > 0);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task FixedPortConflict_QuiescesEndpointConsumerBeforeReturning()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        const int fixedPort = 28_770;
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with
        {
            RequestedPort = fixedPort,
            Endpoint = $"http://127.0.0.1:{fixedPort}/v1",
        });
        var events = new List<string>();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            fixedPort,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: fixedPort);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(["quiesce"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task PreparationFailure_QuiescesEndpointConsumerBeforeReturning()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        LocalAiResolvedInstall install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        File.Delete(install.ExecutablePath);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_772);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(["quiesce"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task AutomaticPort_RejectsWildcardChildListenerWithoutProbing()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(
            platform,
            events,
            selectedPort: 28_771,
            listenerAddress: IPAddress.Any);
        var client = new FakeClient(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Empty(client.ProbedPorts);
        Assert.Equal(["quiesce", "start", "stop"], events);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task Stop_QuiescesEndpointConsumerBeforeListenerDisappears()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        await runtime.EnsureStartedAsync();
        events.Clear();

        LocalAiRuntimeSnapshot stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(["quiesce", "stop"], events);
    }

    [Fact]
    public async Task PublishFailure_StopsChildAndLeavesEndpointConsumerQuiesced()
    {
        using var temp = new TempDirectory("local-ai-port-");
        (LocalAiPaths paths, IDisposable hubCacheScope) = await PrepareInstallAsync(temp);
        using var _hubCacheScope = hubCacheScope;
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        var lifecycle = new FakeLifecycle(events) { FailPublish = true };
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(1, host.Process!.StopCount);
        Assert.Equal(["quiesce", "start", "probe:28767", "publish:28767", "stop"], events);

        // The endpoint receipt is already durable but the provider is still absent.
        // A later tray start must safely allocate again and complete publication.
        await runtime.DisposeAsync();
        var retryPlatform = new FakePlatform();
        var retryHost = new FakeProcessHost(retryPlatform, [], selectedPort: 28_768);
        await using var retry = CreateRuntime(
            paths,
            retryHost,
            retryPlatform,
            new FakeClient([]),
            new FakeLifecycle([]));
        LocalAiRuntimeSnapshot recovered = await retry.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(28_768, recovered.Endpoint.Port);
    }

    private static LlamaServerRuntimeService CreateRuntime(
        LocalAiPaths paths,
        FakeProcessHost host,
        FakePlatform platform,
        FakeClient client,
        ILocalAiEndpointLifecycle lifecycle,
        TimeSpan? startupTimeout = null) => new(
            new LlamaServerRuntimeOptions
            {
                Paths = paths,
                EndpointLifecycle = lifecycle,
                HealthPollInterval = TimeSpan.FromMilliseconds(1),
                StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(1),
                RestartDelay = TimeSpan.Zero,
            },
            NullLogger.Instance,
            host,
            platform,
            client);

    /// <summary>
    /// Prepares a managed install whose model lives in an isolated, per-test Hugging
    /// Face hub cache directory (via a temporary <c>HF_HUB_CACHE</c> override), since
    /// the model path is no longer contained within the Local AI root. The returned
    /// scope must be kept alive (e.g. via <c>using</c>) for as long as the manifest may
    /// still be reloaded and revalidated.
    /// </summary>
    private static async Task<(LocalAiPaths Paths, IDisposable HubCacheScope)> PrepareInstallAsync(TempDirectory temp)
    {
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        var hubCacheScope = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        try
        {
            var paths = new LocalAiPaths(temp.Path);
            LocalAiInstallManifest manifest = ValidManifest(cacheRoot);
            string executable = paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
            string model = manifest.ModelPath;
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            await File.WriteAllTextAsync(executable, "test executable");
            await using (var stream = new FileStream(model, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(manifest.ModelAsset.SizeBytes);
            await new LocalAiManifestStore(paths).SaveAsync(manifest);
            return (paths, hubCacheScope);
        }
        catch
        {
            hubCacheScope.Dispose();
            throw;
        }
    }

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string name)
    {
        int index = Array.IndexOf(arguments.ToArray(), name);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private static LlamaServerRouterProbeResult ReadyProbe(string expectedModelPath) => new(
        true,
        LocalAiModelAvailabilityState.Verified,
        expectedModelPath,
        "The managed model is ready.");

    /// <summary>
    /// A managed Qwen3.5 9B install predates the profile-aware catalog and is no
    /// longer offered for new installs. Upgrading must not strand it: its own
    /// pinned receipt has to keep resolving and launching unchanged.
    /// </summary>
    [Fact]
    public async Task Router_LaunchesRetiredQwen9BInstallAfterUpgrade()
    {
        using var temp = new TempDirectory("local-ai-legacy-model-");
        using var hubCacheScope = LegacyHubCacheScope(temp, out LocalAiPaths paths);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LegacyQwen9BManifest());
        JsonObject legacyJson = (JsonNode.Parse(await File.ReadAllTextAsync(paths.ManifestPath)) as JsonObject)!;
        legacyJson.Remove("keyCachePrecision");
        legacyJson.Remove("valueCachePrecision");
        legacyJson.Remove("draftKeyCachePrecision");
        legacyJson.Remove("draftValueCachePrecision");
        await File.WriteAllTextAsync(paths.ManifestPath, legacyJson.ToJsonString());

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal(LocalModelCatalog.Qwen9BModelId, launch.ModelAlias);
        Assert.Contains("ctx-size = 262144", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k = f16", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v = f16", launch.PresetContent.Split(Environment.NewLine));
        Assert.Equal(
            $"llamacpp/{LocalModelCatalog.Qwen9BModelId}",
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(saved));
    }

    /// <summary>
    /// The retired entry is a compatibility shim only. It must never be offered,
    /// recommended, or selectable for a new install.
    /// </summary>
    [Fact]
    public void Catalog_DoesNotOfferRetiredQwen9BForNewInstalls()
    {
        Assert.DoesNotContain(
            LocalModelCatalog.Models,
            model => string.Equals(model.Id, LocalModelCatalog.Qwen9BModelId, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            LocalModelCatalog.ExplicitAlternatives,
            model => string.Equals(model.Id, LocalModelCatalog.Qwen9BModelId, StringComparison.OrdinalIgnoreCase));
        Assert.Null(LocalModelCatalog.Find(LocalModelCatalog.Qwen9BModelId));
        Assert.NotNull(LocalModelCatalog.FindInstalled(LocalModelCatalog.Qwen9BModelId));
        Assert.True(LocalModelCatalog.IsLegacy(LocalModelCatalog.Qwen9BModelId));
        Assert.False(LocalModelCatalog.IsLegacy(LocalModelCatalog.Qwen38_27BModelId));
    }

    /// <summary>
    /// Compatibility must not become silent remapping: a retired model receipt
    /// that claims a context or KV profile it could never have been installed
    /// with still has to fail receipt validation.
    /// </summary>
    [Fact]
    public async Task Router_RejectsRetiredQwen9BReceiptWithUnsupportedProfile()
    {
        using var temp = new TempDirectory("local-ai-legacy-profile-");
        using var hubCacheScope = LegacyHubCacheScope(temp, out LocalAiPaths paths);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LegacyQwen9BManifest() with
        {
            KeyCachePrecision = KvCachePrecision.Q8_0,
            ValueCachePrecision = KvCachePrecision.Q8_0,
            DraftKeyCachePrecision = KvCachePrecision.Q8_0,
            DraftValueCachePrecision = KvCachePrecision.Q8_0,
        });

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LlamaServerRouterConfiguration.Build(paths, saved));
        Assert.Contains("qualified catalog profile", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stages an isolated, per-test Hugging Face hub cache containing the retired
    /// Qwen3.5 9B weights file, so its legacy receipt resolves under the schema-v4
    /// absolute <c>ModelPath</c> layout. The returned scope must be kept alive
    /// (e.g. via <c>using</c>) for as long as the manifest may still be reloaded
    /// and revalidated.
    /// </summary>
    private static IDisposable LegacyHubCacheScope(TempDirectory temp, out LocalAiPaths paths)
    {
        string cacheRoot = Path.Combine(temp.Path, "hf-cache");
        var hubCacheScope = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        try
        {
            Assert.True(
                HuggingFaceHubCache.TryGetSnapshotPaths(
                    cacheRoot,
                    "unsloth/Qwen3.5-9B-MTP-GGUF",
                    "9716a636ee4bddc3fed678220b7a33dd2a4160ae",
                    "Qwen3.5-9B-Q4_K_M.gguf",
                    out string legacyModelPath,
                    out _,
                    out string pathError),
                pathError);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
            using (var stream = new FileStream(legacyModelPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(5_868_826_976);
            paths = new LocalAiPaths(temp.Path);
            return hubCacheScope;
        }
        catch
        {
            hubCacheScope.Dispose();
            throw;
        }
    }

    private static LocalAiInstallManifest LegacyQwen9BManifest()
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.Arm64)!;
        Assert.True(
            HuggingFaceHubCache.TryGetSnapshotPaths(
                HuggingFaceHubCache.ResolveCacheRoot(),
                "unsloth/Qwen3.5-9B-MTP-GGUF",
                "9716a636ee4bddc3fed678220b7a33dd2a4160ae",
                "Qwen3.5-9B-Q4_K_M.gguf",
                out string legacyModelPath,
                out _,
                out string pathError),
            pathError);
        return ValidManifest() with
        {
            ModelCatalogId = LocalModelCatalog.Qwen9BModelId,
            ModelPath = legacyModelPath,
            ModelId = "unsloth/Qwen3.5-9B-MTP-GGUF@9716a636ee4bddc3fed678220b7a33dd2a4160ae",
            ModelAlias = LocalModelCatalog.Qwen9BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "Qwen3.5-9B-Q4_K_M.gguf",
                SourceUrl = "https://huggingface.co/unsloth/Qwen3.5-9B-MTP-GGUF/resolve/" +
                    "9716a636ee4bddc3fed678220b7a33dd2a4160ae/Qwen3.5-9B-Q4_K_M.gguf?download=true",
                SizeBytes = 5_868_826_976,
                Sha256 = "e8dd94817e95d6c0939102049d068418269978377b13616c4726235e232841fe",
            },
            RuntimeAssets = runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
            ContextLength = LocalModelCatalog.NativeContextTokens,
            KeyCachePrecision = KvCachePrecision.F16,
            ValueCachePrecision = KvCachePrecision.F16,
            DraftKeyCachePrecision = KvCachePrecision.F16,
            DraftValueCachePrecision = KvCachePrecision.F16,
        };
    }

    private static LocalAiInstallManifest ValidManifest() => ValidManifest(HuggingFaceHubCache.ResolveCacheRoot());

    private static LocalAiInstallManifest ValidManifest(string cacheRoot)
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.Arm64)!;
        const string repositoryId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF";
        const string revisionSha = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
        const string fileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf";
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            repositoryId,
            revisionSha,
            fileName,
            out string modelPath,
            out _,
            out string pathError), pathError);
        return new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = "arm64",
            RuntimeId = runtime.Id,
            ModelCatalogId = "qwen3.6-35b-a3b-mtp-q4-k-m",
            SelectedGpuId = "GPU-01234567-89ab-cdef-0123-456789abcdef",
            ExecutablePath = Path.Combine(
                "engines",
                $"llama-{LlamaRuntimeCatalog.ReleaseTag}",
                LlamaRuntimeCatalog.ServerExecutableName),
            RuntimeAssets = runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
            ModelPath = modelPath,
            ModelCacheRoot = cacheRoot,
            ModelId = $"{repositoryId}@{revisionSha}",
            ModelAlias = "qwen3.6-35b-a3b-mtp-q4-k-m",
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = fileName,
                SourceUrl = "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/5bc3e238d916f48a861bac2f8a1990a0e9b7e98d/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf?download=true",
                SizeBytes = 22_663_387_424,
                Sha256 = "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b",
            },
            RequestedPort = 0,
            Endpoint = null,
            ContextLength = 262_144,
            KeyCachePrecision = KvCachePrecision.Q8_0,
            ValueCachePrecision = KvCachePrecision.Q8_0,
            DraftKeyCachePrecision = KvCachePrecision.Q8_0,
            DraftValueCachePrecision = KvCachePrecision.Q8_0,
            InstalledAtUtc = DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
        };
    }

    private sealed class FakePlatform : ILlamaServerRuntimePlatform
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        public List<WindowsTcpListenerInfo> Listeners { get; } = [];
        public bool Ipv4Complete { get; set; } = true;

        public WindowsTcpListenerSnapshotResult CaptureListeners() =>
            new([.. Listeners], Ipv4Complete, Ipv6Complete: true);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessHost(
        FakePlatform platform,
        List<string> events,
        int selectedPort,
        TimeSpan? listenerStartOffset = null,
        IPAddress? listenerAddress = null) : ILocalAiManagedProcessHost
    {
        public LocalAiProcessStartSpec? LastSpec { get; private set; }
        public FakeProcess? Process { get; private set; }

        public Task<ILocalAiManagedProcess> StartProcessAsync(
            LocalAiProcessStartSpec spec,
            Action<LocalAiManagedProcessExit> exited,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start");
            LastSpec = spec;
            Process = new FakeProcess(4201, platform.UtcNow, platform, events);
            platform.Listeners.Add(new WindowsTcpListenerInfo(
                listenerAddress ?? IPAddress.Loopback,
                selectedPort,
                Process.ProcessId,
                "llama-server",
                @"C:\managed\llama-server.exe",
                (Process.StartedAtUtc + (listenerStartOffset ?? TimeSpan.Zero)).UtcDateTime));
            return Task.FromResult<ILocalAiManagedProcess>(Process);
        }
    }

    private sealed class FakeProcess(
        int processId,
        DateTimeOffset startedAtUtc,
        FakePlatform platform,
        List<string> events) : ILocalAiManagedProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public bool HasExited { get; private set; }
        public int StopCount { get; private set; }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("stop");
            StopCount++;
            HasExited = true;
            platform.Listeners.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient(
        List<string> events,
        Func<string, int, LlamaServerRouterProbeResult>? probeFactory = null) : ILlamaServerClient
    {
        private int _probeCount;

        public List<int> ProbedPorts { get; } = [];

        public Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(
            Uri endpoint,
            string modelAlias,
            string expectedModelPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbedPorts.Add(endpoint.Port);
            events.Add($"probe:{endpoint.Port}");
            int probeNumber = ++_probeCount;
            return Task.FromResult(probeFactory?.Invoke(expectedModelPath, probeNumber) ?? new LlamaServerRouterProbeResult(
                true,
                LocalAiModelAvailabilityState.Verified,
                expectedModelPath,
                null));
        }

        public void Dispose() { }
    }

    private sealed class FakeLifecycle(List<string> events) : ILocalAiEndpointLifecycle
    {
        public bool FailPublish { get; set; }
        public bool FailQuiesce { get; set; }
        public Exception? QuiesceException { get; set; }

        public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
            LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        {
            events.Add("quiesce");
            if (QuiesceException is not null)
                return Task.FromException<LocalAiEndpointLifecycleResult>(QuiesceException);
            return Task.FromResult(FailQuiesce
                ? LocalAiEndpointLifecycleResult.Failed("quiesce failed")
                : LocalAiEndpointLifecycleResult.Ok());
        }

        public Task<LocalAiEndpointLifecycleResult> PublishAsync(
            LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        {
            events.Add($"publish:{install.Endpoint!.Port}");
            return Task.FromResult(FailPublish
                ? LocalAiEndpointLifecycleResult.Failed("publish failed")
                : LocalAiEndpointLifecycleResult.Ok());
        }
    }

    public enum RefreshOwnershipLoss
    {
        Incomplete,
        Conflict,
        MissingEndpoint,
    }
}
