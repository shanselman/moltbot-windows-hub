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

public static class GatewayInstallPolicy
{
    public const string DefaultInstallUrl = "https://openclaw.ai/install-cli.sh";
    public const int ProtocolGeneration = 4;
    public const string NodeVersion = "24.19.0";

    public static bool IsOfficialInstallerUrl(string? installUrl) =>
        string.IsNullOrWhiteSpace(installUrl) ||
        string.Equals(installUrl, DefaultInstallUrl, StringComparison.OrdinalIgnoreCase);

    public static void ValidateAndApply(SetupConfig config, bool allowExactCandidate = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var gateway = config.Gateway;
        var requestedVersion = gateway.Version?.Trim();
        var customInstaller = !IsOfficialInstallerUrl(gateway.InstallUrl);

        if (customInstaller)
        {
            RequireExactVersion(requestedVersion, "Custom Gateway installer URLs require an exact stable Gateway.Version.");
            gateway.Version = requestedVersion;
            return;
        }

        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            if (!allowExactCandidate)
            {
                throw Failure(
                    GatewayCompatibilityFailureKind.InvalidPolicy,
                    "Official Gateway installs use the npm latest tag. Gateway.Version is reserved for explicit candidate validation.");
            }

            RequireExactVersion(requestedVersion, "Gateway candidate validation requires an exact stable Gateway.Version.");
            gateway.Version = requestedVersion;
        }
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

        var installedVersion = config.Gateway.Version;
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
