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
    bool CanUninstall)
{
    public string ToggleLabel { get; init; } = string.Empty;
}

internal sealed record PluginSearchItemPresentation(
    string PackageName,
    string Name,
    string Summary,
    string Version,
    string Verification,
    string? RuntimeId,
    bool IsOfficial,
    bool CanReview,
    bool CanInstall);

internal sealed record PluginReviewPresentation(
    string PluginId,
    string Name,
    string Description,
    string Version,
    string Origin,
    string DeclaredSurfaces,
    string Trust,
    string ReviewToken,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient,
    PluginSearchItemPresentation? SearchItem = null,
    PluginListItemPresentation? InstalledItem = null);

internal sealed record PluginCapabilityPrompt(
    string PluginId,
    string DeclaredSurfaces,
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
    private IReadOnlyList<PluginListItemPresentation> _installedPlugins = [];
    private IReadOnlyList<PluginSearchItemPresentation> _pluginSearchResults = [];
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
                InstalledPlugins = [];
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
                InstalledPlugins = [];
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
                    canMutate && client.AdvertisedFeatures.SupportsMethod("plugins.setEnabled"),
                    canMutate && plugin.Removable && client.AdvertisedFeatures.SupportsMethod("plugins.uninstall"))
                {
                    ToggleLabel = _runtime.GetText(plugin.Enabled
                        ? "ExtensionsPage_DisableAction"
                        : "ExtensionsPage_EnableAction"),
                })
                .ToArray();
            ApplyPluginIfCurrent(generation, client, () =>
            {
                if (client.ConnectionEpoch != epoch)
                    return;
                PluginsSupported = result.IsSupported;
                PluginMutationAllowed = result.MutationAllowed;
                PluginDiagnosticCount = result.DiagnosticCount;
                InstalledPlugins = rows;
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
                PluginErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_PluginLoadFormat",
                    TokenSanitizer.Sanitize(ex.Message));
            });
        }
    }

    public async Task SearchPluginsAsync(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginSearchRequired");
            return;
        }

        var client = _runtime.CurrentClient;
        Dispatch(() =>
        {
            IsSearchingPlugins = true;
            PluginErrorMessage = null;
            PluginStatusMessage = null;
        });
        if (client is null || !client.IsConnectedToGateway)
        {
            Dispatch(() =>
            {
                IsSearchingPlugins = false;
                PluginErrorMessage = _runtime.GetText("ExtensionsPage_Error_Disconnected");
            });
            return;
        }
        if (!client.AdvertisedFeatures.SupportsMethod("plugins.search"))
        {
            Dispatch(() =>
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
            var rows = result.Results.Select(entry => new PluginSearchItemPresentation(
                    entry.Package.Name,
                    string.IsNullOrWhiteSpace(entry.Package.DisplayName)
                        ? entry.Package.Name
                        : entry.Package.DisplayName,
                    entry.Package.Summary ?? string.Empty,
                    entry.Package.LatestVersion ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
                    entry.Package.VerificationTier ?? _runtime.GetText("ExtensionsPage_TrustUnknown"),
                    entry.Package.RuntimeId,
                    entry.Package.IsOfficial,
                    canInspect && !string.IsNullOrWhiteSpace(entry.Package.RuntimeId),
                    HasAdminScope(client) && PluginMutationAllowed &&
                        client.AdvertisedFeatures.SupportsMethod("plugins.install") &&
                        !string.IsNullOrWhiteSpace(entry.Package.Name)))
                .ToArray();
            Dispatch(() =>
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
            Dispatch(() =>
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
        var review = await ReviewPluginByIdAsync(item.PluginId).ConfigureAwait(false);
        return review is null ? null : review with { InstalledItem = item };
    }

    public async Task<PluginReviewPresentation?> ReviewPluginAsync(PluginSearchItemPresentation item)
    {
        if (string.IsNullOrWhiteSpace(item.RuntimeId))
            return null;
        var review = await ReviewPluginByIdAsync(item.RuntimeId).ConfigureAwait(false);
        return review is null ? null : review with { SearchItem = item };
    }

    private async Task<PluginReviewPresentation?> ReviewPluginByIdAsync(string pluginId)
    {
        var client = _runtime.CurrentClient;
        if (client is null || !client.AdvertisedFeatures.SupportsMethod("plugins.inspect"))
        {
            PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginsUpgradeRequired");
            return null;
        }

        try
        {
            var result = await client.InspectPluginAsync(pluginId).ConfigureAwait(false);
            if (!result.IsSupported || !result.Ok)
            {
                Dispatch(() => PluginStatusMessage = _runtime.GetText("ExtensionsPage_PluginInspectUnavailable"));
                return null;
            }
            return new PluginReviewPresentation(
                result.Plugin.Id,
                string.IsNullOrWhiteSpace(result.Plugin.Name) ? result.Plugin.Id : result.Plugin.Name,
                result.Plugin.Description ?? string.Empty,
                result.Plugin.Version ?? _runtime.GetText("ExtensionsPage_VersionUnknown"),
                result.Plugin.Origin ?? result.Source?.Kind ?? _runtime.GetText("ExtensionsPage_OriginUnknown"),
                FormatDeclaredSurfaces(result.Declared),
                FormatPluginTrust(result.Trust),
                result.ReviewToken,
                client.ConnectionEpoch,
                client);
        }
        catch (Exception ex)
        {
            Dispatch(() => PluginErrorMessage = _runtime.FormatText(
                "ExtensionsPage_Error_PluginInspectFormat",
                TokenSanitizer.Sanitize(ex.Message)));
            return null;
        }
    }

    private string FormatDeclaredSurfaces(PluginDeclaredSurface declared)
    {
        var lines = new List<string>();
        AddSurface(lines, "ExtensionsPage_PluginSurfaceChannels", declared.Channels);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceProviders", declared.Providers);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceTools", declared.Tools);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceHooks", declared.Hooks);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceMcpServers", declared.McpServers);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceSkills", declared.Skills);
        AddSurface(lines, "ExtensionsPage_PluginSurfaceCli", declared.CliCommands.Concat(declared.CliBackends));
        AddSurface(lines, "ExtensionsPage_PluginSurfaceConfig", declared.DangerousConfigFlags);
        return lines.Count == 0
            ? _runtime.GetText("ExtensionsPage_PluginNoDeclaredSurfaces")
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
            : trust.Disposition;
        return trust.Reasons.Count == 0
            ? disposition
            : disposition + ": " + string.Join(" ", trust.Reasons);
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

        var request = PluginInstallRequest.FromClawHub(item.PackageName) with
        {
            Version = IsKnownVersion(item.Version) ? item.Version : null,
            AcknowledgeCapabilities = acknowledgement,
            AcknowledgeInstallPolicyWarning = acknowledgeInstallPolicyWarning,
        };
        try
        {
            var result = await client.InstallPluginAsync(request).ConfigureAwait(false);
            return await CompletePluginMutationAsync(
                client,
                result,
                "ExtensionsPage_PluginInstalled").ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (PluginCapabilityConsentDetails.TryParse(ex, out var consent))
        {
            return await BuildCapabilityOutcomeAsync(client, consent!).ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (InstallPolicyWarningDetails.TryParse(ex, out var policy))
        {
            return BuildInstallPolicyOutcome(policy!);
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(client, expectRestart: true).ConfigureAwait(false);
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
                result,
                item.Enabled ? "ExtensionsPage_PluginDisabled" : "ExtensionsPage_PluginEnabled").ConfigureAwait(false);
        }
        catch (GatewayRequestException ex) when (PluginCapabilityConsentDetails.TryParse(ex, out var consent))
        {
            return await BuildCapabilityOutcomeAsync(client, consent!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(client, expectRestart: true).ConfigureAwait(false);
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
                result,
                "ExtensionsPage_PluginUninstalled").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await WaitForPluginReconnectAndRefreshAsync(client, expectRestart: true).ConfigureAwait(false);
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
        PluginMutationResult result,
        string successKey)
    {
        if (!result.IsSupported)
            return Failure("ExtensionsPage_PluginsUpgradeRequired");
        if (!result.Ok)
            return new(false, _runtime.GetText("ExtensionsPage_ActionFailed"));

        var recovered = await WaitForPluginReconnectAndRefreshAsync(
            client,
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
        PluginCapabilityConsentDetails consent)
    {
        if (client.ConnectionEpoch != _runtime.CurrentClient?.ConnectionEpoch)
            return Failure("ExtensionsPage_PluginReviewExpired");
        try
        {
            var inspected = await client.InspectPluginAsync(consent.PluginId).ConfigureAwait(false);
            if (!inspected.Ok || client.ConnectionEpoch != _runtime.CurrentClient?.ConnectionEpoch)
                return Failure("ExtensionsPage_PluginInspectUnavailable");
            var prompt = new PluginCapabilityPrompt(
                consent.PluginId,
                FormatDeclaredSurfaces(inspected.Declared),
                FormatDeclaredSurfaces(consent.Widened),
                new PluginCapabilityAcknowledgement(consent.ReviewToken, client.ConnectionEpoch));
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
                TokenSanitizer.Sanitize(policy.Reason),
                findings));
    }

    private async Task<bool> WaitForPluginReconnectAndRefreshAsync(
        IOperatorGatewayClient initialClient,
        bool expectRestart)
    {
        var recovered = !expectRestart;
        if (expectRestart)
        {
            var initialEpoch = initialClient.ConnectionEpoch;
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
}
