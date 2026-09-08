using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Text.Json;
using System.Reflection;

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
    public async Task SearchAndInstall_UseExactSourceQualifiedReferenceAndRequireExplicitTrustConsent()
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
                        TrustState = "not-scanned-by-clawhub",
                        Version = "search-label-only",
                    },
                ],
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);

        await vm.SearchSkillsAsync("shared");
        var row = Assert.Single(vm.SkillSearchResults);
        Assert.Equal("ExtensionsPage_TrustNotScanned", row.TrustLabel);
        var review = Assert.IsType<SkillReviewPresentation>(await vm.ReviewSkillAsync(row));
        var refused = await vm.InstallSkillAsync(review, unscannedAcknowledged: false);
        Assert.True(refused.RequiresUnscannedConfirmation);
        Assert.Null(client.LastInstallRequest);

        var installed = await vm.InstallSkillAsync(review, unscannedAcknowledged: true);
        Assert.True(installed.Succeeded);
        Assert.Equal("@publisher/shared-slug", client.LastInstallRequest?.InstallReference);
        Assert.Equal("main", client.LastInstallRequest?.AgentId);
        Assert.Null(client.LastInstallRequest?.Version);
    }

    [Fact]
    public async Task InstallOnlyWithoutUnscannedTrust_DoesNotInventSecurityWarning()
    {
        var client = new FakeExtensionsClient
        {
            SearchResult = new SkillsSearchResult
            {
                Results =
                [
                    new ClawHubSkillSearchEntry
                    {
                        Slug = "external",
                        DisplayName = "External skill",
                        InstallRef = "skills-sh:owner/repo@commit",
                        InstallOnly = true,
                        Version = "display-only",
                    },
                ],
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);

        await vm.SearchSkillsAsync("external");
        var review = Assert.IsType<SkillReviewPresentation>(
            await vm.ReviewSkillAsync(Assert.Single(vm.SkillSearchResults)));
        Assert.False(review.RequiresUnscannedConfirmation);

        var installed = await vm.InstallSkillAsync(review, unscannedAcknowledged: false);
        Assert.True(installed.Succeeded);
        Assert.Equal("skills-sh:owner/repo@commit", client.LastInstallRequest?.InstallReference);
        Assert.Null(client.LastInstallRequest?.Version);
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

    [Fact]
    public async Task SkillDetailReview_PinsDisplayedVersionAndSendsThatExactVersion()
    {
        var client = new FakeExtensionsClient
        {
            SearchResult = new SkillsSearchResult
            {
                Results =
                [
                    new ClawHubSkillSearchEntry
                    {
                        Slug = "reviewed",
                        DisplayName = "Reviewed",
                        InstallRef = "@owner/reviewed",
                        Version = "1.0.0",
                    },
                ],
            },
            DetailResult = new SkillsDetailResult
            {
                Skill = new ClawHubSkillDetail { Slug = "reviewed", DisplayName = "Reviewed" },
                LatestVersion = new ClawHubSkillVersion { Version = "1.2.0" },
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);
        await vm.SearchSkillsAsync("reviewed");

        var review = Assert.IsType<SkillReviewPresentation>(
            await vm.ReviewSkillAsync(Assert.Single(vm.SkillSearchResults)));
        Assert.Equal("1.2.0", review.Version);
        Assert.Equal("@owner/reviewed", review.InstallReference);

        var outcome = await vm.InstallSkillAsync(review, unscannedAcknowledged: false);
        Assert.True(outcome.Succeeded);
        Assert.Equal("1.2.0", client.LastInstallRequest?.Version);
        Assert.Equal("@owner/reviewed", client.LastInstallRequest?.InstallReference);
    }

    [Fact]
    public async Task SkillDetailReview_RejectsExpiredEpochBeforeInstall()
    {
        var client = new FakeExtensionsClient
        {
            SearchResult = new SkillsSearchResult
            {
                Results =
                [
                    new ClawHubSkillSearchEntry
                    {
                        Slug = "reviewed",
                        InstallRef = "@owner/reviewed",
                        Version = "1.0.0",
                    },
                ],
            },
            DetailResult = new SkillsDetailResult
            {
                Skill = new ClawHubSkillDetail { Slug = "reviewed" },
                LatestVersion = new ClawHubSkillVersion { Version = "1.2.0" },
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);
        await vm.SearchSkillsAsync("reviewed");
        var review = Assert.IsType<SkillReviewPresentation>(
            await vm.ReviewSkillAsync(Assert.Single(vm.SkillSearchResults)));

        client.ConnectionEpoch++;
        var outcome = await vm.InstallSkillAsync(review, unscannedAcknowledged: false);

        Assert.False(outcome.Succeeded);
        Assert.Null(client.LastInstallRequest);
    }

    [Fact]
    public async Task InstalledSkillSecurityVerdicts_RequireExactRegistryOwnerSlugAndVersion()
    {
        var client = new FakeExtensionsClient
        {
            StatusHandler = agentId => Task.FromResult(new SkillsStatusReport
            {
                AgentId = agentId,
                Skills =
                [
                    new SkillStatusEntry
                    {
                        SkillKey = "owner-a-skill",
                        Name = "Owner A",
                        Eligible = true,
                        Clawhub = new ClawHubSkillLink
                        {
                            Valid = true,
                            Registry = "https://clawhub.ai",
                            Slug = "shared",
                            OwnerHandle = "owner-a",
                            InstalledVersion = "1.0.0",
                        },
                    },
                    new SkillStatusEntry
                    {
                        SkillKey = "owner-b-skill",
                        Name = "Owner B",
                        Eligible = true,
                        Clawhub = new ClawHubSkillLink
                        {
                            Valid = true,
                            Registry = "https://clawhub.ai",
                            Slug = "shared",
                            OwnerHandle = "owner-b",
                            InstalledVersion = "2.0.0",
                        },
                    },
                ],
            }),
            SecurityResult = new SkillsSecurityVerdictsResult
            {
                Items =
                [
                    new SkillSecurityVerdict
                    {
                        Registry = "https://clawhub.ai",
                        RequestedSlug = "shared",
                        RequestedOwnerHandle = "owner-a",
                        RequestedVersion = "1.0.0",
                        SecurityStatus = "passed-a",
                        SkillUrl = "https://clawhub.ai/owner-a/shared",
                    },
                    new SkillSecurityVerdict
                    {
                        Registry = "https://clawhub.ai",
                        RequestedSlug = "shared",
                        RequestedOwnerHandle = "owner-b",
                        RequestedVersion = "2.0.0",
                        SecurityStatus = "passed-b",
                        SkillUrl = "https://clawhub.ai/owner-b/shared",
                    },
                ],
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills && vm.VisibleSkills.Count == 2);

        var ownerA = Assert.Single(vm.VisibleSkills, row => row.SkillKey == "owner-a-skill");
        var ownerB = Assert.Single(vm.VisibleSkills, row => row.SkillKey == "owner-b-skill");
        Assert.Equal("passed-a", ownerA.SecurityLabel);
        Assert.Equal("https://clawhub.ai/owner-a/shared", ownerA.SafeSkillUrl);
        Assert.Equal("passed-b", ownerB.SecurityLabel);
        Assert.Equal("https://clawhub.ai/owner-b/shared", ownerB.SafeSkillUrl);
    }

    [Fact]
    public async Task LateSkillReviewFailure_CannotOverwriteNewerReviewState()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<SkillsDetailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeExtensionsClient
        {
            SearchResult = new SkillsSearchResult
            {
                Results =
                [
                    new ClawHubSkillSearchEntry { Slug = "first", InstallRef = "@owner/first" },
                    new ClawHubSkillSearchEntry { Slug = "second", InstallRef = "@owner/second" },
                ],
            },
            DetailHandler = installReference =>
            {
                if (installReference == "@owner/first")
                {
                    firstEntered.SetResult();
                    return first.Task;
                }
                return Task.FromResult(new SkillsDetailResult
                {
                    Skill = new ClawHubSkillDetail { Slug = "second" },
                });
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingSkills);
        await vm.SearchSkillsAsync("skills");
        var firstRow = Assert.Single(vm.SkillSearchResults, row => row.Slug == "first");
        var secondRow = Assert.Single(vm.SkillSearchResults, row => row.Slug == "second");

        var firstReview = vm.ReviewSkillAsync(firstRow);
        await firstEntered.Task;
        Assert.NotNull(await vm.ReviewSkillAsync(secondRow));
        first.SetException(new InvalidOperationException("stale failure"));
        Assert.Null(await firstReview);

        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task PluginCatalog_LoadsOnlyInstalledEntriesAndPreservesDiagnostics()
    {
        var client = new FakeExtensionsClient
        {
            AdvertisedFeatures = FeaturesWithPlugins(),
            PluginListResult = new PluginsListResult
            {
                MutationAllowed = true,
                DiagnosticCount = 2,
                Plugins =
                [
                    new PluginCatalogEntry { Id = "installed", Name = "Installed", Installed = true, Enabled = true, Kind = ["provider"] },
                    new PluginCatalogEntry { Id = "catalog-only", Name = "Catalog", Installed = false },
                ],
            },
        };
        using var vm = Create(client, ["main"]);

        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        Assert.Equal("installed", Assert.Single(vm.InstalledPlugins).PluginId);
        Assert.True(vm.PluginMutationAllowed);
        Assert.Equal(2, vm.PluginDiagnosticCount);
        Assert.Contains("2", vm.PluginStatusMessage);
    }

    [Fact]
    public async Task OfficialCatalogEntry_UsesExactOfficialInstallIdentity()
    {
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "official.plugin",
                    Name = "Official plugin",
                    Installed = false,
                    Version = "4.5.6",
                    Install = new PluginCatalogInstallAction
                    {
                        Source = "official",
                        PluginId = "official.plugin",
                    },
                },
            ],
        };
        client.PluginInspectResult = new PluginInspectResult
        {
            Ok = true,
            Plugin = new PluginInspectEntry { Id = "official.plugin", Name = "Official plugin", Version = "4.5.6" },
            Source = new PluginInspectSource { Kind = "official", Spec = "official.plugin" },
            ReviewToken = "official-review-token",
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        var row = Assert.Single(vm.PluginSearchResults);
        Assert.True(row.IsOfficial);
        Assert.Equal(PluginInstallSource.Official, row.InstallSource);
        var review = Assert.IsType<PluginReviewPresentation>(await vm.ReviewPluginAsync(row));
        var outcome = await vm.InstallPluginAsync(review, vm.CreateAcknowledgement(review));

        Assert.True(outcome.Succeeded);
        Assert.Equal(PluginInstallSource.Official, client.LastPluginInstallRequest?.Source);
        Assert.Equal("official.plugin", client.LastPluginInstallRequest?.PluginId);
        Assert.Null(client.LastPluginInstallRequest?.PackageName);
        Assert.Null(client.LastPluginInstallRequest?.Version);
    }

    [Fact]
    public async Task OfficialCatalogConsent_BindsRuntimeIdWithoutAssumingSourceKind()
    {
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "official.plugin",
                    Name = "Official plugin",
                    Version = "4.5.6",
                    Install = new PluginCatalogInstallAction
                    {
                        Source = "official",
                        PluginId = "official.plugin",
                    },
                },
            ],
        };
        client.PluginInspectResult = new PluginInspectResult
        {
            Ok = true,
            Plugin = new PluginInspectEntry { Id = "official.plugin", Name = "Official plugin", Version = "4.5.6" },
            Source = new PluginInspectSource
            {
                Kind = "clawhub",
                Spec = "clawhub:@openclaw/official-plugin@4.5.6",
                PackageName = "@openclaw/official-plugin",
            },
            ReviewToken = "fresh-official-review",
        };
        client.PluginInstallHandler = _ => Task.FromException<PluginMutationResult>(GatewayError(
            "plugins.install",
            "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
            """
            {
              "capabilityConsentCode":"PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId":"official.plugin",
              "reviewToken":"challenge-token",
              "widened":{"contracts":["contract.official"]}
            }
            """));
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        var outcome = await vm.InstallPluginAsync(review, vm.CreateAcknowledgement(review));

        Assert.NotNull(outcome.CapabilityPrompt);
        Assert.Equal("fresh-official-review", outcome.CapabilityPrompt.Acknowledgement.ReviewToken);
        Assert.Contains("contract.official", outcome.CapabilityPrompt.WidenedSurfaces);
    }

    [Fact]
    public async Task PluginMutationControls_RequireInspectMethod()
    {
        var client = LifecycleClient();
        client.AdvertisedFeatures = new GatewayFeatureSet(
            ["skills.status", "plugins.list", "plugins.install", "plugins.setEnabled", "plugins.uninstall"],
            []);
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "installed",
                    Installed = true,
                    Removable = true,
                },
                new PluginCatalogEntry
                {
                    Id = "catalog",
                    Installed = false,
                    Install = new PluginCatalogInstallAction { Source = "official", PluginId = "catalog" },
                },
            ],
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        var installed = Assert.Single(vm.InstalledPlugins);
        var catalog = Assert.Single(vm.PluginSearchResults);
        Assert.False(installed.CanReview);
        Assert.False(installed.CanSetEnabled);
        Assert.False(installed.CanUninstall);
        Assert.False(catalog.CanReview);
        Assert.False(catalog.CanInstall);
    }

    [Fact]
    public async Task PluginSearchAndInspect_UseRuntimeIdAndShowExactDeclaredSurfaces()
    {
        var client = new FakeExtensionsClient
        {
            AdvertisedFeatures = FeaturesWithPlugins(),
            PluginSearchResult = new PluginsSearchResult
            {
                Results =
                [
                    new PluginSearchEntry
                    {
                        Package = new PluginSearchPackage
                        {
                            Name = "@publisher/package",
                            DisplayName = "Plugin",
                            RuntimeId = "publisher.plugin",
                        },
                    },
                ],
            },
            PluginInspectResult = new PluginInspectResult
            {
                Ok = true,
                Plugin = new PluginInspectEntry { Id = "publisher.plugin", Name = "Plugin" },
                Declared = new PluginDeclaredSurface
                {
                    Providers = ["provider.alpha"],
                    Tools = ["tool.read", "tool.write"],
                    Contracts = ["contract.alpha"],
                    McpServers = ["server.one"],
                },
                Source = new PluginInspectSource
                {
                    Kind = "clawhub",
                    PackageName = "@publisher/package",
                    Integrity = "sha512-exact",
                },
                Grants = new PluginOperatorGrants
                {
                    Hooks = new PluginHookGrants
                    {
                        AllowConversationAccess = new PluginHookGrant { Effective = true },
                    },
                },
                Trust = new PluginInstallTrust { Pending = true },
                ReviewToken = "exact-review-token",
            },
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        await vm.SearchPluginsAsync("publisher");
        var review = await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults));

        Assert.Equal("publisher.plugin", client.LastInspectPluginId);
        Assert.NotNull(review);
        Assert.Contains("provider.alpha", review.DeclaredSurfaces);
        Assert.Contains("tool.read, tool.write", review.DeclaredSurfaces);
        Assert.Contains("contract.alpha", review.DeclaredSurfaces);
        Assert.Contains("server.one", review.DeclaredSurfaces);
        Assert.Contains("ExtensionsPage_PluginGrantConversationAccess", review.GrantedAccess);
        Assert.Contains("ExtensionsPage_PluginTrustPending", review.Trust);
        Assert.Equal("@publisher/package", review.InstallIdentity);
        Assert.Equal("sha512-exact", review.Integrity);
        Assert.Equal("exact-review-token", review.ReviewToken);
        Assert.Equal(client.ConnectionEpoch, review.ConnectionEpoch);
    }

    [Fact]
    public async Task LatePluginReviewFailure_CannotOverwriteNewerReviewState()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<PluginInspectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry { Id = "first.plugin", Name = "First", Installed = true },
                new PluginCatalogEntry { Id = "second.plugin", Name = "Second", Installed = true },
            ],
        };
        client.PluginInspectHandler = pluginId =>
        {
            if (pluginId == "first.plugin")
            {
                firstEntered.SetResult();
                return first.Task;
            }
            return Task.FromResult(new PluginInspectResult
            {
                Ok = true,
                Plugin = new PluginInspectEntry { Id = "second.plugin", Name = "Second" },
                ReviewToken = "second-review",
            });
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        var firstRow = Assert.Single(vm.InstalledPlugins, row => row.PluginId == "first.plugin");
        var secondRow = Assert.Single(vm.InstalledPlugins, row => row.PluginId == "second.plugin");

        var firstReview = vm.ReviewPluginAsync(firstRow);
        await firstEntered.Task;
        Assert.NotNull(await vm.ReviewPluginAsync(secondRow));
        first.SetException(new InvalidOperationException("stale failure"));
        Assert.Null(await firstReview);

        Assert.Null(vm.PluginErrorMessage);
    }

    [Fact]
    public async Task PluginInstall_StagesExactCommunityPackageWithoutInventingConsent()
    {
        var client = LifecycleClient();
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));
        Assert.Empty(review.ReviewToken);
        Assert.Null(vm.CreateAcknowledgement(review));

        var outcome = await vm.InstallPluginAsync(review);

        Assert.True(outcome.Succeeded);
        var request = Assert.IsType<PluginInstallRequest>(client.LastPluginInstallRequest);
        Assert.Equal(PluginInstallSource.ClawHub, request.Source);
        Assert.Equal("@publisher/package", request.PackageName);
        Assert.Equal("1.2.3", request.Version);
        Assert.Null(request.AcknowledgeCapabilities);
        Assert.False(request.AcknowledgeInstallPolicyWarning);
    }

    [Fact]
    public async Task PluginCapabilityChallenge_UsesFreshInspectionTokenAndExactIdentityOnRetry()
    {
        var client = LifecycleClient();
        client.PluginInstallHandler = _ => Task.FromException<PluginMutationResult>(GatewayError(
            "plugins.install",
            "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
            """
            {
              "capabilityConsentCode":"PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId":"publisher.plugin",
              "reviewToken":"challenge-token",
              "widened":{"tools":["tool.write"]}
            }
            """));
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        var outcome = await vm.InstallPluginAsync(review);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.CapabilityPrompt);
        Assert.Equal("fresh-review-token", outcome.CapabilityPrompt.Acknowledgement.ReviewToken);
        Assert.Equal(client.ConnectionEpoch, outcome.CapabilityPrompt.Acknowledgement.ConnectionEpoch);
        Assert.Contains("tool.write", outcome.CapabilityPrompt.WidenedSurfaces);
        Assert.Contains("contract.alpha", outcome.CapabilityPrompt.Review.DeclaredSurfaces);
        Assert.Equal("@publisher/package", outcome.CapabilityPrompt.Review.InstallIdentity);
        Assert.Equal("sha512-exact", outcome.CapabilityPrompt.Review.Integrity);
        Assert.Equal("publisher.plugin", client.LastInspectPluginId);

        client.PluginInstallHandler = _ => Task.FromResult(new PluginMutationResult { Ok = true });
        var accepted = await vm.InstallPluginAsync(
            outcome.CapabilityPrompt.Review,
            outcome.CapabilityPrompt.Acknowledgement);
        Assert.True(accepted.Succeeded);
        Assert.Equal("fresh-review-token", client.LastPluginInstallRequest?.AcknowledgeCapabilities?.ReviewToken);
    }

    [Fact]
    public async Task PluginInstallPolicyWarning_RequiresSeparateAcknowledgementOnRetry()
    {
        var client = LifecycleClient();
        var calls = 0;
        client.PluginInstallHandler = request =>
        {
            calls++;
            if (!request.AcknowledgeInstallPolicyWarning)
            {
                return Task.FromException<PluginMutationResult>(GatewayError(
                    "plugins.install",
                    "POLICY",
                    """
                    {
                      "installPolicyCode":"install_policy_warning_acknowledgement_required",
                      "targetName":"@publisher/package",
                      "targetType":"plugin",
                      "requestMode":"install",
                      "reason":"review script behavior",
                      "findings":[{"ruleId":"script","severity":"warning","message":"postinstall script"}]
                    }
                    """));
            }
            return Task.FromResult(new PluginMutationResult { Ok = true });
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));
        var acknowledgement = vm.CreateAcknowledgement(review);
        Assert.Null(acknowledgement);

        var warning = await vm.InstallPluginAsync(review, acknowledgement);
        Assert.NotNull(warning.InstallPolicyPrompt);
        Assert.Contains("postinstall script", warning.InstallPolicyPrompt.Findings);

        var accepted = await vm.InstallPluginAsync(
            review,
            acknowledgement,
            acknowledgeInstallPolicyWarning: true);
        Assert.True(accepted.Succeeded);
        Assert.True(client.LastPluginInstallRequest?.AcknowledgeInstallPolicyWarning);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PluginInstallPolicyWarning_DisclosesGatewayManifestTarget()
    {
        var client = LifecycleClient();
        client.PluginInstallHandler = _ => Task.FromException<PluginMutationResult>(GatewayError(
            "plugins.install",
            "POLICY",
            """
            {
              "installPolicyCode":"install_policy_warning_acknowledgement_required",
              "targetName":"@other/package",
              "targetType":"plugin",
              "requestMode":"install",
              "reason":"wrong target",
              "findings":[]
            }
            """));
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        var outcome = await vm.InstallPluginAsync(review);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.InstallPolicyPrompt);
        Assert.Contains("@other/package", outcome.InstallPolicyPrompt.Reason);
    }

    [Fact]
    public async Task PluginCapabilityChallenge_ForDifferentPluginIsRejectedBeforeInspect()
    {
        var client = LifecycleClient();
        client.PluginInstallHandler = _ => Task.FromException<PluginMutationResult>(GatewayError(
            "plugins.install",
            "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
            """
            {
              "capabilityConsentCode":"PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId":"different.plugin",
              "reviewToken":"challenge-token",
              "widened":{}
            }
            """));
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        var outcome = await vm.InstallPluginAsync(review);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.CapabilityPrompt);
        Assert.Null(client.LastInspectPluginId);
        Assert.Equal("ExtensionsPage_PluginReviewExpired", outcome.Message);
    }

    [Fact]
    public async Task PluginCapabilityChallenge_WithoutExactStagedPackageIdentityIsRejected()
    {
        var client = LifecycleClient();
        client.PluginInspectResult.Source = null;
        client.PluginInstallHandler = _ => Task.FromException<PluginMutationResult>(GatewayError(
            "plugins.install",
            "PLUGIN_CAPABILITY_CONSENT_REQUIRED",
            """
            {
              "capabilityConsentCode":"PLUGIN_CAPABILITY_CONSENT_REQUIRED",
              "pluginId":"publisher.plugin",
              "reviewToken":"challenge-token",
              "widened":{}
            }
            """));
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        var outcome = await vm.InstallPluginAsync(review);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.CapabilityPrompt);
        Assert.Equal("ExtensionsPage_PluginReviewExpired", outcome.Message);
    }

    [Fact]
    public async Task PluginReviewToken_ExpiresAcrossConnectionEpoch()
    {
        var client = LifecycleClient();
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        client.ConnectionEpoch++;

        Assert.Null(vm.CreateAcknowledgement(review));
    }

    [Fact]
    public async Task PluginReviewToken_ExpiresWhenClientSwapsAtSameEpoch()
    {
        var original = LifecycleClient();
        var replacement = LifecycleClient();
        IOperatorGatewayClient current = original;
        using var vm = new ExtensionsPageViewModel(
            new ExtensionsRuntimeSource(
                () => current,
                () => ["main"],
                static key => key,
                static (key, values) => key + ":" + string.Join(",", values)),
            new RecordingUiDispatcher());
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        await vm.SearchPluginsAsync("publisher");
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.PluginSearchResults)));

        current = replacement;

        Assert.Null(vm.CreateAcknowledgement(review));
        var outcome = await vm.InstallPluginAsync(review);
        Assert.False(outcome.Succeeded);
        Assert.Null(replacement.LastPluginInstallRequest);
    }

    [Fact]
    public async Task InstalledPluginMutations_UseExactReviewedPluginId()
    {
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "publisher.plugin",
                    Name = "Plugin",
                    Installed = true,
                    Enabled = false,
                    Removable = true,
                },
            ],
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        var item = Assert.Single(vm.InstalledPlugins);
        var review = Assert.IsType<PluginReviewPresentation>(await vm.ReviewPluginAsync(item));

        var enabled = await vm.SetPluginEnabledAsync(review, vm.CreateAcknowledgement(review));
        var uninstalled = await vm.UninstallPluginAsync(review);

        Assert.True(enabled.Succeeded);
        Assert.True(uninstalled.Succeeded);
        Assert.Equal("publisher.plugin", client.LastPluginSetEnabledRequest?.PluginId);
        Assert.True(client.LastPluginSetEnabledRequest?.Enabled);
        Assert.Equal("publisher.plugin", client.LastUninstalledPluginId);
    }

    [Fact]
    public async Task DisablingPlugin_DoesNotSendCapabilityAcknowledgement()
    {
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "publisher.plugin",
                    Name = "Plugin",
                    Installed = true,
                    Enabled = true,
                    Removable = true,
                },
            ],
        };
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);
        var review = Assert.IsType<PluginReviewPresentation>(
            await vm.ReviewPluginAsync(Assert.Single(vm.InstalledPlugins)));

        var outcome = await vm.SetPluginEnabledAsync(review);

        Assert.True(outcome.Succeeded);
        Assert.False(client.LastPluginSetEnabledRequest?.Enabled);
        Assert.Null(client.LastPluginSetEnabledRequest?.AcknowledgeCapabilities);
    }

    [Fact]
    public async Task ActivePluginSearch_IsNotOverwrittenByLateCatalogLoad()
    {
        var list = new TaskCompletionSource<PluginsListResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = LifecycleClient();
        client.PluginListHandler = () => list.Task;
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");

        await vm.SearchPluginsAsync("publisher");
        Assert.Equal("Plugin", Assert.Single(vm.PluginSearchResults).Name);
        list.SetResult(new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "catalog",
                    Name = "Catalog",
                    Install = new PluginCatalogInstallAction { Source = "official", PluginId = "catalog" },
                },
            ],
        });
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        Assert.Equal("Plugin", Assert.Single(vm.PluginSearchResults).Name);
    }

    [Fact]
    public async Task EmptyPluginQuery_CancelsSearchSpinnerAndRestoresCatalog()
    {
        var search = new TaskCompletionSource<PluginsSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = LifecycleClient();
        client.PluginListResult = new PluginsListResult
        {
            MutationAllowed = true,
            Plugins =
            [
                new PluginCatalogEntry
                {
                    Id = "catalog",
                    Name = "Catalog",
                    Install = new PluginCatalogInstallAction { Source = "official", PluginId = "catalog" },
                },
            ],
        };
        client.PluginSearchHandler = (_, _) => search.Task;
        using var vm = Create(client, ["main"]);
        vm.Activate("extensions");
        await WaitUntilAsync(() => !vm.IsLoadingPlugins);

        var pending = vm.SearchPluginsAsync("publisher");
        await WaitUntilAsync(() => vm.IsSearchingPlugins);
        await vm.SearchPluginsAsync(string.Empty);

        Assert.False(vm.IsSearchingPlugins);
        Assert.Equal("Catalog", Assert.Single(vm.PluginSearchResults).Name);
        search.SetResult(client.PluginSearchResult);
        await pending;
        Assert.Equal("Catalog", Assert.Single(vm.PluginSearchResults).Name);
    }

    [Fact]
    public async Task RuntimeClientReplacement_ReloadsAndMovesSkillsChangedSubscription()
    {
        var original = new FakeExtensionsClient
        {
            StatusHandler = agentId => Task.FromResult(new SkillsStatusReport
            {
                AgentId = agentId,
                Skills = [Skill("original", SkillReadinessState.Ready)],
            }),
        };
        var replacement = new FakeExtensionsClient
        {
            StatusHandler = agentId => Task.FromResult(new SkillsStatusReport
            {
                AgentId = agentId,
                Skills = [Skill("replacement", SkillReadinessState.Ready)],
            }),
        };
        IOperatorGatewayClient current = original;
        var runtime = new ExtensionsRuntimeSource(
            () => current,
            () => ["main"],
            static key => key,
            static (key, values) => key + ":" + string.Join(",", values));
        using var vm = new ExtensionsPageViewModel(runtime, new RecordingUiDispatcher());
        vm.Activate("extensions");
        await WaitUntilAsync(() => vm.VisibleSkills.Count == 1);
        Assert.Equal(1, original.SkillsChangedSubscriberCount);

        current = replacement;
        runtime.NotifyCurrentClientChanged();
        await WaitUntilAsync(() => vm.VisibleSkills.Count == 1 &&
            vm.VisibleSkills[0].SkillKey == "replacement");
        Assert.Equal(0, original.SkillsChangedSubscriberCount);
        Assert.Equal(1, replacement.SkillsChangedSubscriberCount);

        var calls = replacement.StatusCalls;
        original.RaiseSkillsChanged();
        await Task.Delay(25);
        Assert.Equal(calls, replacement.StatusCalls);
        replacement.RaiseSkillsChanged();
        await WaitUntilAsync(() => replacement.StatusCalls > calls);
    }

    private static FakeExtensionsClient LifecycleClient() => new()
    {
        AdvertisedFeatures = FeaturesWithPlugins(),
        PluginListResult = new PluginsListResult { MutationAllowed = true },
        PluginSearchResult = new PluginsSearchResult
        {
            Results =
            [
                new PluginSearchEntry
                {
                    Package = new PluginSearchPackage
                    {
                        Name = "@publisher/package",
                        DisplayName = "Plugin",
                        RuntimeId = "publisher.plugin",
                        LatestVersion = "1.2.3",
                    },
                },
            ],
        },
        PluginInspectResult = new PluginInspectResult
        {
            Ok = true,
            Plugin = new PluginInspectEntry
            {
                Id = "publisher.plugin",
                Name = "Plugin",
                Version = "1.2.3",
            },
            Source = new PluginInspectSource
            {
                Kind = "clawhub",
                PackageName = "@publisher/package",
                Integrity = "sha512-exact",
            },
            Declared = new PluginDeclaredSurface
            {
                Tools = ["tool.read"],
                Contracts = ["contract.alpha"],
            },
            ReviewToken = "fresh-review-token",
        },
    };

    private static GatewayRequestException GatewayError(string method, string code, string detailsJson)
    {
        using var document = JsonDocument.Parse(detailsJson);
        return (GatewayRequestException)Activator.CreateInstance(
            typeof(GatewayRequestException),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [method, "rejected", code, (JsonElement?)document.RootElement.Clone()],
            culture: null)!;
    }

    private static GatewayFeatureSet FeaturesWithPlugins() => new(
        [
            "skills.status", "skills.securityVerdicts",
            "plugins.list", "plugins.search", "plugins.inspect", "plugins.install",
            "plugins.setEnabled", "plugins.uninstall",
        ],
        []);

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
        public SkillsDetailResult DetailResult { get; set; } = new()
        {
            Skill = new ClawHubSkillDetail(),
        };
        public SkillsSecurityVerdictsResult SecurityResult { get; set; } = new();
        public Func<string, Task<SkillsDetailResult>>? DetailHandler { get; set; }
        public PluginsListResult PluginListResult { get; set; } = new();
        public PluginsSearchResult PluginSearchResult { get; set; } = new();
        public PluginInspectResult PluginInspectResult { get; set; } = new();
        public Func<Task<PluginsListResult>>? PluginListHandler { get; set; }
        public Func<string, int, Task<PluginsSearchResult>>? PluginSearchHandler { get; set; }
        public Func<string, Task<PluginInspectResult>>? PluginInspectHandler { get; set; }
        public Func<PluginInstallRequest, Task<PluginMutationResult>> PluginInstallHandler { get; set; } =
            _ => Task.FromResult(new PluginMutationResult { Ok = true });
        public PluginInstallRequest? LastPluginInstallRequest { get; private set; }
        public PluginSetEnabledRequest? LastPluginSetEnabledRequest { get; private set; }
        public string? LastUninstalledPluginId { get; private set; }
        public string? LastInspectPluginId { get; private set; }
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

        private EventHandler? _skillsChanged;
        public int SkillsChangedSubscriberCount { get; private set; }
        public event EventHandler? SkillsChanged
        {
            add
            {
                _skillsChanged += value;
                SkillsChangedSubscriberCount++;
            }
            remove
            {
                _skillsChanged -= value;
                SkillsChangedSubscriberCount--;
            }
        }
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
            Task.FromResult(SecurityResult);
        public Task<SkillsSearchResult> SearchSkillsAsync(string? query = null, int limit = 20, int timeoutMs = 15000) =>
            Task.FromResult(SearchResult);
        public Task<SkillsDetailResult> GetSkillDetailAsync(string installReference, int timeoutMs = 15000) =>
            DetailHandler?.Invoke(installReference) ?? Task.FromResult(DetailResult);
        public Task<SkillMutationResult> InstallClawHubSkillAsync(ClawHubSkillInstallRequest request, int timeoutMs = 120000)
        {
            LastInstallRequest = request;
            return Task.FromResult(new SkillMutationResult { Ok = true });
        }
        public Task<SkillMutationResult> UpdateClawHubSkillAsync(ClawHubSkillUpdateRequest request, int timeoutMs = 120000) =>
            Task.FromResult(new SkillMutationResult { Ok = true });
        public Task<SkillMutationResult> SetSkillEnabledDetailedAsync(string skillKey, bool enabled, int timeoutMs = 15000) =>
            Task.FromResult(new SkillMutationResult { Ok = true });
        public Task<PluginsListResult> ListPluginsAsync(int timeoutMs = 15000) =>
            PluginListHandler?.Invoke() ?? Task.FromResult(PluginListResult);
        public Task<PluginsSearchResult> SearchPluginsAsync(string query, int limit = 20, int timeoutMs = 15000) =>
            PluginSearchHandler?.Invoke(query, limit) ?? Task.FromResult(PluginSearchResult);
        public Task<PluginInspectResult> InspectPluginAsync(string pluginId, int timeoutMs = 15000)
        {
            LastInspectPluginId = pluginId;
            return PluginInspectHandler?.Invoke(pluginId) ?? Task.FromResult(PluginInspectResult);
        }
        public Task<PluginMutationResult> InstallPluginAsync(PluginInstallRequest request, int timeoutMs = 120000)
        {
            LastPluginInstallRequest = request;
            return PluginInstallHandler(request);
        }
        public Task<PluginMutationResult> SetPluginEnabledAsync(PluginSetEnabledRequest request, int timeoutMs = 30000)
        {
            LastPluginSetEnabledRequest = request;
            return Task.FromResult(new PluginMutationResult { Ok = true });
        }
        public Task<PluginMutationResult> UninstallPluginAsync(string pluginId, int timeoutMs = 120000)
        {
            LastUninstalledPluginId = pluginId;
            return Task.FromResult(new PluginMutationResult { Ok = true });
        }
        public void RaiseSkillsChanged() => _skillsChanged?.Invoke(this, EventArgs.Empty);
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
