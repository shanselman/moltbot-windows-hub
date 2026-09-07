namespace OpenClaw.Connection;

/// <summary>
/// Immutable record representing a known gateway endpoint.
/// Stored in <c>gateways.json</c> via <see cref="GatewayRegistry"/>.
/// </summary>
public sealed record GatewayRecord
{
    /// <summary>Stable GUID, primary key.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gateway WebSocket URL (e.g. wss://gateway.example.com).</summary>
    public string Url { get; init; } = "";

    /// <summary>User-facing label (e.g. "Home Gateway").</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Long-lived shared token for any device.</summary>
    public string? SharedGatewayToken { get; init; }

    /// <summary>One-time bootstrap token for first-time pairing.</summary>
    public string? BootstrapToken { get; init; }

    /// <summary>Last successful connection time.</summary>
    public DateTime? LastConnected { get; init; }

    /// <summary>True for gateways provisioned locally (localhost/WSL).</summary>
    public bool IsLocal { get; init; }

    /// <summary>True when this gateway is known to require v2 auth signatures.</summary>
    public bool RequiresV2Signature { get; init; }

    /// <summary>WSL distro name for gateway records provisioned by SetupEngine.</summary>
    public string? SetupManagedDistroName { get; init; }

    /// <summary>Per-gateway SSH tunnel configuration. Null if no tunnel needed.</summary>
    public SshTunnelConfig? SshTunnel { get; init; }

    /// <summary>
    /// Per-gateway override for the local browser-control host port that the node-side
    /// <c>browser.proxy</c> capability connects to. Null (default) derives the port from the
    /// active gateway/tunnel (see <c>BrowserControlEndpoint</c>). Scoped to this gateway so a
    /// split/remote forward set up for one gateway cannot misroute when another is active.
    /// </summary>
    public int? BrowserControlPort { get; init; }

    /// <summary>
    /// Identity directory name, deterministically derived from Id.
    /// GUIDs are path-safe and guarantee uniqueness even if URLs change.
    /// </summary>
    public string IdentityDirName => Id;
}

/// <summary>
/// Helpers for the saved-gateway edit/connect flows, which rebuild a fresh
/// <see cref="GatewayRecord"/> from the form fields rather than mutating the stored one.
/// </summary>
public static class GatewayRecordEditing
{
    /// <summary>
    /// Carries forward advanced per-gateway fields that the edit/connect forms don't expose,
    /// so editing name / token / URL / SSH settings can't silently drop them. A value already
    /// set on the rebuilt record wins (the form changed it); otherwise the existing record's
    /// value is preserved. Covers <see cref="GatewayRecord.BrowserControlPort"/> and — when the
    /// gateway is still the same managed-local WSL gateway — the setup-managed ownership fields
    /// (<see cref="GatewayRecord.IsLocal"/>, <see cref="GatewayRecord.SetupManagedDistroName"/>,
    /// <see cref="GatewayRecord.RequiresV2Signature"/>). Preserving those keeps a managed gateway's
    /// keepalive and auto-repair working across an edit; dropping them silently disabled self-healing.
    /// "Same gateway" means the endpoint URL is unchanged (a name/token-only edit) or differs only by
    /// the standard localhost aliases <c>localhost</c>, <c>127.0.0.1</c>, and <c>::1</c>, with scheme,
    /// port, path, and query unchanged. If the user repoints the URL or adds a tunnel, the record becomes
    /// manual and all managed-ownership metadata is removed.
    /// </summary>
    public static GatewayRecord PreserveAdvancedFields(this GatewayRecord rebuilt, GatewayRecord? existing)
    {
        if (existing is null)
            return rebuilt;

        var result = rebuilt with { BrowserControlPort = rebuilt.BrowserControlPort ?? existing.BrowserControlPort };

        var stillSameManagedEndpoint = AreEquivalentManagedEndpoints(rebuilt.Url, existing.Url);
        var existingManagedDistroName = ResolveManagedDistroName(existing);
        var managedDistroName =
            rebuilt.SetupManagedDistroName ??
            existingManagedDistroName;

        if (existing.IsLocal &&
            managedDistroName is not null &&
            rebuilt.SshTunnel is null &&
            stillSameManagedEndpoint)
        {
            result = result with
            {
                IsLocal = true,
                // Migrate legacy "Local (<distro>)" ownership to the explicit durable marker.
                SetupManagedDistroName = managedDistroName,
                RequiresV2Signature = rebuilt.RequiresV2Signature || existing.RequiresV2Signature,
            };
        }
        else if (existingManagedDistroName is not null)
        {
            result = result with
            {
                IsLocal = OpenClaw.Shared.LocalGatewayUrlClassifier.IsLocalGatewayUrl(rebuilt.Url),
                SetupManagedDistroName = null,
                RequiresV2Signature = false,
                FriendlyName = ParseLegacyManagedDistroName(result.FriendlyName) is not null
                        ? null
                        : result.FriendlyName,
            };
        }

        return result;
    }

    public static bool AreEquivalentLoopbackEndpoints(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri) ||
            !leftUri.IsLoopback ||
            !rightUri.IsLoopback)
        {
            return false;
        }

        return AreEquivalentManagedEndpoints(leftUri, rightUri);
    }

    private static bool AreEquivalentManagedEndpoints(string? left, string? right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var leftUri) &&
        Uri.TryCreate(right, UriKind.Absolute, out var rightUri) &&
        AreEquivalentManagedEndpoints(leftUri, rightUri);

    private static bool AreEquivalentManagedEndpoints(Uri leftUri, Uri rightUri)
    {
        var hostsEquivalent =
            string.Equals(
                NormalizeHost(leftUri.Host),
                NormalizeHost(rightUri.Host),
                StringComparison.OrdinalIgnoreCase) ||
            (leftUri.IsLoopback &&
             rightUri.IsLoopback &&
             IsStandardLoopbackAlias(leftUri.Host) &&
             IsStandardLoopbackAlias(rightUri.Host));

        return hostsEquivalent &&
            string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            leftUri.Port == rightUri.Port &&
            string.Equals(leftUri.UserInfo, rightUri.UserInfo, StringComparison.Ordinal) &&
            string.Equals(leftUri.AbsolutePath, rightUri.AbsolutePath, StringComparison.Ordinal) &&
            string.Equals(leftUri.Query, rightUri.Query, StringComparison.Ordinal) &&
            string.Equals(leftUri.Fragment, rightUri.Fragment, StringComparison.Ordinal);
    }

    private static bool IsStandardLoopbackAlias(string host)
    {
        var normalized = NormalizeHost(host);
        return string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(normalized, "::1", StringComparison.Ordinal);
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.');

    public static bool IsLoopbackEndpoint(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static string? ResolveManagedDistroName(GatewayRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SetupManagedDistroName))
            return record.SetupManagedDistroName;

        if (!record.IsLocal ||
            !OpenClaw.Shared.LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url) ||
            ParseLegacyManagedDistroName(record.FriendlyName) is not { } distro)
        {
            return null;
        }

        return distro;
    }

    public static bool IsSetupManagedLocalRecord(GatewayRecord record)
    {
        if (record.SshTunnel is not null)
            return false;

        return ResolveManagedDistroName(record) is not null &&
            (record.IsLocal ||
             OpenClaw.Shared.LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url));
    }

    private static string? ParseLegacyManagedDistroName(string? friendlyName)
    {
        const string prefix = "Local (";
        if (string.IsNullOrWhiteSpace(friendlyName) ||
            !friendlyName.StartsWith(prefix, StringComparison.Ordinal) ||
            !friendlyName.EndsWith(')'))
        {
            return null;
        }

        var distro = friendlyName[prefix.Length..^1].Trim();
        return string.IsNullOrWhiteSpace(distro) ? null : distro;
    }
}

/// <summary>Per-gateway SSH tunnel configuration.</summary>
public sealed record SshTunnelConfig(
    string User,
    string Host,
    int RemotePort,
    int LocalPort,
    bool IncludeBrowserProxyForward = false,
    int SshPort = 22);
