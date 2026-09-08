using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal sealed record PluginListItemPresentation(
    string PluginId,
    string Name,
    string Description,
    string Version,
    string State,
    string Kinds,
    bool Enabled,
    bool Removable,
    bool CanReview,
    bool CanSetEnabled,
    bool CanUninstall,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient)
{
    public string ToggleLabel { get; init; } = string.Empty;
    public override string ToString() => Name;
}

internal sealed record PluginSearchItemPresentation(
    PluginInstallSource? InstallSource,
    string? PackageName,
    string? OfficialPluginId,
    string Name,
    string Summary,
    string Version,
    string Verification,
    string? RuntimeId,
    bool IsOfficial,
    bool CanReview,
    bool CanInstall,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient)
{
    public override string ToString() => Name;
}

internal sealed record PluginReviewPresentation(
    string PluginId,
    string Name,
    string Description,
    string Version,
    string Origin,
    string InstallIdentity,
    string Integrity,
    string DeclaredSurfaces,
    string GrantedAccess,
    string Trust,
    string ReviewToken,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient,
    PluginSearchItemPresentation? SearchItem = null,
    PluginListItemPresentation? InstalledItem = null);

internal sealed record PluginCapabilityPrompt(
    PluginReviewPresentation Review,
    string WidenedSurfaces,
    PluginCapabilityAcknowledgement Acknowledgement);

internal sealed record PluginInstallPolicyPrompt(
    string Reason,
    string Findings);

internal sealed record PluginActionOutcome(
    bool Succeeded,
    string Message,
    bool RestartExpected = false,
    PluginCapabilityPrompt? CapabilityPrompt = null,
    PluginInstallPolicyPrompt? InstallPolicyPrompt = null);

internal sealed partial class ExtensionsPageViewModel
{
    private long _pluginLoadGeneration;
    private long _pluginSearchGeneration;
    private long _pluginReviewGeneration;
    private string _activePluginQuery = string.Empty;
    private IReadOnlyList<PluginListItemPresentation> _installedPlugins = [];
    private IReadOnlyList<PluginSearchItemPresentation> _pluginSearchResults = [];
    private IReadOnlyList<PluginSearchItemPresentation> _catalogPlugins = [];
    private bool _isLoadingPlugins;
    private bool _isSearchingPlugins;
    private bool _pluginsSupported = true;
    private bool _pluginMutationAllowed;
    private int _pluginDiagnosticCount;
    private string? _pluginStatusMessage;
    private string? _pluginErrorMessage;

    public IReadOnlyList<PluginListItemPresentation> InstalledPlugins
    {
        get => _installedPlugins;
        private set
        {
            if (SetField(ref _installedPlugins, value))
            {
                OnPropertyChanged(nameof(HasInstalledPlugins));
                OnPropertyChanged(nameof(PluginCountText));
            }
        }
    }

    public IReadOnlyList<PluginSearchItemPresentation> PluginSearchResults
    {
        get => _pluginSearchResults;
        private set
        {
            if (SetField(ref _pluginSearchResults, value))
                OnPropertyChanged(nameof(HasPluginSearchResults));
        }
    }

    public bool IsLoadingPlugins
    {
        get => _isLoadingPlugins;
        private set => SetField(ref _isLoadingPlugins, value);
    }

    public bool IsSearchingPlugins
    {
        get => _isSearchingPlugins;
        private set => SetField(ref _isSearchingPlugins, value);
    }

    public bool PluginsSupported
    {
        get => _pluginsSupported;
        private set => SetField(ref _pluginsSupported, value);
    }

    public bool PluginMutationAllowed
    {
        get => _pluginMutationAllowed;
        private set => SetField(ref _pluginMutationAllowed, value);
    }

    public int PluginDiagnosticCount
    {
        get => _pluginDiagnosticCount;
        private set => SetField(ref _pluginDiagnosticCount, value);
    }

    public string? PluginStatusMessage
    {
        get => _pluginStatusMessage;
        private set => SetField(ref _pluginStatusMessage, value);
    }

    public string? PluginErrorMessage
    {
        get => _pluginErrorMessage;
        private set => SetField(ref _pluginErrorMessage, value);
    }

    public bool HasInstalledPlugins => InstalledPlugins.Count > 0;
    public bool HasPluginSearchResults => PluginSearchResults.Count > 0;
    public string PluginCountText => _runtime.FormatText(
        "ExtensionsPage_PluginCountFormat",
        InstalledPlugins.Count);

    public async Task LoadPluginsAsync()
    {
        var generation = Interlocked.Increment(ref _pluginLoadGeneration);
        var client = _runtime.CurrentClient;
        Dispatch(() =>
        {
            IsLoadingPlugins = true;
            PluginErrorMessage = null;
            PluginStatusMessage = null;
            PluginsSupported = true;
        });

        if (client is null || !client.IsConnectedToGateway)
        {
            ApplyPluginIfCurrent(generation, client, () =>
            {
                IsLoadingPlugins = false;
                PluginErrorMessage = _runtime.GetText("ExtensionsPage_Error_Disconnected");
                ClearPluginSnapshot();
            });
            return;
        }

        if (!client.AdvertisedFeatures.SupportsMethod("plugins.list"))
        {
            ApplyPluginIfCurrent(generation, client, () =>
            {
                IsLoadingPlugins = false;
                PluginsSupported = false;
                PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginsUpgradeRequired");
                ClearPluginSnapshot();
            });
            return;
        }

        var epoch = client.ConnectionEpoch;
        try
        {
            var result = await client.ListPluginsAsync().ConfigureAwait(false);
            var canInspect = client.AdvertisedFeatures.SupportsMethod("plugins.inspect");
            var canMutate = HasAdminScope(client) && result.MutationAllowed;
            var rows = result.Plugins
                .Where(static plugin => plugin.Installed)
                .OrderBy(static plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(plugin => new PluginListItemPresentation(
                    plugin.Id,
                    string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
                    plugin.Description ?? string.Empty,
                    plugin.Version ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
                    string.IsNullOrWhiteSpace(plugin.State)
                        ? _runtime.GetText("ExtensionsPage_StateUnknown")
                        : plugin.State,
                    plugin.Kind.Count == 0 ? _runtime.GetText("ExtensionsPage_KindUnknown") : string.Join(", ", plugin.Kind),
                    plugin.Enabled,
                    plugin.Removable,
                    canInspect,
                    canInspect && canMutate && client.AdvertisedFeatures.SupportsMethod("plugins.setEnabled"),
                    canInspect && canMutate && plugin.Removable &&
                        client.AdvertisedFeatures.SupportsMethod("plugins.uninstall"),
                    epoch,
                    client)
                {
                    ToggleLabel = _runtime.GetText(plugin.Enabled
                        ? "ExtensionsPage_DisableAction"
                        : "ExtensionsPage_EnableAction"),
                })
                .ToArray();
            var catalogRows = result.Plugins
                .Where(static plugin => !plugin.Installed && plugin.Install is not null)
                .OrderByDescending(static plugin => plugin.Featured)
                .ThenBy(static plugin => plugin.Order ?? double.MaxValue)
                .ThenBy(static plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(plugin => BuildCatalogPluginRow(plugin, client, epoch, canInspect, canMutate))
                .ToArray();
            ApplyPluginIfCurrent(generation, client, () =>
            {
                if (client.ConnectionEpoch != epoch)
                    return;
                PluginsSupported = result.IsSupported;
                PluginMutationAllowed = result.MutationAllowed;
                PluginDiagnosticCount = result.DiagnosticCount;
                InstalledPlugins = rows;
                _catalogPlugins = catalogRows;
                if (_activePluginQuery.Length == 0)
                    PluginSearchResults = catalogRows;
                IsLoadingPlugins = false;
                if (result.DiagnosticCount > 0)
                {
                    PluginStatusMessage = _runtime.FormatText(
                        "ExtensionsPage_PluginDiagnosticsFormat",
                        result.DiagnosticCount);
                }
            });
        }
        catch (Exception ex)
        {
            ApplyPluginIfCurrent(generation, client, () =>
            {
                IsLoadingPlugins = false;
                ClearPluginSnapshot();
                PluginErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_PluginLoadFormat",
                    TokenSanitizer.Sanitize(ex.Message));
            });
        }
    }

    public async Task SearchPluginsAsync(string? query)
    {
        var generation = Interlocked.Increment(ref _pluginSearchGeneration);
        var trimmed = query?.Trim() ?? string.Empty;
        _activePluginQuery = trimmed;
        var client = _runtime.CurrentClient;
        var epoch = client?.ConnectionEpoch ?? 0;
        if (trimmed.Length == 0)
        {
            ApplyPluginSearchIfCurrent(generation, client, epoch, () =>
            {
                IsSearchingPlugins = false;
                PluginErrorMessage = null;
                PluginSearchResults = _catalogPlugins;
                PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginSearchRequired");
            });
            return;
        }

        Dispatch(() =>
        {
            IsSearchingPlugins = true;
            PluginSearchResults = [];
            PluginErrorMessage = null;
            PluginStatusMessage = null;
        });
        if (client is null || !client.IsConnectedToGateway)
        {
            ApplyPluginSearchIfCurrent(generation, client, epoch, () =>
            {
                IsSearchingPlugins = false;
                PluginErrorMessage = _runtime.GetText("ExtensionsPage_Error_Disconnected");
            });
            return;
        }
        if (!client.AdvertisedFeatures.SupportsMethod("plugins.search"))
        {
            ApplyPluginSearchIfCurrent(generation, client, epoch, () =>
            {
                IsSearchingPlugins = false;
                PluginsSupported = false;
                PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginsUpgradeRequired");
            });
            return;
        }

        try
        {
            var result = await client.SearchPluginsAsync(trimmed, 30).ConfigureAwait(false);
            var canInspect = client.AdvertisedFeatures.SupportsMethod("plugins.inspect");
            var canInstall = HasAdminScope(client) && PluginMutationAllowed &&
                client.AdvertisedFeatures.SupportsMethod("plugins.install") && canInspect;
            var rows = result.Results.Select(entry => new PluginSearchItemPresentation(
                    PluginInstallSource.ClawHub,
                    entry.Package.Name,
                    null,
                    string.IsNullOrWhiteSpace(entry.Package.DisplayName)
                        ? entry.Package.Name
                        : entry.Package.DisplayName,
                    entry.Package.Summary ?? string.Empty,
                    entry.Package.LatestVersion ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
                    entry.Package.VerificationTier ?? _runtime.GetText("ExtensionsPage_TrustUnknown"),
                    entry.Package.RuntimeId,
                    entry.Package.IsOfficial,
                    (canInspect && !string.IsNullOrWhiteSpace(entry.Package.RuntimeId)) || canInstall,
                    canInstall && !string.IsNullOrWhiteSpace(entry.Package.Name),
                    epoch,
                    client))
                .ToArray();
            ApplyPluginSearchIfCurrent(generation, client, epoch, () =>
            {
                PluginSearchResults = rows;
                IsSearchingPlugins = false;
                if (!result.IsSupported)
                    PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginsUpgradeRequired");
                else if (rows.Length == 0)
                    PluginStatusMessage = _runtime.GetText("ExtensionsPage_NoPluginSearchResults");
            });
        }
        catch (Exception ex)
        {
            ApplyPluginSearchIfCurrent(generation, client, epoch, () =>
            {
                IsSearchingPlugins = false;
                PluginErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_PluginSearchFormat",
                    TokenSanitizer.Sanitize(ex.Message));
            });
        }
    }

    public async Task<PluginReviewPresentation?> ReviewPluginAsync(PluginListItemPresentation item)
    {
        var reviewGeneration = Interlocked.Increment(ref _pluginReviewGeneration);
        var client = _runtime.CurrentClient;
        if (client is null || !ReferenceEquals(client, item.ConnectionClient) ||
            client.ConnectionEpoch != item.ConnectionEpoch)
        {
            PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginReviewExpired");
            return null;
        }
        var review = await ReviewPluginByIdAsync(
                item.PluginId,
                client,
                item.ConnectionEpoch,
                reviewGeneration)
            .ConfigureAwait(false);
        return review is null ? null : review with { InstalledItem = item };
    }

    public async Task<PluginReviewPresentation?> ReviewPluginAsync(PluginSearchItemPresentation item)
    {
        var reviewGeneration = Interlocked.Increment(ref _pluginReviewGeneration);
        var client = _runtime.CurrentClient;
        if (client is null || !ReferenceEquals(client, item.ConnectionClient) ||
            client.ConnectionEpoch != item.ConnectionEpoch)
        {
            PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginReviewExpired");
            return null;
        }

        if (item.InstallSource == PluginInstallSource.Official &&
            !string.IsNullOrWhiteSpace(item.RuntimeId) &&
            client.AdvertisedFeatures.SupportsMethod("plugins.inspect"))
        {
            var officialReview = await ReviewPluginByIdAsync(
                item.RuntimeId,
                client,
                item.ConnectionEpoch,
                reviewGeneration).ConfigureAwait(false);
            return officialReview is null ? null : officialReview with { SearchItem = item };
        }

        if (item.CanInstall)
        {
            var installIdentity = item.InstallSource switch
            {
                PluginInstallSource.ClawHub => item.PackageName,
                PluginInstallSource.Official => item.OfficialPluginId,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(installIdentity))
                return null;
            return new PluginReviewPresentation(
                item.RuntimeId ?? item.OfficialPluginId ?? item.PackageName!,
                item.Name,
                item.Summary,
                item.Version,
                _runtime.GetText("ExtensionsPage_ClawHubCatalogOrigin"),
                installIdentity,
                _runtime.GetText("ExtensionsPage_IntegrityPending"),
                _runtime.GetText("ExtensionsPage_PluginSurfacesPendingInstall"),
                _runtime.GetText("ExtensionsPage_PluginGrantsPendingInstall"),
                item.Verification,
                string.Empty,
                item.ConnectionEpoch,
                client,
                SearchItem: item);
        }

        if (string.IsNullOrWhiteSpace(item.RuntimeId))
            return null;
        var review = await ReviewPluginByIdAsync(
                item.RuntimeId,
                client,
                item.ConnectionEpoch,
                reviewGeneration)
            .ConfigureAwait(false);
        return review is null ? null : review with { SearchItem = item };
    }

    private async Task<PluginReviewPresentation?> ReviewPluginByIdAsync(
        string pluginId,
        IOperatorGatewayClient client,
        long epoch,
        long reviewGeneration)
    {
        if (!ReferenceEquals(client, _runtime.CurrentClient) ||
            client.ConnectionEpoch != epoch ||
            !client.AdvertisedFeatures.SupportsMethod("plugins.inspect"))
        {
            PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginsUpgradeRequired");
            return null;
        }

        try
        {
            var result = await client.InspectPluginAsync(pluginId).ConfigureAwait(false);
            if (reviewGeneration != Volatile.Read(ref _pluginReviewGeneration) ||
                !ReferenceEquals(client, _runtime.CurrentClient) || client.ConnectionEpoch != epoch)
                return null;
            if (!result.IsSupported || !result.Ok)
            {
                ApplyPluginReviewIfCurrent(reviewGeneration, client, epoch, () =>
                    PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginInspectUnavailable"));
                return null;
            }
            return new PluginReviewPresentation(
                result.Plugin.Id,
                string.IsNullOrWhiteSpace(result.Plugin.Name) ? result.Plugin.Id : result.Plugin.Name,
                result.Plugin.Description ?? string.Empty,
                result.Plugin.Version ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
                result.Plugin.Origin ?? result.Source?.Kind ?? _runtime.GetText("ExtensionsPage_OriginUnknown"),
                result.Source?.PackageName ?? result.Source?.Spec ?? result.Plugin.Id,
                result.Source?.Integrity ?? _runtime.GetText("ExtensionsPage_IntegrityUnavailable"),
                FormatDeclaredSurfaces(result.Declared),
                FormatOperatorGrants(result.Grants),
                FormatPluginTrust(result.Trust),
                result.ReviewToken,
                client.ConnectionEpoch,
                client);
        }
        catch (Exception ex)
        {
            ApplyPluginReviewIfCurrent(reviewGeneration, client, epoch, () =>
                PluginErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_PluginInspectFormat",
                    TokenSanitizer.Sanitize(ex.Message)));
            return null;
        }
    }

    private PluginSearchItemPresentation BuildCatalogPluginRow(
        PluginCatalogEntry plugin,
        IOperatorGatewayClient client,
        long epoch,
        bool canInspect,
        bool canMutate)
    {
        var source = plugin.Install?.Source switch
        {
            "clawhub" => PluginInstallSource.ClawHub,
            "official" => PluginInstallSource.Official,
            _ => (PluginInstallSource?)null,
        };
        var packageName = plugin.Install?.PackageName ?? plugin.PackageName;
        var officialPluginId = plugin.Install?.PluginId ?? plugin.Id;
        var hasExactInstallIdentity = source switch
        {
            PluginInstallSource.ClawHub => !string.IsNullOrWhiteSpace(packageName),
            PluginInstallSource.Official => !string.IsNullOrWhiteSpace(officialPluginId),
            _ => false,
        };
        var canInstall = canMutate && hasExactInstallIdentity &&
            client.AdvertisedFeatures.SupportsMethod("plugins.install") && canInspect;
        return new PluginSearchItemPresentation(
            source,
            packageName,
            source == PluginInstallSource.Official ? officialPluginId : null,
            string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name,
            plugin.Description ?? string.Empty,
            plugin.Version ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
            source == PluginInstallSource.Official
                ? _runtime.GetText("ExtensionsPage_OfficialCatalogVerification")
                : _runtime.GetText("ExtensionsPage_TrustUnknown"),
            plugin.Id,
            source == PluginInstallSource.Official,
            (canInspect && !string.IsNullOrWhiteSpace(plugin.Id)) || canInstall,
            canInstall,
            epoch,
            client);
    }

    private string FormatDeclaredSurfaces(PluginDeclaredSurface declared)
    {
        var lines = new List<string>();
        AddSurface(lines, "ExtensionsPage_PluginSurfaceChannels", declared.Channels);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceProviders", declared.Providers);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceTools", declared.Tools);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceContracts", declared.Contracts);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceHooks", declared.Hooks);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceMcpServers", declared.McpServers);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceSkills", declared.Skills);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceCli", declared.CliCommands.Concat(declared.CliBackends));
        AddSurface(lines, "ExtensionsPage_PluginSurfaceConfig", declared.DangerousConfigFlags);
        return lines.Count == 0
            ? _runtime.GetText("ExtensionsPage_PluginNoDeclaredSurfaces")
            : string.Join(Environment.NewLine, lines);
    }

    private string FormatOperatorGrants(PluginOperatorGrants grants)
    {
        var lines = new List<string>();
        if (grants.Hooks.AllowPromptInjection.Effective)
            lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantPromptInjection"));
        if (grants.Hooks.AllowConversationAccess.Effective)
            lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantConversationAccess"));

        if (grants.Llm is { } llm)
        {
            if (llm.AllowModelOverride == true)
                lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantModelOverride"));
            AddSurface(lines, "ExtensionsPage_PluginGrantAllowedModels", llm.AllowedModels);
            AddSurface(lines, "ExtensionsPage_PluginGrantAllowedCompletionModels", llm.AllowedCompletionModels);
            if (llm.AllowAuthProfileOverride == true)
                lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantAuthProfileOverride"));
            if (llm.AllowAgentIdOverride == true)
                lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantAgentOverride"));
        }

        if (grants.Subagent is { } subagent)
        {
            if (subagent.AllowModelOverride == true)
                lines.Add(_runtime.GetText("ExtensionsPage_PluginGrantSubagentModelOverride"));
            AddSurface(lines, "ExtensionsPage_PluginGrantSubagentModels", subagent.AllowedModels);
        }

        return lines.Count == 0
            ? _runtime.GetText("ExtensionsPage_PluginNoEffectiveGrants")
            : string.Join(Environment.NewLine, lines);
    }

    private void AddSurface(List<string> lines, string key, IEnumerable<string> values)
    {
        var exact = values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (exact.Length > 0)
            lines.Add(_runtime.FormatText(key, string.Join(", ", exact)));
    }

    private string FormatPluginTrust(PluginInstallTrust? trust)
    {
        if (trust is null)
            return _runtime.GetText("ExtensionsPage_TrustUnknown");
        var disposition = string.IsNullOrWhiteSpace(trust.Disposition)
            ? _runtime.GetText("ExtensionsPage_TrustUnknown")
            : TokenSanitizer.Sanitize(trust.Disposition);
        var summary = trust.Reasons.Count == 0
            ? disposition
            : disposition + ": " + string.Join(" ", trust.Reasons.Select(static reason =>
                TokenSanitizer.Sanitize(reason)));
        if (trust.Pending)
            summary += " " + _runtime.GetText("ExtensionsPage_PluginTrustPending");
        if (trust.Stale)
            summary += " " + _runtime.GetText("ExtensionsPage_PluginTrustStale");
        return summary;
    }

    public async Task<PluginActionOutcome> InstallPluginAsync(
        PluginReviewPresentation review,
        PluginCapabilityAcknowledgement? acknowledgement = null,
        bool acknowledgeInstallPolicyWarning = false)
    {
        ArgumentNullException.ThrowIfNull(review);
        var item = review.SearchItem;
        var client = _runtime.CurrentClient;
        if (item is null || client is null || !ReferenceEquals(review.ConnectionClient, client) ||
            review.ConnectionEpoch != client.ConnectionEpoch || !HasAdminScope(client) || !PluginMutationAllowed)
            return Failure("ExtensionsPage_PluginMutationUnavailable");
        if (!client.AdvertisedFeatures.SupportsMethod("plugins.install"))
            return Failure("ExtensionsPage_PluginsUpgradeRequired");
        if (!client.AdvertisedFeatures.SupportsMethod("plugins.inspect"))
            return Failure("ExtensionsPage_PluginsUpgradeRequired");

        var request = item.InstallSource switch
        {
            PluginInstallSource.ClawHub when !string.IsNullOrWhiteSpace(item.PackageName) =>
                PluginInstallRequest.FromClawHub(item.PackageName),
            PluginInstallSource.Official when !string.IsNullOrWhiteSpace(item.OfficialPluginId) =>
                PluginInstallRequest.FromOfficialCatalog(item.OfficialPluginId),
            _ => null,
        };
        if (request is null)
            return Failure("ExtensionsPage_PluginMutationUnavailable");
        request = request with
        {
            Version = item.InstallSource == PluginInstallSource.ClawHub && IsKnownVersion(review.Version)
                ? review.Version
                : null,
            AcknowledgeCapabilities = acknowledgement,
            AcknowledgeInstallPolicyWarning = acknowledgeInstallPolicyWarning,
        };
        try
        {
            var result = await client.InstallPluginAsync(request).ConfigureAwait(false);
            return await CompletePluginMutationAsync(
                client,
                review.ConnectionEpoch,
                result,
                "ExtensionsPage_PluginInstalled").ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (PluginCapabilityConsentDetails.TryParse(ex, out var consent))
        {
            return await BuildCapabilityOutcomeAsync(client, review, consent!).ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (InstallPolicyWarningDetails.TryParse(ex, out var policy))
        {
            return IsExpectedInstallPolicyWarning(policy!)
                ? BuildInstallPolicyOutcome(policy!)
                : Failure("ExtensionsPage_PluginReviewExpired");
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(
                client,
                review.ConnectionEpoch,
                expectRestart: true).ConfigureAwait(false);
            return Failure("ExtensionsPage_PluginActionUnconfirmed");
        }
        catch (Exception ex)
        {
            return FailureWithError(ex);
        }
    }

    public async Task<PluginActionOutcome> SetPluginEnabledAsync(
        PluginReviewPresentation review,
        PluginCapabilityAcknowledgement? acknowledgement = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        var item = review.InstalledItem;
        var client = _runtime.CurrentClient;
        if (item is null || client is null || !ReferenceEquals(review.ConnectionClient, client) ||
            review.ConnectionEpoch != client.ConnectionEpoch || !item.CanSetEnabled || !HasAdminScope(client))
            return Failure("ExtensionsPage_PluginMutationUnavailable");
        try
        {
            var result = await client.SetPluginEnabledAsync(new PluginSetEnabledRequest(
                item.PluginId,
                !item.Enabled,
                acknowledgement)).ConfigureAwait(false);
            return await CompletePluginMutationAsync(
                client,
                review.ConnectionEpoch,
                result,
                item.Enabled ? "ExtensionsPage_PluginDisabled" : "ExtensionsPage_PluginEnabled").ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (PluginCapabilityConsentDetails.TryParse(ex, out var consent))
        {
            return await BuildCapabilityOutcomeAsync(client, review, consent!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(
                client,
                review.ConnectionEpoch,
                expectRestart: true).ConfigureAwait(false);
            return Failure("ExtensionsPage_PluginActionUnconfirmed");
        }
        catch (Exception ex)
        {
            return FailureWithError(ex);
        }
    }

    public async Task<PluginActionOutcome> UninstallPluginAsync(PluginReviewPresentation review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var item = review.InstalledItem;
        var client = _runtime.CurrentClient;
        if (item is null || client is null || !ReferenceEquals(review.ConnectionClient, client) ||
            review.ConnectionEpoch != client.ConnectionEpoch || !item.CanUninstall || !HasAdminScope(client))
            return Failure("ExtensionsPage_PluginMutationUnavailable");
        try
        {
            var result = await client.UninstallPluginAsync(item.PluginId).ConfigureAwait(false);
            return await CompletePluginMutationAsync(
                client,
                review.ConnectionEpoch,
                result,
                "ExtensionsPage_PluginUninstalled").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(
                client,
                review.ConnectionEpoch,
                expectRestart: true).ConfigureAwait(false);
            return Failure("ExtensionsPage_PluginActionUnconfirmed");
        }
        catch (Exception ex)
        {
            return FailureWithError(ex);
        }
    }

    public PluginCapabilityAcknowledgement? CreateAcknowledgement(PluginReviewPresentation review)
    {
        var client = _runtime.CurrentClient;
        if (client is null || !ReferenceEquals(review.ConnectionClient, client) ||
            string.IsNullOrWhiteSpace(review.ReviewToken) ||
            client.ConnectionEpoch != review.ConnectionEpoch)
        {
            return null;
        }
        return new PluginCapabilityAcknowledgement(review.ReviewToken, review.ConnectionEpoch);
    }

    private async Task<PluginActionOutcome> CompletePluginMutationAsync(
        IOperatorGatewayClient client,
        long initialEpoch,
        PluginMutationResult result,
        string successKey)
    {
        if (!result.IsSupported)
            return Failure("ExtensionsPage_PluginsUpgradeRequired");
        if (!result.Ok)
            return new(false, _runtime.GetText("ExtensionsPage_ActionFailed"));

        var recovered = await WaitForPluginReconnectAndRefreshAsync(
            client,
            initialEpoch,
            result.RestartRequired).ConfigureAwait(false);
        var baseMessage = _runtime.GetText(successKey);
        var warnings = result.Warnings
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Select(TokenSanitizer.Sanitize)
            .ToArray();
        var message = warnings.Length == 0
            ? baseMessage
            : baseMessage + " " + string.Join(" ", warnings);
        if (result.RestartRequired)
        {
            message += " " + _runtime.GetText(recovered
                ? "ExtensionsPage_GatewayReconnected"
                : "ExtensionsPage_GatewayReconnectPending");
        }
        return new(true, message, result.RestartRequired);
    }

    private async Task<PluginActionOutcome> BuildCapabilityOutcomeAsync(
        IOperatorGatewayClient client,
        PluginReviewPresentation review,
        PluginCapabilityConsentDetails consent)
    {
        if (!ReferenceEquals(client, _runtime.CurrentClient) ||
            client.ConnectionEpoch != review.ConnectionEpoch)
            return Failure("ExtensionsPage_PluginReviewExpired");
        var expectedPluginId = review.InstalledItem?.PluginId ??
            review.SearchItem?.RuntimeId ?? review.SearchItem?.OfficialPluginId;
        if (!string.IsNullOrWhiteSpace(expectedPluginId) &&
            !string.Equals(consent.PluginId, expectedPluginId, StringComparison.Ordinal))
        {
            return Failure("ExtensionsPage_PluginReviewExpired");
        }
        try
        {
            var inspected = await client.InspectPluginAsync(consent.PluginId).ConfigureAwait(false);
            if (!inspected.IsSupported || !inspected.Ok ||
                !ReferenceEquals(client, _runtime.CurrentClient) ||
                client.ConnectionEpoch != review.ConnectionEpoch ||
                string.IsNullOrWhiteSpace(inspected.ReviewToken) ||
                !string.Equals(inspected.Plugin.Id, consent.PluginId, StringComparison.Ordinal))
            {
                return Failure("ExtensionsPage_PluginInspectUnavailable");
            }

            if (review.SearchItem is { InstallSource: PluginInstallSource.ClawHub } searchItem &&
                (string.IsNullOrWhiteSpace(inspected.Source?.PackageName) ||
                 !string.Equals(inspected.Source.PackageName, searchItem.PackageName, StringComparison.Ordinal)))
            {
                return Failure("ExtensionsPage_PluginReviewExpired");
            }
            var inspectedVersion = inspected.Plugin.Version;
            if (IsKnownVersion(review.Version) &&
                (string.IsNullOrWhiteSpace(inspectedVersion) ||
                 !string.Equals(review.Version, inspectedVersion, StringComparison.Ordinal)))
            {
                return Failure("ExtensionsPage_PluginReviewExpired");
            }

            var refreshedReview = review with
            {
                PluginId = inspected.Plugin.Id,
                Name = string.IsNullOrWhiteSpace(inspected.Plugin.Name)
                    ? inspected.Plugin.Id
                    : inspected.Plugin.Name,
                Description = inspected.Plugin.Description ?? review.Description,
                Version = string.IsNullOrWhiteSpace(inspectedVersion) ? review.Version : inspectedVersion,
                Origin = inspected.Plugin.Origin ?? inspected.Source?.Kind ?? review.Origin,
                InstallIdentity = inspected.Source?.PackageName ?? inspected.Source?.Spec ?? review.InstallIdentity,
                Integrity = inspected.Source?.Integrity ?? _runtime.GetText("ExtensionsPage_IntegrityUnavailable"),
                DeclaredSurfaces = FormatDeclaredSurfaces(inspected.Declared),
                GrantedAccess = FormatOperatorGrants(inspected.Grants),
                Trust = FormatPluginTrust(inspected.Trust),
                ReviewToken = inspected.ReviewToken,
            };
            var prompt = new PluginCapabilityPrompt(
                refreshedReview,
                FormatDeclaredSurfaces(consent.Widened),
                new PluginCapabilityAcknowledgement(inspected.ReviewToken, client.ConnectionEpoch));
            return new(
                false,
                _runtime.GetText("ExtensionsPage_PluginCapabilityConsentRequired"),
                CapabilityPrompt: prompt);
        }
        catch (Exception ex)
        {
            return FailureWithError(ex);
        }
    }

    private PluginActionOutcome BuildInstallPolicyOutcome(InstallPolicyWarningDetails policy)
    {
        var findings = policy.Findings.Count == 0
            ? _runtime.GetText("ExtensionsPage_NoPolicyFindings")
            : string.Join(Environment.NewLine, policy.Findings.Select(finding =>
                $"{finding.Severity}: {TokenSanitizer.Sanitize(finding.Message)}"));
        return new(
            false,
            _runtime.GetText("ExtensionsPage_PluginInstallPolicyConsentRequired"),
            InstallPolicyPrompt: new PluginInstallPolicyPrompt(
                _runtime.FormatText(
                    "ExtensionsPage_PluginPolicyTargetFormat",
                    TokenSanitizer.Sanitize(policy.TargetName)) + Environment.NewLine +
                    TokenSanitizer.Sanitize(policy.Reason),
                findings));
    }

    private static bool IsExpectedInstallPolicyWarning(InstallPolicyWarningDetails policy)
    {
        return string.Equals(policy.TargetType, "plugin", StringComparison.Ordinal) &&
            string.Equals(policy.RequestMode, "install", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(policy.TargetName);
    }

    private async Task<bool> WaitForPluginReconnectAndRefreshAsync(
        IOperatorGatewayClient initialClient,
        long initialEpoch,
        bool expectRestart)
    {
        var recovered = !expectRestart;
        if (expectRestart)
        {
            var observedRestart = false;
            for (var attempt = 0; attempt < 10 && _active; attempt++)
            {
                var current = _runtime.CurrentClient;
                if (!ReferenceEquals(current, initialClient) ||
                    current is null || !current.IsConnectedToGateway ||
                    current.ConnectionEpoch != initialEpoch)
                {
                    observedRestart = true;
                    break;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }

            if (observedRestart)
            {
                for (var attempt = 0; attempt < 60 && _active; attempt++)
                {
                    var current = _runtime.CurrentClient;
                    if (current is { IsConnectedToGateway: true, HasHandshakeSnapshot: true })
                    {
                        recovered = true;
                        break;
                    }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        if (_active)
            await LoadPluginsAsync().ConfigureAwait(false);
        return recovered;
    }

    private PluginActionOutcome Failure(string key) => new(false, _runtime.GetText(key));

    private void ClearPluginSnapshot()
    {
        Interlocked.Increment(ref _pluginReviewGeneration);
        _activePluginQuery = string.Empty;
        InstalledPlugins = [];
        _catalogPlugins = [];
        PluginSearchResults = [];
        PluginMutationAllowed = false;
        PluginDiagnosticCount = 0;
        IsSearchingPlugins = false;
        IsLoadingPlugins = false;
    }

    private PluginActionOutcome FailureWithError(Exception exception) => new(
        false,
        _runtime.FormatText(
            "ExtensionsPage_Error_PluginActionFormat",
            TokenSanitizer.Sanitize(exception.Message)));

    private bool IsKnownVersion(string version) =>
        !string.IsNullOrWhiteSpace(version) &&
        !string.Equals(version, _runtime.GetText("ExtensionsPage_VersionUnknown"), StringComparison.Ordinal);

    private void ApplyPluginIfCurrent(
        long generation,
        IOperatorGatewayClient? client,
        Action action) => Dispatch(() =>
    {
        if (!_active || generation != Interlocked.Read(ref _pluginLoadGeneration) ||
            !ReferenceEquals(client, _runtime.CurrentClient))
        {
            return;
        }
        action();
    });

    private void ApplyPluginSearchIfCurrent(
        long generation,
        IOperatorGatewayClient? client,
        long epoch,
        Action action) => Dispatch(() =>
    {
        if (!_active || generation != Volatile.Read(ref _pluginSearchGeneration) ||
            !ReferenceEquals(client, _runtime.CurrentClient) ||
            (client is not null && client.ConnectionEpoch != epoch))
        {
            return;
        }
        action();
    });

    private void ApplyPluginReviewIfCurrent(
        long generation,
        IOperatorGatewayClient client,
        long epoch,
        Action action) => Dispatch(() =>
    {
        if (_active && generation == Volatile.Read(ref _pluginReviewGeneration) &&
            ReferenceEquals(client, _runtime.CurrentClient) &&
            client.ConnectionEpoch == epoch)
        {
            action();
        }
    });
}
