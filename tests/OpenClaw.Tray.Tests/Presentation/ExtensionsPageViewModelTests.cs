using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Text.Json;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class ExtensionsPageViewModelTests
{
    [Fact]
    public async Task Activate_PreservesAgentDeepLinkAndProjectsReadinessFilters()
    {
        var client = new FakeExtensionsClient
        {
            StatusHandler = agentId => Task.FromResult(new SkillsStatusReport
            {
                AgentId = agentId,
                Skills =
                [
                    Skill("ready", SkillReadinessState.Ready),
                    Skill("disabled", SkillReadinessState.Disabled),
                    Skill("setup", SkillReadinessState.NeedsSetup),
                    Skill("blocked", SkillReadinessState.Blocked),
                ],
            }),
        };
        using var vm = Create(client, ["main", "alpha"]);

        vm.Activate("agent:alpha:skills");
        await WaitUntilAsync(() => !vm.IsLoadingSkills && vm.VisibleSkills.Count == 4);

        Assert.Equal("alpha", vm.SelectedAgentId);
        Assert.Equal("alpha", client.LastStatusAgentId);
        vm.SetSkillFilter(SkillListFilter.NeedsSetup);
        Assert.Equal(["blocked", "setup"], vm.VisibleSkills.Select(static row => row.SkillKey).Order());
        vm.SetSkillFilter(SkillListFilter.Disabled);
        Assert.Equal("disabled", Assert.Single(vm.VisibleSkills).SkillKey);
    }

    [Fact]
    public async Task Activate_OlderGatewayShowsUpgradeWithoutSendingRequest()
    {
        var client = new FakeExtensionsClient { AdvertisedFeatures = GatewayFeatureSet.Empty };
        using var vm = Create(client, ["main"]);

        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);

        Assert.False(vm.SkillsSupported);
        Assert.Equal("ExtensionsPage_SkillsUpgradeRequired", vm.StatusMessage);
        Assert.Equal(0, client.StatusCalls);
    }

    [Fact]
    public async Task SearchAndInstall_UseExactSourceQualifiedReferenceAndRequireUnscannedConsent()
    {
        var client = new FakeExtensionsClient
        {
            SearchResult = new SkillsSearchResult
            {
                Results =
                [
                    new ClawHubSkillSearchEntry
                    {
                        Slug = "shared-slug",
                        DisplayName = "Shared skill",
                        InstallRef = "@publisher/shared-slug",
                        InstallOnly = true,
                    },
                ],
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);

        await vm.SearchSkillsAsync("shared");
        var row = Assert.Single(vm.SkillSearchResults);
        var refused = await vm.InstallSkillAsync(row, unscannedAcknowledged: false);
        Assert.True(refused.RequiresUnscannedConfirmation);
        Assert.Null(client.LastInstallRequest);

        var installed = await vm.InstallSkillAsync(row, unscannedAcknowledged: true);
        Assert.True(installed.Succeeded);
        Assert.Equal("@publisher/shared-slug", client.LastInstallRequest?.InstallReference);
        Assert.Equal("main", client.LastInstallRequest?.AgentId);
    }

    [Fact]
    public async Task AgentChange_DiscardsLateResponseFromPreviousAgent()
    {
        var alpha = new TaskCompletionSource<SkillsStatusReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeExtensionsClient
        {
            StatusHandler = agentId => agentId == "alpha"
                ? alpha.Task
                : Task.FromResult(new SkillsStatusReport { AgentId = agentId, Skills = [Skill("beta-skill", SkillReadinessState.Ready)] }),
        };
        using var vm = Create(client, ["alpha", "beta"]);
        vm.Activate("agent:alpha:extensions");
        await WaitUntilAsync(() => client.StatusCalls == 1);

        await vm.SelectAgentAsync("beta");
        alpha.SetResult(new SkillsStatusReport { AgentId = "alpha", Skills = [Skill("alpha-skill", SkillReadinessState.Ready)] });
        await Task.Delay(25);

        Assert.Equal("beta", vm.SelectedAgentId);
        Assert.Equal("beta-skill", Assert.Single(vm.VisibleSkills).SkillKey);
    }

    private static SkillStatusEntry Skill(string key, SkillReadinessState readiness) => new()
    {
        SkillKey = key,
        Name = key,
        Eligible = readiness == SkillReadinessState.Ready,
        Disabled = readiness == SkillReadinessState.Disabled,
        BlockedByAllowlist = readiness == SkillReadinessState.Blocked,
        PlatformIncompatible = readiness == SkillReadinessState.Incompatible,
        Missing = readiness == SkillReadinessState.NeedsSetup
            ? new SkillRequirements { Bins = ["tool"] }
            : new SkillRequirements(),
    };

    private static ExtensionsPageViewModel Create(FakeExtensionsClient client, IReadOnlyList<string> agents) =>
        new(
            new ExtensionsRuntimeSource(
                () => client,
                () => agents,
                static key => key,
                static (key, values) => key + ":" + string.Join(",", values)),
            new RecordingUiDispatcher());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition());
    }

#pragma warning disable CS0067, CS0618
    private sealed class FakeExtensionsClient : IOperatorGatewayClient, ISkillsGatewayEvents
    {
        public Func<string?, Task<SkillsStatusReport>> StatusHandler { get; set; } =
            agentId => Task.FromResult(new SkillsStatusReport { AgentId = agentId });
        public SkillsSearchResult SearchResult { get; set; } = new();
        public ClawHubSkillInstallRequest? LastInstallRequest { get; private set; }
        public string? LastStatusAgentId { get; private set; }
        public int StatusCalls { get; private set; }
        public string? OperatorDeviceId => "operator";
        public IReadOnlyList<string> GrantedOperatorScopes { get; set; } = ["operator.read", "operator.admin"];
        public bool IsConnectedToGateway { get; set; } = true;
        public string? MainSessionKey => "agent:main:main";
        public bool HasHandshakeSnapshot => true;
        public GatewayFeatureSet AdvertisedFeatures { get; set; } = new(
            ["skills.status", "skills.search", "skills.detail", "skills.securityVerdicts", "skills.install", "skills.update"],
            ["skills.changed"]);
        public long ConnectionEpoch { get; set; } = 1;

        public event EventHandler? SkillsChanged;
        public event EventHandler<OpenClawNotification>? NotificationReceived;
        public event EventHandler<AgentActivity>? ActivityChanged;
        public event EventHandler<ChannelHealth[]>? ChannelHealthUpdated;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<GatewayUsageInfo>? UsageUpdated;
        public event EventHandler<GatewayUsageStatusInfo>? UsageStatusUpdated;
        public event EventHandler<GatewayCostUsageInfo>? UsageCostUpdated;
        public event EventHandler<GatewayNodeInfo[]>? NodesUpdated;
        public event EventHandler<SessionsPreviewPayloadInfo>? SessionPreviewUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<GatewaySelfInfo>? GatewaySelfUpdated;
        public event EventHandler<JsonElement>? CronListUpdated;
        public event EventHandler<JsonElement>? CronStatusUpdated;
        public event EventHandler<JsonElement>? CronRunsUpdated;
        public event EventHandler<JsonElement>? SkillsStatusUpdated;
        public event EventHandler<JsonElement>? ConfigUpdated;
        public event EventHandler<JsonElement>? ConfigSchemaUpdated;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<PairingListInfo>? NodePairListUpdated;
        public event EventHandler<DevicePairingListInfo>? DevicePairListUpdated;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public event EventHandler<PresenceEntry[]>? PresenceUpdated;
        public event EventHandler<JsonElement>? AgentsListUpdated;
        public event EventHandler<JsonElement>? AgentFilesListUpdated;
        public event EventHandler<JsonElement>? AgentFileContentUpdated;
        public event EventHandler<AgentEventInfo>? ChatEventReceived;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<string>? AuthenticationFailed;
        public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
        public event EventHandler? HandshakeSucceeded;

        public Task<SkillsStatusReport> GetSkillsStatusAsync(string? agentId = null, int timeoutMs = 15000)
        {
            StatusCalls++;
            LastStatusAgentId = agentId;
            return StatusHandler(agentId);
        }
        public Task<SkillsSecurityVerdictsResult> GetSkillSecurityVerdictsAsync(string? agentId = null, int timeoutMs = 15000) =>
            Task.FromResult(new SkillsSecurityVerdictsResult());
        public Task<SkillsSearchResult> SearchSkillsAsync(string? query = null, int limit = 20, int timeoutMs = 15000) =>
            Task.FromResult(SearchResult);
        public Task<SkillsDetailResult> GetSkillDetailAsync(string installReference, int timeoutMs = 15000) =>
            Task.FromResult(new SkillsDetailResult { Skill = new ClawHubSkillDetail { Slug = installReference } });
        public Task<SkillMutationResult> InstallClawHubSkillAsync(ClawHubSkillInstallRequest request, int timeoutMs = 120000)
        {
            LastInstallRequest = request;
            return Task.FromResult(new SkillMutationResult { Ok = true });
        }
        public Task<SkillMutationResult> UpdateClawHubSkillAsync(ClawHubSkillUpdateRequest request, int timeoutMs = 120000) =>
            Task.FromResult(new SkillMutationResult { Ok = true });
        public Task<SkillMutationResult> SetSkillEnabledDetailedAsync(string skillKey, bool enabled, int timeoutMs = 15000) =>
            Task.FromResult(new SkillMutationResult { Ok = true });
        public void RaiseSkillsChanged() => SkillsChanged?.Invoke(this, EventArgs.Empty);
        public void SetUserRules(IReadOnlyList<UserNotificationRule>? rules) { }
        public void SetPreferStructuredCategories(bool value) { }
        public Task SendChatMessageAsync(string message, string? sessionKey = null) => Task.CompletedTask;
        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey = null) => Task.FromResult(new ChatSendResult());
        public Task CheckHealthAsync() => Task.CompletedTask;
        public Task RequestSessionsAsync(string? agentId = null) => Task.CompletedTask;
        public Task RequestUsageAsync() => Task.CompletedTask;
        public Task RequestNodesAsync() => Task.CompletedTask;
        public Task RequestUsageStatusAsync() => Task.CompletedTask;
        public Task RequestUsageCostAsync(int days = 30) => Task.CompletedTask;
        public Task RequestSessionPreviewAsync(string[] keys, int limit = 12, int maxChars = 240) => Task.CompletedTask;
        public Task<bool> PatchSessionAsync(string key, string? model = null, string? thinkingLevel = null, string? verboseLevel = null) => Task.FromResult(false);
        public Task<bool> ResetSessionAsync(string key) => Task.FromResult(false);
        public Task<bool> DeleteSessionAsync(string key, bool deleteTranscript = true) => Task.FromResult(false);
        public Task<bool> CompactSessionAsync(string key, int maxLines = 400) => Task.FromResult(false);
        public Task RequestCronListAsync() => Task.CompletedTask;
        public Task RequestCronStatusAsync() => Task.CompletedTask;
        public Task<bool> RunCronJobAsync(string jobId, bool force = true) => Task.FromResult(false);
        public Task<bool> RemoveCronJobAsync(string jobId) => Task.FromResult(false);
        public Task<bool> AddCronJobAsync(object jobDefinition) => Task.FromResult(false);
        public Task<bool> UpdateCronJobAsync(string id, object patch) => Task.FromResult(false);
        public Task RequestCronRunsAsync(string? id = null, int limit = 20, int offset = 0) => Task.CompletedTask;
        public Task RequestSkillsStatusAsync(string? agentId = null) => Task.CompletedTask;
        public Task<bool> InstallSkillAsync(string skillId) => Task.FromResult(false);
        public Task<bool> SetSkillEnabledAsync(string skillKey, bool enabled) => Task.FromResult(false);
        public Task RequestConfigAsync() => Task.CompletedTask;
        public Task RequestConfigSchemaAsync() => Task.CompletedTask;
        public Task<bool> SetConfigAsync(string path, object value) => Task.FromResult(false);
        public Task<bool> PatchConfigAsync(JsonElement fullConfig, string? baseHash) => Task.FromResult(false);
        public Task<ConfigPatchResult> PatchConfigDetailedAsync(JsonElement fullConfig, string? baseHash, int timeoutMs = 15000) => Task.FromResult(new ConfigPatchResult { Ok = false });
        public Task RequestAgentsListAsync() => Task.CompletedTask;
        public Task RequestAgentFilesListAsync(string agentId = "main") => Task.CompletedTask;
        public Task RequestAgentFileGetAsync(string agentId, string name) => Task.CompletedTask;
        public Task RequestModelsListAsync() => Task.CompletedTask;
        public Task RequestNodePairListAsync() => Task.CompletedTask;
        public Task<bool> NodePairApproveAsync(string requestId) => Task.FromResult(false);
        public Task<bool> NodePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task<NodeForgetResult> NodePairRemoveAsync(string nodeId) => Task.FromResult(new NodeForgetResult(false));
        public Task<NodeRenameResult> NodeRenameAsync(string nodeId, string displayName) => Task.FromResult(new NodeRenameResult(false));
        public Task RequestDevicePairListAsync() => Task.CompletedTask;
        public Task<bool> DevicePairApproveAsync(string requestId) => Task.FromResult(false);
        public Task<bool> DevicePairRejectAsync(string requestId) => Task.FromResult(false);
        public Task<bool> StartChannelAsync(string channelName) => Task.FromResult(false);
        public Task<ChannelStartResult?> StartChannelDetailedAsync(string channelName, int timeoutMs = 12000) => Task.FromResult<ChannelStartResult?>(null);
        public Task<bool> StopChannelAsync(string channelName) => Task.FromResult(false);
        public Task<ChannelsStatusSnapshot?> GetChannelsStatusAsync(bool probe = false, int timeoutMs = 12000) => Task.FromResult<ChannelsStatusSnapshot?>(null);
        public Task<bool> LogoutChannelAsync(string channelName, int timeoutMs = 12000) => Task.FromResult(false);
        public Task<WebLoginStartResult?> WebLoginStartAsync(bool force = false, int timeoutMs = 30000) => Task.FromResult<WebLoginStartResult?>(null);
        public Task<WebLoginWaitResult?> WebLoginWaitAsync(string? currentQrDataUrl = null, int timeoutMs = 30000) => Task.FromResult<WebLoginWaitResult?>(null);
        public Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000) => Task.FromResult(default(JsonElement));
    }
#pragma warning restore CS0067, CS0618
}
