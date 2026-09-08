using OpenClaw.Shared;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal enum SkillListFilter
{
    All,
    Ready,
    NeedsSetup,
    Disabled,
}

internal sealed record SkillListItemPresentation(
    string SkillKey,
    string Name,
    string Description,
    string Source,
    SkillReadinessState Readiness,
    string ReadinessLabel,
    string RequirementsSummary,
    string ToggleLabel,
    bool CanToggle,
    bool CanUpdate,
    string? UpdateReference,
    string? SecurityLabel,
    string? SafeSkillUrl,
    string? SafeSecurityAuditUrl,
    string AgentId,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient)
{
    public bool HasSkillUrl => SafeSkillUrl is not null;
    public bool HasSecurityAuditUrl => SafeSecurityAuditUrl is not null;
    public override string ToString() => Name;
}

internal sealed record SkillSearchItemPresentation(
    string Slug,
    string Name,
    string Summary,
    string VersionLabel,
    string? RequestedVersion,
    string TrustLabel,
    string? TrustState,
    string? SafeInstallReference,
    bool InstallOnly,
    bool CanReview,
    bool CanInstall,
    string AgentId,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient)
{
    public override string ToString() => Name;
}

internal sealed record SkillReviewPresentation(
    SkillSearchItemPresentation Item,
    string Publisher,
    string Summary,
    string Version,
    string Metadata,
    string InstallReference,
    string? RequestedVersion,
    string AgentId,
    long ConnectionEpoch,
    IOperatorGatewayClient ConnectionClient,
    bool RequiresUnscannedConfirmation);

internal sealed record SkillActionOutcome(
    bool Succeeded,
    bool RequiresUnscannedConfirmation,
    string Message)
{
    public static SkillActionOutcome NeedsConfirmation(string message) =>
        new(false, true, message);
}

internal sealed partial class ExtensionsPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private const string AdminScope = "operator.admin";
    private readonly IExtensionsRuntimeSource _runtime;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<SkillStatusEntry> _skillSnapshot = [];
    private IReadOnlyList<SkillSecurityVerdict> _securityVerdicts = [];
    private long _loadGeneration;
    private long _skillSearchGeneration;
    private long _skillReviewGeneration;
    private IOperatorGatewayClient? _skillSnapshotClient;
    private long _skillSnapshotEpoch;
    private string _skillSnapshotAgentId = "main";
    private bool _disposed;
    private bool _active;
    private IOperatorGatewayClient? _subscribedClient;

    private IReadOnlyList<string> _agentIds = ["main"];
    private string _selectedAgentId = "main";
    private SkillListFilter _skillFilter;
    private IReadOnlyList<SkillListItemPresentation> _visibleSkills = [];
    private IReadOnlyList<SkillSearchItemPresentation> _skillSearchResults = [];
    private bool _isLoadingSkills;
    private bool _isSearchingSkills;
    private bool _skillsSupported = true;
    private bool _canManageExtensions;
    private string? _statusMessage;
    private string? _errorMessage;
    private long _connectionScopeVersion;

    public ExtensionsPageViewModel(IExtensionsRuntimeSource runtime, IUiDispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsActive => _active;
    internal bool IsDisposed => _disposed;
    internal IExtensionsRuntimeSource Runtime => _runtime;

    public IReadOnlyList<string> AgentIds
    {
        get => _agentIds;
        private set => SetField(ref _agentIds, value);
    }

    public string SelectedAgentId
    {
        get => _selectedAgentId;
        private set => SetField(ref _selectedAgentId, value);
    }

    public SkillListFilter SkillFilter
    {
        get => _skillFilter;
        private set => SetField(ref _skillFilter, value);
    }

    public IReadOnlyList<SkillListItemPresentation> VisibleSkills
    {
        get => _visibleSkills;
        private set
        {
            if (SetField(ref _visibleSkills, value))
            {
                OnPropertyChanged(nameof(SkillCountText));
                OnPropertyChanged(nameof(HasVisibleSkills));
            }
        }
    }

    public IReadOnlyList<SkillSearchItemPresentation> SkillSearchResults
    {
        get => _skillSearchResults;
        private set
        {
            if (SetField(ref _skillSearchResults, value))
                OnPropertyChanged(nameof(HasSkillSearchResults));
        }
    }

    public bool IsLoadingSkills
    {
        get => _isLoadingSkills;
        private set => SetField(ref _isLoadingSkills, value);
    }

    public bool IsSearchingSkills
    {
        get => _isSearchingSkills;
        private set => SetField(ref _isSearchingSkills, value);
    }

    public bool SkillsSupported
    {
        get => _skillsSupported;
        private set => SetField(ref _skillsSupported, value);
    }

    public bool CanManageExtensions
    {
        get => _canManageExtensions;
        private set
        {
            if (SetField(ref _canManageExtensions, value))
                RebuildVisibleSkills();
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public long ConnectionScopeVersion
    {
        get => _connectionScopeVersion;
        private set => SetField(ref _connectionScopeVersion, value);
    }

    public string SkillCountText => _runtime.FormatText(
        "ExtensionsPage_SkillsCountFormat",
        VisibleSkills.Count,
        _skillSnapshot.Count);

    public bool HasVisibleSkills => VisibleSkills.Count > 0;
    public bool HasSkillSearchResults => SkillSearchResults.Count > 0;

    public void Activate(object? parameter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = true;
        AgentIds = _runtime.GetAgentIds();
        SelectedAgentId = ResolveAgentId(parameter, AgentIds);
        _runtime.CurrentClientChanged -= OnRuntimeClientChanged;
        _runtime.CurrentClientChanged += OnRuntimeClientChanged;
        SubscribeToCurrentClient();
        _ = LoadSkillsAsync();
        _ = LoadPluginsAsync();
    }

    public void Deactivate()
    {
        _active = false;
        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _skillSearchGeneration);
        Interlocked.Increment(ref _skillReviewGeneration);
        Interlocked.Increment(ref _pluginLoadGeneration);
        Interlocked.Increment(ref _pluginSearchGeneration);
        Interlocked.Increment(ref _pluginReviewGeneration);
        _runtime.CurrentClientChanged -= OnRuntimeClientChanged;
        UnsubscribeClient();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Deactivate();
        _disposed = true;
    }

    public async Task SelectAgentAsync(string? agentId)
    {
        var resolved = AgentIds.FirstOrDefault(id =>
            string.Equals(id, agentId, StringComparison.OrdinalIgnoreCase)) ?? "main";
        if (string.Equals(SelectedAgentId, resolved, StringComparison.OrdinalIgnoreCase))
            return;
        SelectedAgentId = resolved;
        Interlocked.Increment(ref _skillSearchGeneration);
        ClearSkillSnapshot();
        await LoadSkillsAsync().ConfigureAwait(false);
    }

    public void SetSkillFilter(SkillListFilter filter)
    {
        SkillFilter = filter;
        RebuildVisibleSkills();
    }

    public async Task LoadSkillsAsync()
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        var client = _runtime.CurrentClient;
        var agentId = SelectedAgentId;

        Dispatch(() =>
        {
            IsLoadingSkills = true;
            ErrorMessage = null;
            StatusMessage = null;
            SkillsSupported = true;
            CanManageExtensions = HasAdminScope(client);
        });

        if (client is null || !client.IsConnectedToGateway)
        {
            ApplyIfCurrent(generation, client, agentId, () =>
            {
                IsLoadingSkills = false;
                ErrorMessage = _runtime.GetText("ExtensionsPage_Error_Disconnected");
                ClearSkillSnapshot();
            });
            return;
        }

        if (!client.AdvertisedFeatures.SupportsMethod("skills.status"))
        {
            ApplyIfCurrent(generation, client, agentId, () =>
            {
                IsLoadingSkills = false;
                SkillsSupported = false;
                StatusMessage = _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired");
                ClearSkillSnapshot();
            });
            return;
        }

        var epoch = client.ConnectionEpoch;
        try
        {
            var reportTask = client.GetSkillsStatusAsync(agentId);
            var verdictTask = client.AdvertisedFeatures.SupportsMethod("skills.securityVerdicts")
                ? client.GetSkillSecurityVerdictsAsync(agentId)
                : Task.FromResult(SkillsSecurityVerdictsResult.Unsupported);
            await Task.WhenAll(reportTask, verdictTask).ConfigureAwait(false);
            var report = await reportTask.ConfigureAwait(false);
            var verdicts = await verdictTask.ConfigureAwait(false);

            ApplyIfCurrent(generation, client, agentId, () =>
            {
                if (client.ConnectionEpoch != epoch)
                    return;
                SkillsSupported = report.IsSupported;
                _skillSnapshot = report.Skills
                    .OrderBy(static skill => skill.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                _securityVerdicts = verdicts.Items;
                _skillSnapshotClient = client;
                _skillSnapshotEpoch = epoch;
                _skillSnapshotAgentId = agentId;
                RebuildVisibleSkills();
                IsLoadingSkills = false;
                if (!CanManageExtensions)
                    StatusMessage = _runtime.GetText("ExtensionsPage_AdminRequired");
            });
        }
        catch (Exception ex)
        {
            ApplyIfCurrent(generation, client, agentId, () =>
            {
                IsLoadingSkills = false;
                ClearSkillSnapshot();
                ErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_LoadFormat",
                    TokenSanitizer.Sanitize(ex.Message));
            });
        }
    }

    public async Task SearchSkillsAsync(string? query)
    {
        var generation = Interlocked.Increment(ref _skillSearchGeneration);
        var client = _runtime.CurrentClient;
        var agentId = SelectedAgentId;
        var epoch = client?.ConnectionEpoch ?? 0;
        Dispatch(() =>
        {
            IsSearchingSkills = true;
            SkillSearchResults = [];
            ErrorMessage = null;
            StatusMessage = null;
        });

        if (client is null || !client.IsConnectedToGateway)
        {
            ApplySkillSearchIfCurrent(generation, client, agentId, epoch, () =>
            {
                IsSearchingSkills = false;
                ErrorMessage = _runtime.GetText("ExtensionsPage_Error_Disconnected");
            });
            return;
        }

        if (!client.AdvertisedFeatures.SupportsMethod("skills.search"))
        {
            ApplySkillSearchIfCurrent(generation, client, agentId, epoch, () =>
            {
                IsSearchingSkills = false;
                SkillsSupported = false;
                StatusMessage = _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired");
            });
            return;
        }

        try
        {
            var result = await client.SearchSkillsAsync(query?.Trim(), 30).ConfigureAwait(false);
            var canInstall = HasAdminScope(client) &&
                client.AdvertisedFeatures.SupportsMethod("skills.install");
            var canDetail = client.AdvertisedFeatures.SupportsMethod("skills.detail");
            var rows = result.Results.Select(item => new SkillSearchItemPresentation(
                    item.Slug,
                    string.IsNullOrWhiteSpace(item.DisplayName) ? item.Slug : item.DisplayName,
                    item.Summary ?? string.Empty,
                    string.IsNullOrWhiteSpace(item.Version)
                        ? _runtime.GetText("ExtensionsPage_VersionUnknown")
                        : item.Version,
                    string.IsNullOrWhiteSpace(item.Version) ? null : item.Version,
                    SkillTrustLabel(item.TrustState),
                    item.TrustState,
                    item.SafeInstallReference,
                    item.InstallOnly,
                    item.SafeInstallReference is not null && (item.InstallOnly || canDetail),
                    canInstall && item.SafeInstallReference is not null && (item.InstallOnly || canDetail),
                    agentId,
                    epoch,
                    client))
                .ToArray();
            ApplySkillSearchIfCurrent(generation, client, agentId, epoch, () =>
            {
                SkillSearchResults = rows;
                IsSearchingSkills = false;
                if (!result.IsSupported)
                    StatusMessage = _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired");
                else if (rows.Length == 0)
                    StatusMessage = _runtime.GetText("ExtensionsPage_NoSearchResults");
                else if (rows.Any(static row => row.SafeInstallReference is null))
                    StatusMessage = _runtime.GetText("ExtensionsPage_SearchIdentityUpgradeRequired");
            });
        }
        catch (Exception ex)
        {
            ApplySkillSearchIfCurrent(generation, client, agentId, epoch, () =>
            {
                IsSearchingSkills = false;
                ErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_SearchFormat",
                    TokenSanitizer.Sanitize(ex.Message));
            });
        }
    }

    public async Task<SkillReviewPresentation?> ReviewSkillAsync(SkillSearchItemPresentation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var reviewGeneration = Interlocked.Increment(ref _skillReviewGeneration);
        ErrorMessage = null;
        var client = _runtime.CurrentClient;
        var agentId = SelectedAgentId;
        if (client is null || item.SafeInstallReference is null ||
            !ReferenceEquals(client, item.ConnectionClient) ||
            client.ConnectionEpoch != item.ConnectionEpoch ||
            !string.Equals(agentId, item.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = item.SafeInstallReference is null
                ? _runtime.GetText("ExtensionsPage_SearchIdentityUpgradeRequired")
                : _runtime.GetText("ExtensionsPage_SkillReviewExpired");
            return null;
        }
        var epoch = client.ConnectionEpoch;
        var requiresUnscannedConfirmation = IsUnscannedTrustState(item.TrustState);

        if (item.InstallOnly)
        {
            return new SkillReviewPresentation(
                item,
                _runtime.GetText("ExtensionsPage_PublisherUnknown"),
                item.Summary,
                item.VersionLabel,
                _runtime.GetText("ExtensionsPage_UnscannedMetadata"),
                item.SafeInstallReference,
                null,
                agentId,
                epoch,
                client,
                requiresUnscannedConfirmation);
        }

        if (!client.AdvertisedFeatures.SupportsMethod("skills.detail"))
        {
            ErrorMessage = _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired");
            return null;
        }

        try
        {
            var detail = await client.GetSkillDetailAsync(item.SafeInstallReference).ConfigureAwait(false);
            if (reviewGeneration != Volatile.Read(ref _skillReviewGeneration) ||
                !IsCurrentSkillScope(client, epoch, agentId))
                return null;
            if (!detail.IsSupported || detail.Skill is null)
            {
                ApplySkillReviewIfCurrent(reviewGeneration, client, epoch, agentId, () =>
                    ErrorMessage = _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired"));
                return null;
            }

            var publisher = detail.Owner?.DisplayName ?? detail.Owner?.Handle ??
                _runtime.GetText("ExtensionsPage_PublisherUnknown");
            var requestedVersion = string.IsNullOrWhiteSpace(detail.LatestVersion?.Version)
                ? item.RequestedVersion
                : detail.LatestVersion.Version;
            var version = requestedVersion ?? item.VersionLabel;
            var metadata = BuildMetadata(detail.Metadata);
            return new SkillReviewPresentation(
                item,
                publisher,
                detail.Skill.Summary ?? item.Summary,
                version,
                metadata,
                item.SafeInstallReference,
                requestedVersion,
                agentId,
                epoch,
                client,
                requiresUnscannedConfirmation);
        }
        catch (Exception ex)
        {
            ApplySkillReviewIfCurrent(reviewGeneration, client, epoch, agentId, () =>
                ErrorMessage = _runtime.FormatText(
                    "ExtensionsPage_Error_DetailFormat",
                    TokenSanitizer.Sanitize(ex.Message)));
            return null;
        }
    }

    public async Task<SkillActionOutcome> InstallSkillAsync(
        SkillReviewPresentation review,
        bool unscannedAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.RequiresUnscannedConfirmation && !unscannedAcknowledged)
        {
            return SkillActionOutcome.NeedsConfirmation(
                _runtime.GetText("ExtensionsPage_UnscannedWarning"));
        }

        var client = _runtime.CurrentClient;
        if (client is null || !ReferenceEquals(client, review.ConnectionClient) ||
            client.ConnectionEpoch != review.ConnectionEpoch ||
            !string.Equals(SelectedAgentId, review.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillReviewExpired"));
        }
        if (!HasAdminScope(client))
            return new(false, false, _runtime.GetText("ExtensionsPage_AdminRequired"));
        if (!client.AdvertisedFeatures.SupportsMethod("skills.install"))
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired"));

        try
        {
            var result = await client.InstallClawHubSkillAsync(new ClawHubSkillInstallRequest(
                review.InstallReference,
                review.AgentId,
                review.RequestedVersion)).ConfigureAwait(false);
            if (!result.IsSupported)
                return new(false, false, _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired"));
            if (!result.Ok)
                return new(false, false, result.Message ?? _runtime.GetText("ExtensionsPage_ActionFailed"));
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(true, false, result.Message ?? _runtime.GetText("ExtensionsPage_InstallSucceeded"));
        }
        catch (GatewayRequestException ex) when (InstallPolicyWarningDetails.TryParse(ex, out var warning))
        {
            var findings = warning!.Findings.Count == 0
                ? string.Empty
                : " " + string.Join(" ", warning.Findings.Select(static finding =>
                    TokenSanitizer.Sanitize(finding.Message)));
            return new(false, false, _runtime.FormatText(
                "ExtensionsPage_InstallPolicyBlockedFormat",
                TokenSanitizer.Sanitize(warning.Reason) + findings));
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(false, false, _runtime.GetText("ExtensionsPage_ActionUnconfirmed"));
        }
        catch (Exception ex)
        {
            return new(false, false, _runtime.FormatText(
                "ExtensionsPage_Error_ActionFormat",
                TokenSanitizer.Sanitize(ex.Message)));
        }
    }

    public async Task<SkillActionOutcome> ToggleSkillAsync(SkillListItemPresentation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var client = _runtime.CurrentClient;
        if (client is null || !IsCurrentSkillItem(item, client))
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillReviewExpired"));
        if (!HasAdminScope(client))
            return new(false, false, _runtime.GetText("ExtensionsPage_AdminRequired"));
        if (!client.AdvertisedFeatures.SupportsMethod("skills.update"))
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired"));

        try
        {
            var result = await client.SetSkillEnabledDetailedAsync(
                item.SkillKey,
                item.Readiness == SkillReadinessState.Disabled).ConfigureAwait(false);
            if (!result.Ok)
                return new(false, false, result.Message ?? _runtime.GetText("ExtensionsPage_ActionFailed"));
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(true, false, result.Message ?? _runtime.GetText("ExtensionsPage_ActionSucceeded"));
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(false, false, _runtime.GetText("ExtensionsPage_ActionUnconfirmed"));
        }
        catch (Exception ex)
        {
            return new(false, false, _runtime.FormatText(
                "ExtensionsPage_Error_ActionFormat",
                TokenSanitizer.Sanitize(ex.Message)));
        }
    }

    public async Task<SkillActionOutcome> UpdateSkillAsync(SkillListItemPresentation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var client = _runtime.CurrentClient;
        if (client is null || !IsCurrentSkillItem(item, client))
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillReviewExpired"));
        if (!HasAdminScope(client) || string.IsNullOrWhiteSpace(item.UpdateReference))
            return new(false, false, _runtime.GetText("ExtensionsPage_SafeUpdateUnavailable"));
        if (!client.AdvertisedFeatures.SupportsMethod("skills.update"))
            return new(false, false, _runtime.GetText("ExtensionsPage_SkillsUpgradeRequired"));

        try
        {
            var result = await client.UpdateClawHubSkillAsync(new ClawHubSkillUpdateRequest(
                item.UpdateReference,
                item.AgentId)).ConfigureAwait(false);
            if (!result.Ok)
                return new(false, false, result.Message ?? _runtime.GetText("ExtensionsPage_ActionFailed"));
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(true, false, result.Message ?? _runtime.GetText("ExtensionsPage_UpdateSucceeded"));
        }
        catch (Exception ex) when (ex is TimeoutException or GatewayConnectionLostException)
        {
            await LoadSkillsAsync().ConfigureAwait(false);
            return new(false, false, _runtime.GetText("ExtensionsPage_ActionUnconfirmed"));
        }
        catch (Exception ex)
        {
            return new(false, false, _runtime.FormatText(
                "ExtensionsPage_Error_ActionFormat",
                TokenSanitizer.Sanitize(ex.Message)));
        }
    }

    private void RebuildVisibleSkills()
    {
        var rows = _skillSnapshot
            .Where(skill => SkillFilter switch
            {
                SkillListFilter.Ready => skill.Readiness == SkillReadinessState.Ready,
                SkillListFilter.Disabled => skill.Readiness == SkillReadinessState.Disabled,
                SkillListFilter.NeedsSetup => skill.Readiness is SkillReadinessState.NeedsSetup or
                    SkillReadinessState.Blocked or SkillReadinessState.Incompatible,
                _ => true,
            })
            .Select(BuildSkillRow)
            .ToArray();
        VisibleSkills = rows;
    }

    private void ClearSkillSnapshot()
    {
        Interlocked.Increment(ref _skillReviewGeneration);
        _skillSnapshot = [];
        _securityVerdicts = [];
        _skillSnapshotClient = null;
        _skillSnapshotEpoch = 0;
        VisibleSkills = [];
        SkillSearchResults = [];
        IsSearchingSkills = false;
        IsLoadingSkills = false;
        CanManageExtensions = false;
    }

    private SkillListItemPresentation BuildSkillRow(SkillStatusEntry skill)
    {
        var updateReference = skill.Clawhub is { Valid: true } link &&
            !string.IsNullOrWhiteSpace(link.RequestedReference)
                ? link.RequestedReference
                : null;
        var security = FindSecurityVerdict(skill.Clawhub);

        return new SkillListItemPresentation(
            skill.SkillKey,
            string.IsNullOrWhiteSpace(skill.Emoji) ? skill.Name : $"{skill.Emoji} {skill.Name}",
            skill.Description,
            skill.Source,
            skill.Readiness,
            ReadinessLabel(skill.Readiness),
            RequirementsSummary(skill),
            _runtime.GetText(skill.Disabled
                ? "ExtensionsPage_EnableAction"
                : "ExtensionsPage_DisableAction"),
            CanManageExtensions &&
                _skillSnapshotClient?.AdvertisedFeatures.SupportsMethod("skills.update") == true,
            CanManageExtensions && updateReference is not null &&
                _skillSnapshotClient?.AdvertisedFeatures.SupportsMethod("skills.update") == true,
            updateReference,
            SecurityLabel(security),
            SafeHttpUrl(security?.SkillUrl),
            SafeHttpUrl(security?.SecurityAuditUrl),
            _skillSnapshotAgentId,
            _skillSnapshotEpoch,
            _skillSnapshotClient ?? throw new InvalidOperationException("Skill snapshot client is unavailable."));
    }

    private SkillSecurityVerdict? FindSecurityVerdict(ClawHubSkillLink? link)
    {
        if (link is null || string.IsNullOrWhiteSpace(link.Registry) ||
            string.IsNullOrWhiteSpace(link.Slug) || string.IsNullOrWhiteSpace(link.InstalledVersion))
        {
            return null;
        }

        var matches = _securityVerdicts.Where(verdict =>
            string.Equals(verdict.Registry, link.Registry, StringComparison.Ordinal) &&
            string.Equals(verdict.RequestedSlug, link.Slug, StringComparison.Ordinal) &&
            string.Equals(verdict.RequestedOwnerHandle ?? string.Empty, link.OwnerHandle ?? string.Empty,
                StringComparison.Ordinal) &&
            string.Equals(verdict.RequestedVersion, link.InstalledVersion, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private string ReadinessLabel(SkillReadinessState readiness) => _runtime.GetText(readiness switch
    {
        SkillReadinessState.Ready => "ExtensionsPage_ReadinessReady",
        SkillReadinessState.Disabled => "ExtensionsPage_ReadinessDisabled",
        SkillReadinessState.Blocked => "ExtensionsPage_ReadinessBlocked",
        SkillReadinessState.Incompatible => "ExtensionsPage_ReadinessIncompatible",
        _ => "ExtensionsPage_ReadinessNeedsSetup",
    });

    private string RequirementsSummary(SkillStatusEntry skill)
    {
        if (skill.Readiness == SkillReadinessState.Ready)
            return _runtime.GetText("ExtensionsPage_NoSetupNeeded");
        if (skill.Readiness == SkillReadinessState.Disabled)
            return _runtime.GetText("ExtensionsPage_DisabledDescription");
        if (skill.BlockedByAllowlist || skill.BlockedByAgentFilter)
            return _runtime.GetText("ExtensionsPage_BlockedDescription");
        if (skill.PlatformIncompatible)
            return _runtime.GetText("ExtensionsPage_IncompatibleDescription");

        return _runtime.FormatText(
            "ExtensionsPage_MissingRequirementsFormat",
            skill.Missing.Bins.Count + skill.Missing.AnyBins.Count,
            skill.Missing.Env.Count,
            skill.Missing.Config.Count);
    }

    private string? SecurityLabel(SkillSecurityVerdict? verdict)
    {
        if (verdict is null)
            return null;
        if (!string.IsNullOrWhiteSpace(verdict.SecurityStatus))
            return verdict.SecurityStatus;
        return verdict.Ok
            ? _runtime.GetText("ExtensionsPage_SecurityChecked")
            : _runtime.GetText("ExtensionsPage_SecurityWarning");
    }

    private string BuildMetadata(ClawHubSkillMetadata? metadata)
    {
        if (metadata is null || (metadata.Os.Count == 0 && metadata.Systems.Count == 0))
            return _runtime.GetText("ExtensionsPage_MetadataUnavailable");
        return _runtime.FormatText(
            "ExtensionsPage_MetadataFormat",
            string.Join(", ", metadata.Os),
            string.Join(", ", metadata.Systems));
    }

    private void SubscribeToCurrentClient()
    {
        UnsubscribeClient();
        _subscribedClient = _runtime.CurrentClient;
        if (_subscribedClient is not null)
            _subscribedClient.HandshakeSucceeded += OnClientHandshakeSucceeded;
        if (_subscribedClient is ISkillsGatewayEvents skillEvents)
            skillEvents.SkillsChanged += OnSkillsChanged;
    }

    private void UnsubscribeClient()
    {
        if (_subscribedClient is not null)
            _subscribedClient.HandshakeSucceeded -= OnClientHandshakeSucceeded;
        if (_subscribedClient is ISkillsGatewayEvents skillEvents)
            skillEvents.SkillsChanged -= OnSkillsChanged;
        _subscribedClient = null;
    }

    private void OnSkillsChanged(object? sender, EventArgs e)
    {
        if (!_active)
            return;
        Dispatch(() => _ = LoadSkillsAsync());
    }

    private void OnRuntimeClientChanged(object? sender, EventArgs e) =>
        Dispatch(RefreshConnectionScope);

    private void OnClientHandshakeSucceeded(object? sender, EventArgs e) =>
        Dispatch(RefreshConnectionScope);

    private void RefreshConnectionScope()
    {
        if (!_active)
            return;

        Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _skillSearchGeneration);
        Interlocked.Increment(ref _skillReviewGeneration);
        Interlocked.Increment(ref _pluginLoadGeneration);
        Interlocked.Increment(ref _pluginSearchGeneration);
        UnsubscribeClient();
        SubscribeToCurrentClient();
        AgentIds = _runtime.GetAgentIds();
        SelectedAgentId = ResolveAgentId($"agent:{SelectedAgentId}:extensions", AgentIds);
        ClearSkillSnapshot();
        ClearPluginSnapshot();
        ConnectionScopeVersion++;
        _ = LoadSkillsAsync();
        _ = LoadPluginsAsync();
    }

    private void ApplyIfCurrent(
        long generation,
        IOperatorGatewayClient? client,
        string agentId,
        Action apply) => Dispatch(() =>
    {
        if (!_active || generation != Volatile.Read(ref _loadGeneration) ||
            !ReferenceEquals(client, _runtime.CurrentClient) ||
            !string.Equals(agentId, SelectedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        apply();
    });

    private void ApplySkillSearchIfCurrent(
        long generation,
        IOperatorGatewayClient? client,
        string agentId,
        long epoch,
        Action apply) => Dispatch(() =>
    {
        if (!_active || generation != Volatile.Read(ref _skillSearchGeneration) ||
            !ReferenceEquals(client, _runtime.CurrentClient) ||
            !string.Equals(agentId, SelectedAgentId, StringComparison.OrdinalIgnoreCase) ||
            (client is not null && client.ConnectionEpoch != epoch))
        {
            return;
        }
        apply();
    });

    private bool IsCurrentSkillScope(
        IOperatorGatewayClient client,
        long epoch,
        string agentId) =>
        _active && ReferenceEquals(client, _runtime.CurrentClient) &&
        client.ConnectionEpoch == epoch &&
        string.Equals(agentId, SelectedAgentId, StringComparison.OrdinalIgnoreCase);

    private void ApplySkillReviewIfCurrent(
        long generation,
        IOperatorGatewayClient client,
        long epoch,
        string agentId,
        Action action) => Dispatch(() =>
    {
        if (generation == Volatile.Read(ref _skillReviewGeneration) &&
            IsCurrentSkillScope(client, epoch, agentId))
            action();
    });

    private bool IsCurrentSkillItem(
        SkillListItemPresentation item,
        IOperatorGatewayClient client) =>
        ReferenceEquals(item.ConnectionClient, client) &&
        item.ConnectionEpoch == client.ConnectionEpoch &&
        string.Equals(item.AgentId, SelectedAgentId, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnscannedTrustState(string? trustState) =>
        string.Equals(trustState, "not-scanned-by-clawhub", StringComparison.OrdinalIgnoreCase);

    private string SkillTrustLabel(string? trustState) =>
        IsUnscannedTrustState(trustState)
            ? _runtime.GetText("ExtensionsPage_TrustNotScanned")
            : _runtime.GetText("ExtensionsPage_TrustUnknown");

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(action);
    }

    private static bool HasAdminScope(IOperatorGatewayClient? client) =>
        client?.GrantedOperatorScopes.Contains(AdminScope, StringComparer.Ordinal) == true;

    private static string ResolveAgentId(object? parameter, IReadOnlyList<string> agentIds)
    {
        var tag = parameter as string;
        var requested = HubPageRegistry.ParseAgentId(tag);
        return agentIds.FirstOrDefault(id =>
            string.Equals(id, requested, StringComparison.OrdinalIgnoreCase)) ??
            agentIds.FirstOrDefault() ?? "main";
    }

    private static string? SafeHttpUrl(string? value) =>
        HttpUrlValidator.TryParse(value, out var canonical, out _) ? canonical : null;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
