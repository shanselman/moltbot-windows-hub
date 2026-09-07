using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class WslKeepAlivePolicy
{
    private static string DefaultSetupManagedDistroName => AppIdentity.SetupDistroName;
    private static string DefaultSetupManagedFriendlyName => $"Local ({AppIdentity.SetupDistroName})";

    public static bool ShouldStart(GatewayRecord? activeRecord, string? legacyGatewayUrl)
    {
        if (activeRecord is not null)
        {
            if (activeRecord.SshTunnel is not null)
                return false;

            return activeRecord.IsLocal
                || !string.IsNullOrWhiteSpace(activeRecord.SetupManagedDistroName)
                || LocalGatewayUrlClassifier.IsLocalGatewayUrl(activeRecord.Url);
        }

        return LocalGatewayUrlClassifier.IsLocalGatewayUrl(legacyGatewayUrl ?? string.Empty);
    }

    public static string? ResolveDistroName(
        GatewayRecord? activeRecord,
        string? setupStateDistroName,
        string? environmentOverride)
    {
        if (activeRecord is not null &&
            GatewayRecordEditing.ResolveManagedDistroName(activeRecord) is { } managedDistroName)
            return managedDistroName;

        if (!string.IsNullOrWhiteSpace(setupStateDistroName))
            return setupStateDistroName;

#if DEBUG || OPENCLAW_TRAY_TESTS
        if (!string.IsNullOrWhiteSpace(environmentOverride))
            return environmentOverride;
#endif

        return DefaultSetupManagedDistroName;
    }

    public static IReadOnlyList<string> FindStaleSetupManagedDistroNames(
        IEnumerable<GatewayRecord> records,
        IEnumerable<string> markerDistroNames,
        string? setupStateDistroName)
    {
        var managedLocalDistros = records
            .Where(IsSetupManagedLocalRecord)
            .Select(GetSetupManagedDistroName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = markerDistroNames
            .Append(setupStateDistroName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(distro => !managedLocalDistros.Contains(distro!))
            .Select(distro => distro!)
            .ToArray();
    }

    public static bool HasSetupManagedLocalGateway(IEnumerable<GatewayRecord>? records) =>
        records?.Any(IsSetupManagedLocalRecord) == true;

    public static bool IsKeepaliveCommandLine(string? commandLine, string distroName)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(distroName))
            return false;

        return WslCommandLineMatcher.IsKeepaliveForDistro(commandLine, distroName);
    }

    public static bool IsMarkedKeepaliveProcessIdentity(
        string? processName,
        DateTime processStartTimeUtc,
        DateTime markerStartTimeUtc)
    {
        return string.Equals(processName, "wsl", StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(
                (processStartTimeUtc -
                 DateTime.SpecifyKind(markerStartTimeUtc, DateTimeKind.Utc)).TotalSeconds) <= 5;
    }

    public static bool TryGetMarkerDistroName(string markerJson, out string distroName)
    {
        distroName = string.Empty;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(markerJson);
            if (!doc.RootElement.TryGetProperty("DistroName", out var distroElement))
                return false;

            distroName = distroElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(distroName);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static bool IsSetupManagedLocalRecord(GatewayRecord record)
        => GatewayRecordEditing.IsSetupManagedLocalRecord(record) ||
           (record.SshTunnel is null &&
            IsLegacyDefaultSetupManagedLocalRecord(record));

    public static bool IsSameSetupManagedGateway(
        GatewayRecord expected,
        GatewayRecord? current)
    {
        if (current is null ||
            !IsSetupManagedLocalRecord(expected) ||
            !IsSetupManagedLocalRecord(current) ||
            !string.Equals(expected.Id, current.Id, StringComparison.Ordinal) ||
            !string.Equals(expected.Url, current.Url, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
            GetSetupManagedDistroName(expected),
            GetSetupManagedDistroName(current),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSetupManagedDistroName(GatewayRecord record)
        => GatewayRecordEditing.ResolveManagedDistroName(record);

    private static bool IsLegacyDefaultSetupManagedLocalRecord(GatewayRecord record) =>
        record.IsLocal
        && LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url)
        && string.Equals(record.FriendlyName, DefaultSetupManagedFriendlyName, StringComparison.Ordinal);
}
