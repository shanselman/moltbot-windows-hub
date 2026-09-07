namespace OpenClaw.SetupEngine;

using OpenClaw.Shared.Inference.Catalog;

public sealed record SetupReviewSummary(
    string DistroTitle,
    string DistroDescription,
    string InstallerDescription,
    string InstallerBadge,
    string GatewayDescription,
    string GatewayEndpoint,
    string ExactCommands,
    string CompletionGatewaySummary)
{
    public bool LocalAiEnabled { get; init; }
    public string? LocalAiTitle { get; init; }
    public string? LocalAiDescription { get; init; }
}

public static class SetupReviewSummaryBuilder
{
    public static SetupReviewSummary Build(SetupConfig config, string? dataDir = null, string? localDataDir = null)
    {
        var distroName = Display(config.DistroName, "OpenClawGateway");
        var baseDistro = Display(config.BaseDistro, "Ubuntu-24.04");
        var gatewayBind = Display(config.Gateway.Bind, "loopback");
        var gatewayPort = config.GatewayPort;
        var installPath = Path.Combine(localDataDir ?? SetupContext.ResolveLocalDataDir(), "wsl", distroName);
        var gatewayDataPath = Path.Combine(dataDir ?? SetupContext.ResolveDataDir(), "gateways.json");
        var release = config.Gateway.ResolvedRelease ?? GatewayReleasePolicy.ResolveAndApply(config);
        var installUrl = config.Gateway.InstallUrl ?? GatewayReleasePolicy.DefaultInstallUrl;
        var installerHost = TryGetHttpsHost(installUrl);
        var installerDescription = installerHost is null
            ? "Installer URL is not HTTPS; setup will stop before downloading anything."
            : release.IsCustomInstaller
                ? $"Unverified custom installer from {installerHost}; exact Gateway {release.Version}, protocol v{release.ProtocolGeneration} is checked after install."
                : $"Official Gateway {release.Version}; validated for protocol v{release.ProtocolGeneration} and fetched over HTTPS from {installerHost}.";
        var installerBadge = installerHost is null
            ? "Invalid URL"
            : release.IsCustomInstaller ? "Custom" : $"v{release.ProtocolGeneration} validated";
        var isLanBind = gatewayBind.Equals("lan", StringComparison.OrdinalIgnoreCase);
        var tailscaleEnabled = config.Tailscale.Enabled;
        var tailnetDnsSuffix = config.Tailscale.TailnetDnsSuffix?.Trim().Trim('.');
        var tailscaleEndpoint = string.IsNullOrWhiteSpace(tailnetDnsSuffix)
            ? $"wss://{config.Tailscale.EffectiveHostname}.<tailnet>.ts.net"
            : $"wss://{config.Tailscale.EffectiveHostname}.{tailnetDnsSuffix}";
        var gatewayDescription = tailscaleEnabled
            ? config.Tailscale.TrustTailscaleAuth
                ? "Tailscale Serve enabled: the gateway stays loopback-only, trusts tailnet identity authentication, and Companion connects over private HTTPS/WSS."
                : "Tailscale Serve enabled: the gateway stays loopback-only, requires existing Companion token or device authentication, and connects over private HTTPS/WSS."
            : isLanBind
            ? "LAN bind enabled: reachable from this PC and your local network according to Windows firewall/routing."
            : "Loopback only. It is not reachable from your network or the internet.";
        var gatewayEndpoint = tailscaleEnabled
            ? tailscaleEndpoint
            : isLanBind ? $"LAN:{gatewayPort}" : $"127.0.0.1:{gatewayPort}";
        var wslCommand = "wsl " + string.Join(' ', WslInstallSupport.BuildDirectInstallArgs(baseDistro, distroName, installPath));
        var installCommand = installerHost is null
            ? "setup stops before CLI download: installer URL must use HTTPS"
            : InstallCliStep.BuildInstallCommandPreview(
                installUrl,
                release.Version,
                release.IsCustomInstaller ? null : GatewayReleasePolicy.NodeVersion);
        LocalModelInfo localAiModel =
            LocalModelCatalog.Find(config.LocalAi.SelectedModelId) ?? LocalModelCatalog.Default;
        LocalInferenceRunProfile? localAiProfile =
            LocalModelCatalog.FindProfile(localAiModel, config.LocalAi.SelectedProfileId);
        string[] localAiCommands = config.LocalAi.Enabled
            ?
            [
                "download verified llama-server + CUDA runtime for Windows",
                $"download {localAiModel.Weights.RelativePath} from Hugging Face revision " +
                    ((HuggingFaceRevisionSource)localAiModel.Weights.Source).RevisionSha,
                $"llama-server router on dynamic 127.0.0.1 port; model loads on first request",
                $"openclaw provider llamacpp -> /v1; primary llamacpp/{localAiModel.Id}",
            ]
            : [];

        var summary = new SetupReviewSummary(
            DistroTitle: $"Install {baseDistro.Replace('-', ' ')} in WSL",
            DistroDescription: $"Creates a separate {distroName} instance. Uses several GB.",
            InstallerDescription: installerDescription,
            InstallerBadge: installerBadge,
            GatewayDescription: gatewayDescription,
            GatewayEndpoint: gatewayEndpoint,
            ExactCommands: string.Join(
                Environment.NewLine,
                new[]
                {
                    wslCommand,
                    installCommand,
                    $"openclaw config set gateway.bind {gatewayBind} · port {gatewayPort}",
                    tailscaleEnabled
                        ? config.Tailscale.TrustTailscaleAuth
                            ? "install signed Tailscale package · root owns tailscale up/serve · identity auth enabled"
                            : "install signed Tailscale package · root owns tailscale up/serve"
                        : null,
                    "openclaw gateway install --force   (systemd --user service)",
                }.Concat(localAiCommands).Concat(new[]
                {
                    $"writes -> {installPath}",
                    $"writes -> {gatewayDataPath} + identity"
                }).Where(line => line is not null)),
            CompletionGatewaySummary: $"{distroName} · {gatewayEndpoint}");
        return summary with
        {
            LocalAiEnabled = config.LocalAi.Enabled,
            LocalAiTitle = config.LocalAi.Enabled
                ? $"{DisplayModelName(localAiModel)} installed"
                : null,
            LocalAiDescription = config.LocalAi.Enabled
                ? localAiProfile is null
                    ? "llama-server for Windows · loads on first request · " +
                        "context and KV profile selected from detected GPU"
                    : "llama-server for Windows · loads on first request · " +
                        $"{FormatContext(localAiProfile.ContextTokens)} context · " +
                        $"{FormatKvCache(localAiProfile)}"
                : null,
        };
    }

    public static string DisplayModelName(LocalModelInfo model)
    {
        string displayName = model.DisplayName;
        int detailStart = displayName.IndexOf(" (", StringComparison.Ordinal);
        if (detailStart >= 0)
            displayName = displayName[..detailStart];
        if (displayName.StartsWith("Qwen", StringComparison.Ordinal) &&
            displayName.Length > 4 && char.IsDigit(displayName[4]))
        {
            displayName = displayName.Insert(4, " ");
        }
        return displayName;
    }

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K"
            : tokens % 1000 == 0
                ? $"{tokens / 1000}K"
                : $"{tokens:N0} tokens";

    private static string FormatKvCache(LocalInferenceRunProfile profile)
    {
        string target = LocalModelCatalog.ToDisplayCacheType(profile.KeyCachePrecision);
        string draft = LocalModelCatalog.ToDisplayCacheType(profile.DraftKeyCachePrecision);
        return target == draft
            ? $"{target} target and MTP draft KV"
            : $"{target} target KV and {draft} MTP draft KV";
    }

    private static string Display(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? TryGetHttpsHost(string installUrl)
        => Uri.TryCreate(installUrl, UriKind.Absolute, out var uri)
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.Host
            : null;
}
