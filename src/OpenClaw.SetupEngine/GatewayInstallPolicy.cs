using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum GatewayCompatibilityFailureKind
{
    InvalidPolicy,
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

public static class GatewayPackageVersion
{
    private static readonly Regex s_pattern = new(
        @"^(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_outputPattern = new(
        @"(?<![0-9A-Za-z.+-])(?<version>\d{4}\.\d{1,2}\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)(?![0-9A-Za-z.+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsExact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = s_pattern.Match(value.Trim());
        return match.Success &&
               int.TryParse(match.Groups["month"].Value, out var month) &&
               month is >= 1 and <= 12;
    }

    public static bool TryGetReleaseLine(string? value, out GatewayReleaseVersion version)
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

        version = new GatewayReleaseVersion(year, month, patch, Correction: 0);
        return true;
    }

    public static bool TryExtract(string? output, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var match = s_outputPattern.Match(output);
        if (!match.Success || !IsExact(match.Groups["version"].Value))
            return false;

        version = match.Groups["version"].Value;
        return true;
    }
}

public static class GatewayInstallPolicy
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const int ProtocolGeneration = 4;
    public const string NodeVersion = "24.19.0";
    public const string LegacyRecommendedVersion = "2026.6.34";
    public const string LegacyFallbackVersion = "2026.6.11";

    private static readonly HashSet<string> s_supportedTags = new(
        ["latest", "next", "beta", "extended-stable", "dev"],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsOfficialInstallerUrl(string? installUrl) =>
        string.IsNullOrWhiteSpace(installUrl) ||
        string.Equals(installUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase);

    public static void ValidateAndApply(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var gateway = config.Gateway;
        var customInstaller = !IsOfficialInstallerUrl(gateway.InstallUrl);
        var requestedVersion = ResolveLegacySelection(gateway);
        gateway.InstalledVersion = null;

        if (customInstaller)
        {
            RequireExactVersion(requestedVersion, "Custom Gateway installer URLs require an exact stable Gateway.Version.");
            gateway.Version = requestedVersion;
            gateway.FallbackVersion = NormalizeFallbackVersion(gateway.FallbackVersion);
            return;
        }

        gateway.Version = NormalizeOfficialSelector(requestedVersion);
        gateway.FallbackVersion = NormalizeFallbackVersion(gateway.FallbackVersion);
    }

    internal static void ValidateAndApplyValidationPackage(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!IsOfficialInstallerUrl(config.Gateway.InstallUrl))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                "Gateway candidate package validation requires the official installer.");
        }

        var selectedVersion = config.Gateway.Version?.Trim();
        RequireExactVersion(
            selectedVersion,
            "Gateway candidate package validation requires an exact Gateway.Version.");
        config.Gateway.Version = selectedVersion;
        config.Gateway.Selection = null;
        config.Gateway.InstalledVersion = null;
    }

    public static GatewayCompatibilityException? ValidateHandshake(
        SetupConfig config,
        GatewaySelfInfo? gatewaySelf)
    {
        if (gatewaySelf?.Protocol != ProtocolGeneration)
        {
            var actual = gatewaySelf?.Protocol?.ToString() ?? "missing";
            return Failure(
                GatewayCompatibilityFailureKind.ProtocolMismatch,
                $"Gateway compatibility check failed: expected protocol v{ProtocolGeneration}, received {actual}.");
        }

        var installedVersion = config.Gateway.InstalledVersion;
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                "Gateway installed version was not recorded before the compatibility handshake.");
        }

        if (!string.Equals(gatewaySelf.ServerVersion, installedVersion, StringComparison.Ordinal))
        {
            var actual = string.IsNullOrWhiteSpace(gatewaySelf.ServerVersion) ? "missing" : gatewaySelf.ServerVersion;
            return Failure(
                GatewayCompatibilityFailureKind.ServerVersionMismatch,
                $"Gateway compatibility check failed: installed version {installedVersion}, server reported {actual}.");
        }

        return null;
    }

    public static bool CanRetryWithFallback(
        SetupConfig config,
        GatewayCompatibilityFailureKind failureKind) =>
        failureKind is GatewayCompatibilityFailureKind.InstalledVersionMismatch
            or GatewayCompatibilityFailureKind.ProtocolMismatch
            or GatewayCompatibilityFailureKind.ServerVersionMismatch &&
        IsOfficialInstallerUrl(config.Gateway.InstallUrl) &&
        GatewayReleaseVersion.TryParse(config.Gateway.FallbackVersion, out _) &&
        !string.Equals(config.Gateway.Version, config.Gateway.FallbackVersion, StringComparison.Ordinal) &&
        !string.Equals(config.Gateway.InstalledVersion, config.Gateway.FallbackVersion, StringComparison.Ordinal);

    public static bool TryApplyFallback(SetupConfig config, out string? error)
    {
        if (!IsOfficialInstallerUrl(config.Gateway.InstallUrl))
        {
            error = "Custom Gateway installers do not use Gateway.FallbackVersion.";
            return false;
        }

        var fallbackVersion = config.Gateway.FallbackVersion?.Trim();
        if (!GatewayReleaseVersion.TryParse(fallbackVersion, out _))
        {
            error = "Gateway.FallbackVersion must be an exact stable OpenClaw version.";
            return false;
        }

        if (string.Equals(config.Gateway.Version, fallbackVersion, StringComparison.Ordinal) ||
            string.Equals(config.Gateway.InstalledVersion, fallbackVersion, StringComparison.Ordinal))
        {
            error = $"Gateway {fallbackVersion} is already selected.";
            return false;
        }

        config.Gateway.Selection = null;
        config.Gateway.Version = fallbackVersion;
        config.Gateway.InstalledVersion = null;
        error = null;
        return true;
    }

    private static string? ResolveLegacySelection(GatewayConfig gateway)
    {
        var selection = gateway.Selection?.Trim();
        var version = gateway.Version?.Trim();
        gateway.Selection = null;

        if (string.IsNullOrWhiteSpace(selection))
            return version;

        if (selection.Equals("recommended", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(version)
                ? LegacyRecommendedVersion
                : version;

        if (selection.Equals("exact", StringComparison.OrdinalIgnoreCase))
        {
            RequireExactVersion(version, "Legacy Gateway selection 'exact' requires Gateway.Version.");
            return version;
        }

        if (selection.Equals("fallback", StringComparison.OrdinalIgnoreCase))
        {
            var legacyFallback = string.IsNullOrWhiteSpace(version)
                ? LegacyFallbackVersion
                : version;
            RequireExactVersion(
                legacyFallback,
                "Legacy Gateway selection 'fallback' requires an exact stable Gateway.Version.");
            return legacyFallback;
        }

        throw Failure(
            GatewayCompatibilityFailureKind.InvalidPolicy,
            $"Invalid legacy Gateway.Selection '{selection}'. Use recommended, fallback, or exact.");
    }

    private static string? NormalizeOfficialSelector(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return null;

        var normalized = selector.Trim();
        if (GatewayPackageVersion.IsExact(normalized))
            return normalized;

        if (normalized.Equals("stable", StringComparison.OrdinalIgnoreCase))
            return "latest";

        if (s_supportedTags.Contains(normalized))
            return normalized.ToLowerInvariant();

        throw Failure(
            GatewayCompatibilityFailureKind.InvalidPolicy,
            $"Gateway version selector '{normalized}' is invalid. Use latest, stable, extended-stable, beta, dev, next, or an exact OpenClaw package version.");
    }

    private static string? NormalizeFallbackVersion(string? fallbackVersion)
    {
        if (string.IsNullOrWhiteSpace(fallbackVersion))
            return null;

        fallbackVersion = fallbackVersion.Trim();
        RequireExactVersion(
            fallbackVersion,
            "Gateway.FallbackVersion must be an exact stable OpenClaw version.");
        return fallbackVersion;
    }

    private static void RequireExactVersion(string? version, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw Failure(GatewayCompatibilityFailureKind.InvalidPolicy, missingMessage);

        if (!GatewayReleaseVersion.TryParse(version, out _))
        {
            throw Failure(
                GatewayCompatibilityFailureKind.InvalidPolicy,
                $"Gateway version '{version}' is not an exact stable release. Prerelease channels are not eligible.");
        }
    }

    private static GatewayCompatibilityException Failure(
        GatewayCompatibilityFailureKind kind,
        string message) =>
        new(kind, message);
}
