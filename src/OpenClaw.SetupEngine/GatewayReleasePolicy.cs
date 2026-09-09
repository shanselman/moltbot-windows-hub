using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum GatewayReleaseSelectionMode
{
    Recommended,
    Fallback,
    Exact,
}

public enum GatewayReleaseStatus
{
    Validated,
    Candidate,
    Rejected,
}

public enum GatewayCompatibilityFailureKind
{
    InvalidPolicy,
    MissingFallback,
    BelowSecurityFloor,
    UnattestedRelease,
    InstalledVersionMismatch,
    InstalledRuntimeMismatch,
    ProtocolMismatch,
    ServerVersionMismatch,
}

public sealed class GatewayCompatibilityException : Exception
{
    public GatewayCompatibilityException(GatewayCompatibilityFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public GatewayCompatibilityFailureKind Kind { get; }
}

public sealed record GatewayReleaseEvidence(
    string Version,
    GatewayReleaseStatus Status,
    int ProtocolGeneration,
    string NpmIntegrity,
    string ReleaseUrl,
    string ValidationEvidence,
    string? RejectionReason = null);

public sealed record GatewayReleaseResolution(
    GatewayReleaseSelectionMode Mode,
    string Version,
    int ProtocolGeneration,
    bool IsCustomInstaller,
    GatewayReleaseEvidence? Evidence);

public readonly record struct GatewayReleaseVersion(int Year, int Month, int Patch, int Correction)
    : IComparable<GatewayReleaseVersion>
{
    private static readonly Regex s_pattern = new(
        @"^(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d+)(?:-(?<correction>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_outputPattern = new(
        @"(?<![0-9A-Za-z.-])(?<version>\d{4}\.\d{1,2}\.\d+(?:-\d+)?)(?![0-9A-Za-z.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out GatewayReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = s_pattern.Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["year"].Value, out var year) ||
            !int.TryParse(match.Groups["month"].Value, out var month) ||
            !int.TryParse(match.Groups["patch"].Value, out var patch) ||
            month is < 1 or > 12)
        {
            return false;
        }

        var correction = 0;
        if (match.Groups["correction"].Success &&
            !int.TryParse(match.Groups["correction"].Value, out correction))
        {
            return false;
        }

        version = new GatewayReleaseVersion(year, month, patch, correction);
        return true;
    }

    public static bool TryExtract(string? output, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var match = s_outputPattern.Match(output);
        if (!match.Success || !TryParse(match.Groups["version"].Value, out _))
            return false;

        version = match.Groups["version"].Value;
        return true;
    }

    public int CompareTo(GatewayReleaseVersion other)
    {
        var year = Year.CompareTo(other.Year);
        if (year != 0) return year;
        var month = Month.CompareTo(other.Month);
        if (month != 0) return month;
        var patch = Patch.CompareTo(other.Patch);
        return patch != 0 ? patch : Correction.CompareTo(other.Correction);
    }
}

/// <summary>
/// Embedded Windows release policy. npm dist-tags discover candidates; they never
/// select the version installed by the product.
/// </summary>
public static class GatewayReleasePolicy
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const int ProtocolGeneration = 4;
    public const string NodeVersion = "24.19.0";
    public const string SecurityFloor = "2026.6.11";
    public const string RecommendedVersion = "2026.6.34";
    public const string RuntimeRejectedVersion = "2026.7.1";
    public const string EvidenceRejectedVersion = "2026.7.1-2";

    private static readonly IReadOnlyDictionary<string, GatewayReleaseEvidence> s_releases =
        new Dictionary<string, GatewayReleaseEvidence>(StringComparer.Ordinal)
        {
            ["2026.6.11"] = new(
                "2026.6.11",
                GatewayReleaseStatus.Validated,
                ProtocolGeneration,
                "sha512-T+P/g19IheeT1ckXMoPN61dYuE8vBF4MderI+kWkvpuFYxPkJxn8AXLpu9IXCnN9g36Acpm9+mMD/V+lsvOkyA==",
                "https://github.com/openclaw/openclaw/releases/tag/v2026.6.11",
                "Validated fallback: exact Windows clean setup, protocol-v4 pairing, and connectivity proof on 2026-08-06"),
            [RecommendedVersion] = new(
                RecommendedVersion,
                GatewayReleaseStatus.Validated,
                ProtocolGeneration,
                "sha512-Rm4khBrWn9HYqE99NBryCFgjwlsIuwBqK5jIANn2773CGXJ1JIZkDn5twEHB+8SVFdh0FPNPHRVgZepzNJDfHg==",
                "https://github.com/openclaw/openclaw/releases/tag/v2026.6.34",
                "Promoted 2026-08-06 after exact Windows clean setup, protocol-v4 handshake, operator/node pairing, restart, network recovery, revocation recovery, and Gateway node.invoke proof"),
            [RuntimeRejectedVersion] = new(
                RuntimeRejectedVersion,
                GatewayReleaseStatus.Rejected,
                ProtocolGeneration,
                "sha512-ge/Xss99CHAjPL/ikmH/UFoiOrjcxDB4sW3y9mhyCD+dYW3wzV7TKbAVdkrXFgAG2d2BjpJofP97zUZ+umxo8g==",
                "https://github.com/openclaw/openclaw/releases/tag/v2026.7.1",
                "Exact Windows candidate run on 2026-08-06",
                "clean setup failed when the expanded Gateway wizard restarted the service and the fail-closed reconnect could not re-establish trusted endpoint ownership"),
            [EvidenceRejectedVersion] = new(
                EvidenceRejectedVersion,
                GatewayReleaseStatus.Rejected,
                ProtocolGeneration,
                "sha512-ycF3yPcbjN6bUPeaUx6Mh6vze1hQWoD3CT/wWcmD7a8xaHHHRUaAlaq+lFxMHf1ssEgODVAwjlzYqp2twkYZ7g==",
                "https://github.com/openclaw/openclaw/releases/tag/v2026.7.1-2",
                "Candidate preflight on 2026-08-06",
                "npm provenance attestation and a stable release-validation manifest were not published"),
        };

    public static string? FallbackVersion => "2026.6.11";

    public static IReadOnlyDictionary<string, GatewayReleaseEvidence> Releases => s_releases;

    public static bool IsOfficialInstallerUrl(string? installUrl) =>
        string.IsNullOrWhiteSpace(installUrl) ||
        string.Equals(installUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ValidateEmbeddedPolicy()
    {
        var errors = new List<string>();
        if (!Version.TryParse(NodeVersion, out _))
            errors.Add($"Node runtime '{NodeVersion}' is not an exact numeric version.");
        if (!GatewayReleaseVersion.TryParse(SecurityFloor, out var floor))
            errors.Add($"Security floor '{SecurityFloor}' is not an exact stable release.");

        foreach (var (key, release) in s_releases)
        {
            if (!string.Equals(key, release.Version, StringComparison.Ordinal))
                errors.Add($"Release key '{key}' does not match evidence version '{release.Version}'.");
            if (!GatewayReleaseVersion.TryParse(release.Version, out _))
                errors.Add($"Release '{release.Version}' is not an exact stable version.");
            if (release.ProtocolGeneration != ProtocolGeneration)
                errors.Add($"Release '{release.Version}' does not declare protocol v{ProtocolGeneration}.");
            if (!release.NpmIntegrity.StartsWith("sha512-", StringComparison.Ordinal))
                errors.Add($"Release '{release.Version}' does not have SHA-512 npm integrity.");
            if (!Uri.TryCreate(release.ReleaseUrl, UriKind.Absolute, out var releaseUri) ||
                !string.Equals(releaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Release '{release.Version}' does not have an HTTPS evidence URL.");
            }
            if (release.Status == GatewayReleaseStatus.Rejected &&
                string.IsNullOrWhiteSpace(release.RejectionReason))
            {
                errors.Add($"Rejected release '{release.Version}' does not record a rejection reason.");
            }
        }

        ValidateSelectedPolicyEntry("recommended", RecommendedVersion, floor, errors);
        if (FallbackVersion is { } fallback)
        {
            if (string.Equals(fallback, RecommendedVersion, StringComparison.Ordinal))
                errors.Add("Fallback release must be distinct from the recommendation.");
            ValidateSelectedPolicyEntry("fallback", fallback, floor, errors);
        }

        return errors;
    }

    internal static bool IsReleaseEligible(
        GatewayReleaseEvidence? evidence,
        bool allowCandidate) =>
        evidence is not null &&
        evidence.ProtocolGeneration == ProtocolGeneration &&
        evidence.Status != GatewayReleaseStatus.Rejected &&
        (evidence.Status != GatewayReleaseStatus.Candidate || allowCandidate);

    public static GatewayReleaseResolution ResolveAndApply(
        SetupConfig config,
        bool allowCandidate = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policyErrors = ValidateEmbeddedPolicy();
        if (policyErrors.Count != 0)
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                $"Embedded Gateway release policy is invalid: {string.Join(" ", policyErrors)}");
        }

        var gateway = config.Gateway;
        var customInstaller = !IsOfficialInstallerUrl(gateway.InstallUrl);
        var requestedVersion = gateway.Version?.Trim();

        if (customInstaller)
        {
            if (string.IsNullOrWhiteSpace(requestedVersion))
            {
                throw Failure(
                    GatewayCompatibilityFailureKind.InvalidPolicy,
                    "Custom Gateway installer URLs require an exact stable Gateway.Version.");
            }

            ValidateStableFloor(requestedVersion);
            var customResolution = new GatewayReleaseResolution(
                GatewayReleaseSelectionMode.Exact,
                requestedVersion,
                ProtocolGeneration,
                IsCustomInstaller: true,
                Evidence: null);
            gateway.Selection = "exact";
            gateway.Version = requestedVersion;
            gateway.ResolvedRelease = customResolution;
            return customResolution;
        }

        var mode = ParseMode(gateway.Selection, requestedVersion);
        string selectedVersion;
        switch (mode)
        {
            case GatewayReleaseSelectionMode.Recommended:
                selectedVersion = RecommendedVersion;
                break;
            case GatewayReleaseSelectionMode.Fallback:
                selectedVersion = FallbackVersion ?? throw Failure(
                    GatewayCompatibilityFailureKind.MissingFallback,
                    $"No distinct validated Gateway fallback is available at or above security floor {SecurityFloor}.");
                break;
            case GatewayReleaseSelectionMode.Exact:
                if (string.IsNullOrWhiteSpace(requestedVersion))
                {
                    throw Failure(
                        GatewayCompatibilityFailureKind.InvalidPolicy,
                        "Gateway selection 'exact' requires Gateway.Version.");
                }
                selectedVersion = requestedVersion;
                break;
            default:
                throw new InvalidOperationException($"Unsupported Gateway release selection mode: {mode}");
        }

        ValidateStableFloor(selectedVersion);
        _ = s_releases.TryGetValue(selectedVersion, out var evidence);
        if (!IsReleaseEligible(evidence, allowCandidate))
        {
            var rejection = evidence?.RejectionReason;
            var detail = string.IsNullOrWhiteSpace(rejection) ? "" : $" {rejection}.";
            throw Failure(
                GatewayCompatibilityFailureKind.UnattestedRelease,
                $"Gateway {selectedVersion} is not an eligible protocol-v{ProtocolGeneration} Windows release.{detail}");
        }

        var resolution = new GatewayReleaseResolution(
            mode,
            selectedVersion,
            ProtocolGeneration,
            IsCustomInstaller: false,
            evidence);
        gateway.Selection = mode.ToString().ToLowerInvariant();
        gateway.Version = selectedVersion;
        gateway.ResolvedRelease = resolution;
        return resolution;
    }

    internal static GatewayReleaseResolution ResolveAndApplyValidationPackage(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policyErrors = ValidateEmbeddedPolicy();
        if (policyErrors.Count != 0)
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                $"Embedded Gateway release policy is invalid: {string.Join(" ", policyErrors)}");
        }

        if (!IsOfficialInstallerUrl(config.Gateway.InstallUrl))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                "Gateway candidate package validation requires the official installer.");
        }

        var selectedVersion = config.Gateway.Version?.Trim();
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                "Gateway candidate package validation requires an exact Gateway.Version.");
        }

        ValidateStableFloor(selectedVersion);
        _ = s_releases.TryGetValue(selectedVersion, out var evidence);
        if (evidence?.Status == GatewayReleaseStatus.Rejected)
        {
            var detail = string.IsNullOrWhiteSpace(evidence.RejectionReason)
                ? ""
                : $" {evidence.RejectionReason}.";
            throw Failure(
                GatewayCompatibilityFailureKind.UnattestedRelease,
                $"Gateway {selectedVersion} is rejected for protocol-v{ProtocolGeneration} Windows setup.{detail}");
        }

        var resolution = new GatewayReleaseResolution(
            GatewayReleaseSelectionMode.Exact,
            selectedVersion,
            ProtocolGeneration,
            IsCustomInstaller: false,
            evidence);
        config.Gateway.Selection = "exact";
        config.Gateway.Version = selectedVersion;
        config.Gateway.ResolvedRelease = resolution;
        return resolution;
    }

    public static GatewayCompatibilityException? ValidateHandshake(
        SetupConfig config,
        GatewaySelfInfo? gatewaySelf)
    {
        var selectedVersion = config.Gateway.ResolvedRelease?.Version ?? config.Gateway.Version;
        if (string.IsNullOrWhiteSpace(selectedVersion))
        {
            return Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                "Gateway release policy was not resolved before the compatibility handshake.");
        }

        if (gatewaySelf?.Protocol != ProtocolGeneration)
        {
            var actual = gatewaySelf?.Protocol?.ToString() ?? "missing";
            return Failure(
                GatewayCompatibilityFailureKind.ProtocolMismatch,
                $"Gateway compatibility check failed: expected protocol v{ProtocolGeneration}, received {actual}.");
        }

        if (!string.Equals(gatewaySelf.ServerVersion, selectedVersion, StringComparison.Ordinal))
        {
            var actual = string.IsNullOrWhiteSpace(gatewaySelf.ServerVersion) ? "missing" : gatewaySelf.ServerVersion;
            return Failure(
                GatewayCompatibilityFailureKind.ServerVersionMismatch,
                $"Gateway compatibility check failed: selected version {selectedVersion}, server reported {actual}.");
        }

        return null;
    }

    public static bool TryApplyFallback(SetupConfig config, out string? error)
    {
        if (!CanRetryWithFallback(config))
        {
            error = !IsOfficialInstallerUrl(config.Gateway.InstallUrl)
                ? "Custom Gateway installers do not use the product's validated fallback."
                : FallbackVersion is null
                    ? $"No validated Gateway fallback is available at or above security floor {SecurityFloor}."
                    : $"Gateway {FallbackVersion} is already selected; no older validated fallback will be tried.";
            return false;
        }

        config.Gateway.Selection = "fallback";
        config.Gateway.Version = null;
        config.Gateway.ResolvedRelease = null;
        try
        {
            ResolveAndApply(config);
            error = null;
            return true;
        }
        catch (GatewayCompatibilityException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool CanRetryWithFallback(SetupConfig config) =>
        FallbackVersion is { } fallback &&
        IsOfficialInstallerUrl(config.Gateway.InstallUrl) &&
        !string.Equals(config.Gateway.Version, fallback, StringComparison.Ordinal);

    public static bool CanRetryWithFallback(
        SetupConfig config,
        GatewayCompatibilityFailureKind failureKind) =>
        failureKind is GatewayCompatibilityFailureKind.InstalledVersionMismatch
            or GatewayCompatibilityFailureKind.ProtocolMismatch
            or GatewayCompatibilityFailureKind.ServerVersionMismatch &&
        CanRetryWithFallback(config);

    private static GatewayReleaseSelectionMode ParseMode(string? selection, string? requestedVersion)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion) &&
            (string.IsNullOrWhiteSpace(selection) ||
             selection.Equals("recommended", StringComparison.OrdinalIgnoreCase)))
        {
            return GatewayReleaseSelectionMode.Exact;
        }

        if (string.IsNullOrWhiteSpace(selection))
            return GatewayReleaseSelectionMode.Recommended;

        if (Enum.TryParse<GatewayReleaseSelectionMode>(selection, ignoreCase: true, out var mode))
            return mode;

        throw Failure(
            GatewayCompatibilityFailureKind.InvalidPolicy,
            $"Invalid Gateway.Selection '{selection}'. Use recommended, fallback, or exact.");
    }

    private static void ValidateStableFloor(string version)
    {
        if (!GatewayReleaseVersion.TryParse(version, out var parsed))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                $"Gateway version '{version}' is not an exact stable release. Prerelease channels are not eligible.");
        }

        if (!GatewayReleaseVersion.TryParse(SecurityFloor, out var floor))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                $"Gateway security floor '{SecurityFloor}' is not an exact stable release.");
        }
        if (parsed.CompareTo(floor) < 0)
        {
            throw Failure(
                GatewayCompatibilityFailureKind.BelowSecurityFloor,
                $"Gateway {version} is below the Windows security floor {SecurityFloor}.");
        }
    }

    private static GatewayCompatibilityException Failure(
        GatewayCompatibilityFailureKind kind,
        string message) => new(kind, message);

    private static void ValidateSelectedPolicyEntry(
        string role,
        string version,
        GatewayReleaseVersion floor,
        List<string> errors)
    {
        if (!s_releases.TryGetValue(version, out var evidence))
        {
            errors.Add($"The {role} release '{version}' has no evidence entry.");
            return;
        }

        if (evidence.Status != GatewayReleaseStatus.Validated)
            errors.Add($"The {role} release '{version}' is not validated.");
        if (GatewayReleaseVersion.TryParse(version, out var parsed) &&
            parsed.CompareTo(floor) < 0)
        {
            errors.Add($"The {role} release '{version}' is below security floor {SecurityFloor}.");
        }
    }
}
