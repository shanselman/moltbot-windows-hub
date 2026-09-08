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
    bool CanReview);

internal sealed record PluginSearchItemPresentation(
    string PackageName,
    string Name,
    string Summary,
    string Version,
    string Verification,
    string? RuntimeId,
    bool IsOfficial,
    bool CanReview);

internal sealed record PluginReviewPresentation(
    string PluginId,
    string Name,
    string Description,
    string Version,
    string Origin,
    string DeclaredSurfaces,
    string Trust,
    string ReviewToken,
    long ConnectionEpoch);

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
                    canInspect))
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
                    canInspect && !string.IsNullOrWhiteSpace(entry.Package.RuntimeId)))
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

    public Task<PluginReviewPresentation?> ReviewPluginAsync(PluginListItemPresentation item) =>
        ReviewPluginByIdAsync(item.PluginId);

    public Task<PluginReviewPresentation?> ReviewPluginAsync(PluginSearchItemPresentation item) =>
        string.IsNullOrWhiteSpace(item.RuntimeId)
            ? Task.FromResult<PluginReviewPresentation?>(null)
            : ReviewPluginByIdAsync(item.RuntimeId);

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
                client.ConnectionEpoch);
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
