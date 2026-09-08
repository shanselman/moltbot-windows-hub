namespace OpenClaw.Shared;

public enum SkillReadinessState
{
    Ready,
    Disabled,
    Blocked,
    Incompatible,
    NeedsSetup,
}

public sealed class SkillRequirements
{
    public IReadOnlyList<string> Bins { get; set; } = [];
    public IReadOnlyList<string> AnyBins { get; set; } = [];
    public IReadOnlyList<string> Env { get; set; } = [];
    public IReadOnlyList<string> Config { get; set; } = [];
    public IReadOnlyList<string> Os { get; set; } = [];

    public bool HasAny =>
        Bins.Count > 0 || AnyBins.Count > 0 || Env.Count > 0 ||
        Config.Count > 0 || Os.Count > 0;
}

public sealed class SkillConfigCheck
{
    public string Path { get; set; } = string.Empty;
    public bool Satisfied { get; set; }
}

public sealed class SkillInstallOption
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public IReadOnlyList<string> Bins { get; set; } = [];
}

public sealed class ClawHubSkillLink
{
    public string? Status { get; set; }
    public bool Valid { get; set; }
    public string? Reason { get; set; }
    public string? Registry { get; set; }
    public string? Slug { get; set; }
    public string? OwnerHandle { get; set; }
    public string? RequestedReference { get; set; }
    public string? TrustState { get; set; }
    public string? InstalledVersion { get; set; }
    public long? InstalledAt { get; set; }
}

public sealed class SkillCardMetadata
{
    public bool Present { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class SkillStatusEntry
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Bundled { get; set; }
    public string SkillKey { get; set; } = string.Empty;
    public string? PrimaryEnv { get; set; }
    public string? Emoji { get; set; }
    public string? Homepage { get; set; }
    public bool Always { get; set; }
    public bool Disabled { get; set; }
    public bool BlockedByAllowlist { get; set; }
    public bool BlockedByAgentFilter { get; set; }
    public bool Eligible { get; set; }
    public bool PlatformIncompatible { get; set; }
    public bool ModelVisible { get; set; }
    public bool UserInvocable { get; set; }
    public bool CommandVisible { get; set; }
    public SkillRequirements Requirements { get; set; } = new();
    public SkillRequirements Missing { get; set; } = new();
    public IReadOnlyList<SkillConfigCheck> ConfigChecks { get; set; } = [];
    public IReadOnlyList<SkillInstallOption> Install { get; set; } = [];
    public ClawHubSkillLink? Clawhub { get; set; }
    public SkillCardMetadata? SkillCard { get; set; }

    public SkillReadinessState Readiness =>
        Disabled ? SkillReadinessState.Disabled :
        BlockedByAllowlist || BlockedByAgentFilter ? SkillReadinessState.Blocked :
        PlatformIncompatible ? SkillReadinessState.Incompatible :
        Eligible ? SkillReadinessState.Ready :
        SkillReadinessState.NeedsSetup;
}

public sealed class SkillsStatusReport
{
    public string? AgentId { get; set; }
    public IReadOnlyList<string> AgentSkillFilter { get; set; } = [];
    public IReadOnlyList<SkillStatusEntry> Skills { get; set; } = [];
    public bool IsSupported { get; set; } = true;

    public static SkillsStatusReport Unsupported { get; } = new() { IsSupported = false };
}

public sealed class ClawHubSkillSearchEntry
{
    public double Score { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string? InstallRef { get; set; }
    public bool InstallOnly { get; set; }
    public string? TrustState { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Icon { get; set; }
    public string? Version { get; set; }
    public long? UpdatedAt { get; set; }

    /// <summary>
    /// Exact source-qualified identity that must be echoed to detail/install.
    /// A missing value means the connected Gateway predates the safe identity contract.
    /// </summary>
    public string? SafeInstallReference =>
        string.IsNullOrWhiteSpace(InstallRef) ? null : InstallRef;
}

public sealed class SkillsSearchResult
{
    public IReadOnlyList<ClawHubSkillSearchEntry> Results { get; set; } = [];
    public bool IsSupported { get; set; } = true;

    public static SkillsSearchResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class ClawHubSkillDetail
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public IReadOnlyDictionary<string, string> Tags { get; set; } =
        new Dictionary<string, string>();
    public string? Channel { get; set; }
    public bool? IsOfficial { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class ClawHubSkillVersion
{
    public string Version { get; set; } = string.Empty;
    public long CreatedAt { get; set; }
    public string? Changelog { get; set; }
}

public sealed class ClawHubSkillMetadata
{
    public IReadOnlyList<string> Os { get; set; } = [];
    public IReadOnlyList<string> Systems { get; set; } = [];
}

public sealed class ClawHubSkillOwner
{
    public string? Handle { get; set; }
    public string? DisplayName { get; set; }
    public string? Image { get; set; }
    public bool? Official { get; set; }
    public string? Channel { get; set; }
    public bool? IsOfficial { get; set; }
}

public sealed class SkillsDetailResult
{
    public ClawHubSkillDetail? Skill { get; set; }
    public ClawHubSkillVersion? LatestVersion { get; set; }
    public ClawHubSkillMetadata? Metadata { get; set; }
    public ClawHubSkillOwner? Owner { get; set; }
    public bool IsSupported { get; set; } = true;

    public static SkillsDetailResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class SkillSecurityVerdictError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public sealed class SkillSecurityVerdict
{
    public string Registry { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Decision { get; set; } = string.Empty;
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public string RequestedSlug { get; set; } = string.Empty;
    public string? RequestedOwnerHandle { get; set; }
    public string RequestedVersion { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? Version { get; set; }
    public string? DisplayName { get; set; }
    public string? PublisherHandle { get; set; }
    public string? PublisherDisplayName { get; set; }
    public long? CreatedAt { get; set; }
    public long? CheckedAt { get; set; }
    public string? SkillUrl { get; set; }
    public string? SecurityAuditUrl { get; set; }
    public string? SecurityStatus { get; set; }
    public bool? SecurityPassed { get; set; }
    public SkillSecurityVerdictError? Error { get; set; }
}

public sealed class SkillsSecurityVerdictsResult
{
    public string Schema { get; set; } = string.Empty;
    public IReadOnlyList<SkillSecurityVerdict> Items { get; set; } = [];
    public bool IsSupported { get; set; } = true;

    public static SkillsSecurityVerdictsResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class SkillCardResult
{
    public string Schema { get; set; } = string.Empty;
    public string SkillKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsSupported { get; set; } = true;

    public static SkillCardResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed class SkillMutationResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? SkillKey { get; set; }
    public string? Slug { get; set; }
    public string? Version { get; set; }
    public string? Warning { get; set; }
    public bool IsSupported { get; set; } = true;

    public static SkillMutationResult Unsupported { get; } = new() { IsSupported = false };
}

public sealed record ClawHubSkillInstallRequest(
    string InstallReference,
    string? AgentId = null,
    string? Version = null,
    int? TimeoutMs = null);

public sealed record ClawHubSkillUpdateRequest(
    string InstallReference,
    string? AgentId = null);
