using System.Text.Json;
using System.Runtime.Versioning;
using OpenClaw.Shared.Inference.Catalog;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class SetupConfigTests : IDisposable
{
    private readonly string _tempDir;

    public SetupConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_AreReasonable()
    {
        var config = new SetupConfig();
        Assert.Equal("OpenClawGateway", config.DistroName);
        Assert.Equal(18789, config.GatewayPort);
        Assert.Equal("Ubuntu-24.04", config.BaseDistro);
        Assert.False(config.Headless);
        Assert.False(config.DryRun);
        Assert.Equal("trace", config.LogLevel);
        Assert.False(config.RollbackOnFailure);
        Assert.Equal("loopback", config.Gateway.Bind);
        Assert.Equal("hybrid", config.Gateway.ReloadMode);
        Assert.False(config.SkipPermissions);
        Assert.False(config.SkipWizard);
        Assert.True(config.WindowsNodeContext.Enabled);
        Assert.Null(config.WindowsNodeContext.WorkspacePath);
        Assert.Equal(180, config.WindowsNodeContext.TimeoutSeconds);
        Assert.False(config.Tailscale.Enabled);
        Assert.False(config.Tailscale.TrustTailscaleAuth);
        Assert.Equal(TailscaleAuthMode.Browser, config.Tailscale.AuthMode);
        Assert.Equal(300, config.Tailscale.AuthTimeoutSeconds);
        Assert.Equal(300, config.Tailscale.ServeApprovalTimeoutSeconds);
        Assert.False(config.LocalAi.Enabled);
    }

    [Fact]
    public void BundledConfig_RequiresExplicitLocalAiOptIn()
    {
        var config = SetupConfig.LoadFromFile(Path.Combine(
            RepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine",
            "default-config.json"));

        Assert.False(config.LocalAi.Enabled);
    }

    [Fact]
    public void ValidationPackagePath_IsRuntimeOnly()
    {
        var config = new SetupConfig
        {
            Gateway = new GatewayConfig { ValidationPackagePath = @"C:\candidate\openclaw-current.tgz" }
        };

        var json = JsonSerializer.Serialize(config, SetupConfig.JsonWriteOptions);
        var loaded = JsonSerializer.Deserialize<SetupConfig>(
            """{"Gateway":{"ValidationPackagePath":"C:\\untrusted\\candidate.tgz"}}""",
            SetupConfig.JsonOptions);

        Assert.DoesNotContain("ValidationPackagePath", json, StringComparison.Ordinal);
        Assert.NotNull(loaded);
        Assert.Null(loaded.Gateway.ValidationPackagePath);
    }

    [Fact]
    public void ApplyUiDefaults_EnablesRollbackAndClearsHeadless()
    {
        var config = new SetupConfig
        {
            Headless = true,
            RollbackOnFailure = false
        };

        config.ApplyUiDefaults();

        Assert.False(config.Headless);
        Assert.True(config.RollbackOnFailure);
    }

    [Fact]
    public void ApplyUiDefaults_AllowsRollbackOptOut()
    {
        var config = new SetupConfig { RollbackOnFailure = true };

        config.ApplyUiDefaults(rollbackOnFailure: false);

        Assert.False(config.Headless);
        Assert.False(config.RollbackOnFailure);
    }

    [Fact]
    public void EffectiveGatewayUrl_UsesPort()
    {
        var config = new SetupConfig { GatewayPort = 9999 };
        Assert.Equal("ws://127.0.0.1:9999", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void EffectiveGatewayUrl_PreferExplicitUrl()
    {
        var config = new SetupConfig { GatewayUrl = "ws://custom:1234" };
        Assert.Equal("ws://custom:1234", config.EffectiveGatewayUrl);
    }

    [Fact]
    public void TailscaleConfig_NormalizesHostnameAndRejectsIncompatibleGatewaySettings()
    {
        var config = new SetupConfig
        {
            GatewayUrl = "wss://external.example.test",
            Tailscale = new TailscaleConfig { Enabled = true, Hostname = "OpenClaw !!! Gateway" }
        };

        Assert.Equal("openclaw-gateway", config.Tailscale.EffectiveHostname);
        Assert.Contains("GatewayUrl", TailscaleSetupPolicy.ValidateConfig(config));

        config.GatewayUrl = null;
        config.Gateway.Bind = "lan";
        Assert.Contains("loopback", TailscaleSetupPolicy.ValidateConfig(config));

        config.Gateway.Bind = "loopback";
        config.BaseDistro = "Debian";
        Assert.Contains("Ubuntu-24.04", TailscaleSetupPolicy.ValidateConfig(config));
    }

    [Fact]
    public void TailscaleAuthKey_IsRuntimeOnlyAndStatusParsesMagicDns()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, AuthMode = TailscaleAuthMode.AuthKey, AuthKey = "tskey-auth-secret" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(config, SetupConfig.JsonWriteOptions);

        Assert.DoesNotContain("tskey-auth-secret", json);
        Assert.DoesNotContain("\"AuthKey\":", json);
        Assert.True(TailscaleSetupPolicy.TryParseStatus("""{"BackendState":"Running","Self":{"DNSName":"openclaw.tailnet.ts.net."}}""", out var status));
        Assert.True(status.IsRunning);
        Assert.Equal("tailnet.ts.net", TailscaleSetupPolicy.GetTailnetDnsSuffix(status.DnsName));
        Assert.Equal("openclaw-gateway", TailscaleSetupPolicy.NormalizeHostname("openclaw_gateway", "ignored"));

        Assert.True(TailscaleSetupPolicy.TryParseStatus("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/next-token","Health":["register request: http 410: auth path not found"]}""", out var staleAuthorization));
        Assert.True(staleAuthorization.HasExpiredAuthorizationPath);
        Assert.Equal("https://login.tailscale.com/a/next-token", staleAuthorization.AuthorizationUri?.AbsoluteUri);
    }

    [Fact]
    public void LoadFromFile_ParsesJson()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """
        {
            "DistroName": "TestDistro",
            "GatewayPort": 12345,
            "Headless": true,
            // comment support
            "Gateway": {
                "Bind": "localhost",
                "ReloadMode": "cold"
            }
        }
        """);

        var config = SetupConfig.LoadFromFile(path);
        Assert.Equal("TestDistro", config.DistroName);
        Assert.Equal(12345, config.GatewayPort);
        Assert.True(config.Headless);
        Assert.Equal("localhost", config.Gateway.Bind);
        Assert.Equal("cold", config.Gateway.ReloadMode);
    }

    [Fact]
    public void TryLoadFromFile_ReturnsErrorForJsonNull()
    {
        var path = Path.Combine(_tempDir, "null.json");
        File.WriteAllText(path, "null");

        var loaded = SetupConfig.TryLoadFromFile(path, out var config, out var error);

        Assert.False(loaded);
        Assert.Null(config);
        Assert.Equal("Config file must contain a JSON object.", error);
    }

    [Fact]
    public void TryLoadFromFile_ReturnsErrorForDirectoryPath()
    {
        var loaded = SetupConfig.TryLoadFromFile(_tempDir, out var config, out var error);

        Assert.False(loaded);
        Assert.Null(config);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FromEnvironment_OverridesDefaults()
    {
        // Set env vars temporarily
        var prevDistro = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_DISTRO");
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        var prevHeadless = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS");
        var prevTrustTailscaleAuth = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_TAILSCALE_TRUST_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", "EnvDistro");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "9876");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", "true");
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_TAILSCALE_TRUST_AUTH", "true");

            var config = SetupConfig.FromEnvironment();
            Assert.Equal("EnvDistro", config.DistroName);
            Assert.Equal(9876, config.GatewayPort);
            Assert.True(config.Headless);
            Assert.True(config.Tailscale.Enabled);
            Assert.True(config.Tailscale.TrustTailscaleAuth);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_DISTRO", prevDistro);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_HEADLESS", prevHeadless);
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_TAILSCALE_TRUST_AUTH", prevTrustTailscaleAuth);
        }
    }

    [Fact]
    public void FromEnvironment_InvalidPort_KeepsDefault()
    {
        var prevPort = Environment.GetEnvironmentVariable("OPENCLAW_SETUP_PORT");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", "notanumber");
            var config = SetupConfig.FromEnvironment();
            Assert.Equal(18789, config.GatewayPort); // default
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_SETUP_PORT", prevPort);
        }
    }

    [Fact]
    public void CapabilitiesConfig_DefaultsEnableExpectedCategories()
    {
        var caps = new CapabilitiesConfig();
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.Contains("system", categories);
        Assert.Contains("canvas", categories);
        Assert.Contains("screen", categories);
        Assert.Contains("device", categories);
        Assert.Contains("tts", categories);
        Assert.Contains("stt", categories);
    }

    [Fact]
    public void CapabilitiesConfig_DefaultOrderMatchesTrayRegistrationOrder()
    {
        var caps = new CapabilitiesConfig();

        Assert.Equal(
            ["system", "canvas", "screen", "camera", "location", "tts", "stt", "device", "browser"],
            caps.GetEnabledCapabilities().Select(c => c.Category).ToArray());
    }

    [Fact]
    public void CapabilitiesConfig_GetEnabledCommandIds_FlattensEnabledCapabilities()
    {
        var caps = new CapabilitiesConfig
        {
            Camera = false,
            Stt = false
        };

        var commands = caps.GetEnabledCommandIds();

        Assert.Contains("system.notify", commands);
        Assert.Contains("tts.speak", commands);
        Assert.Contains("tts.status", commands);
        Assert.DoesNotContain("camera.snap", commands);
        Assert.DoesNotContain("stt.listen", commands);
        Assert.Equal(commands.Count, commands.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(commands.Order(StringComparer.OrdinalIgnoreCase), commands);
    }

    [Fact]
    public void CapabilitiesConfig_DisabledCategory_NotInList()
    {
        var caps = new CapabilitiesConfig { System = false, Canvas = false };
        var enabled = caps.GetEnabledCapabilities();
        var categories = enabled.Select(c => c.Category).ToList();

        Assert.DoesNotContain("system", categories);
        Assert.DoesNotContain("canvas", categories);
        Assert.Contains("screen", categories);
    }

    [Fact]
    public void TraySettingsConfig_MergesIntoFile_OverwritesSetupKeysAndPreservesUnknownKeys()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"CustomKey": "custom_value", "EnableNodeMode": false, "AutoStart": true, "NodeCameraEnabled": false}""");

        var traySettings = new TraySettingsConfig { EnableNodeMode = true, AutoStart = false, NodeCameraEnabled = false };
        traySettings.MergeIntoSettingsFile(settingsPath);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("EnableManagedLocalGatewayAutoRepair").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.Equal("custom_value", result.RootElement.GetProperty("CustomKey").GetString());
    }

    [Fact]
    public void TraySettingsConfig_ExplicitAutoRepairChoice_IsPersistedForFreshSetup()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var traySettings = new TraySettingsConfig
        {
            EnableManagedLocalGatewayAutoRepair = false,
        };

        traySettings.MergeIntoSettingsFile(settingsPath);

        using var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(
            result.RootElement
                .GetProperty("EnableManagedLocalGatewayAutoRepair")
                .GetBoolean());
    }

    [Fact]
    public void TraySettingsConfig_SetupRerun_PreservesExistingAutoRepairKillSwitch()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            """{"EnableManagedLocalGatewayAutoRepair":false}""");

        new TraySettingsConfig().MergeIntoSettingsFile(settingsPath);

        using var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(
            result.RootElement
                .GetProperty("EnableManagedLocalGatewayAutoRepair")
                .GetBoolean());
    }

    [Fact]
    public void TraySettingsConfig_ApplyCapabilities_MapsSetupCapabilitiesToRuntimeNodeSettings()
    {
        var caps = new CapabilitiesConfig
        {
            System = false,
            Canvas = true,
            Screen = true,
            Camera = false,
            Location = false,
            Browser = false,
            Device = true,
            Tts = true,
            Stt = false,
        };

        var traySettings = new TraySettingsConfig();
        traySettings.ApplyCapabilities(caps);

        Assert.False(traySettings.NodeSystemRunEnabled);
        Assert.True(traySettings.NodeCanvasEnabled);
        Assert.True(traySettings.NodeScreenEnabled);
        Assert.False(traySettings.NodeCameraEnabled);
        Assert.False(traySettings.NodeLocationEnabled);
        Assert.False(traySettings.NodeBrowserProxyEnabled);
        Assert.True(traySettings.NodeTtsEnabled);
        Assert.False(traySettings.NodeSttEnabled);
    }

    /// <summary>
    /// The install-review card's WSL title/description in CapabilitiesPage.xaml is a design-time
    /// placeholder that CapabilitiesPage.xaml.cs immediately overwrites at runtime with
    /// SetupReviewSummaryBuilder's DistroTitle/DistroDescription. This pins the default-config
    /// runtime text to the same simplified copy so the two cannot drift again.
    /// </summary>
    [Fact]
    public void SetupReviewSummary_DistroTitleAndDescription_MatchSimplifiedReviewCopy()
    {
        var summary = SetupReviewSummaryBuilder.Build(new SetupConfig());

        Assert.Equal("Install Ubuntu 24.04 in WSL", summary.DistroTitle);
        Assert.Equal("Creates a separate OpenClawGateway instance. Uses several GB.", summary.DistroDescription);
    }

    [Fact]
    public void SetupReviewSummary_UsesActiveSetupConfig()
    {
        var oldData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
        var oldLocalData = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", Path.Combine(_tempDir, "roaming"));
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", Path.Combine(_tempDir, "local"));
            LocalModelInfo qwen35B = LocalModelCatalog.Find(LocalModelCatalog.Qwen35BModelId)!;
            var config = new SetupConfig
            {
                DistroName = "CustomClaw",
                BaseDistro = "Debian",
                GatewayPort = 19999,
                Gateway =
                {
                    Bind = "lan",
                    InstallUrl = "https://example.test/install.sh",
                    Version = GatewayReleasePolicy.SecurityFloor
                },
                LocalAi =
                {
                    Enabled = true,
                    SelectedModelId = qwen35B.Id,
                    SelectedProfileId = LocalModelCatalog.GetProfiles(qwen35B)[1].Id,
                }
            };

            var summary = SetupReviewSummaryBuilder.Build(config);

            Assert.Contains("Debian", summary.DistroTitle);
            Assert.Contains("CustomClaw", summary.DistroDescription);
            Assert.Contains("several GB", summary.DistroDescription);
            Assert.Contains("19999", summary.GatewayEndpoint);
            Assert.Contains("LAN bind enabled", summary.GatewayDescription);
            Assert.Contains("example.test", summary.InstallerDescription);
            Assert.Contains("Unverified custom installer", summary.InstallerDescription);
            Assert.Contains("CustomClaw", summary.ExactCommands);
            Assert.Contains("19999", summary.ExactCommands);
            Assert.Equal("CustomClaw · LAN:19999", summary.CompletionGatewaySummary);
            Assert.Equal("Qwen 3.6 35B-A3B installed", summary.LocalAiTitle);
            Assert.StartsWith(
                "llama-server for Windows · loads on first request · ",
                summary.LocalAiDescription,
                StringComparison.Ordinal);
            Assert.Contains("256K context", summary.LocalAiDescription, StringComparison.Ordinal);
            Assert.Contains("Q8_0 target and MTP draft KV", summary.LocalAiDescription, StringComparison.Ordinal);
            Assert.DoesNotContain("full CUDA offload", summary.LocalAiDescription, StringComparison.Ordinal);
            Assert.DoesNotContain("immutable revision", summary.LocalAiDescription, StringComparison.Ordinal);
            Assert.DoesNotContain("llama-server b", summary.LocalAiDescription, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", oldLocalData);
        }
    }

    [Fact]
    public void SetupReviewSummary_UsesDiscoveredTailnetSuffix()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig
            {
                Enabled = true,
                Hostname = "openclaw-test",
                TailnetDnsSuffix = "example.ts.net"
            }
        };

        var summary = SetupReviewSummaryBuilder.Build(config);

        Assert.Equal("wss://openclaw-test.example.ts.net", summary.GatewayEndpoint);
        Assert.DoesNotContain("<tailnet>", summary.GatewayEndpoint);
        Assert.Contains("requires existing Companion token or device authentication", summary.GatewayDescription);
        Assert.Equal("OpenClawGateway · wss://openclaw-test.example.ts.net", summary.CompletionGatewaySummary);
    }

    [Fact]
    public void SetupReviewSummary_StatesWhenTailscaleAuthIsTrusted()
    {
        var config = new SetupConfig
        {
            Tailscale = new TailscaleConfig { Enabled = true, TrustTailscaleAuth = true }
        };

        var summary = SetupReviewSummaryBuilder.Build(config);

        Assert.Contains("trusts tailnet identity authentication", summary.GatewayDescription);
    }

    [Fact]
    public void SetupConfig_UsesBundledDefaultConfig_IsRuntimeOnly()
    {
        var config = new SetupConfig { UsesBundledDefaultConfig = true };
        var path = Path.Combine(_tempDir, "config.json");

        File.WriteAllText(path, JsonSerializer.Serialize(config, SetupConfig.JsonWriteOptions));
        var roundTripped = SetupConfig.LoadFromFile(path);

        Assert.False(roundTripped.UsesBundledDefaultConfig);
    }

    [Fact]
    public void TraySettingsConfig_UpdateAutoStartInSettingsFile_PreservesCapabilitySettings()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"AutoStart": false, "NodeCameraEnabled": false, "NodeSystemRunEnabled": false}""");

        TraySettingsConfig.UpdateAutoStartInSettingsFile(settingsPath, autoStart: true);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("AutoStart").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeCameraEnabled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("NodeSystemRunEnabled").GetBoolean());
    }

    [Fact]
    public void TraySettingsConfig_CorruptExistingFile_BacksUpAndThrows()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, "{not json");

        var ex = Assert.Throws<InvalidDataException>(() => new TraySettingsConfig().MergeIntoSettingsFile(settingsPath));

        Assert.Contains("settings.json is corrupt", ex.Message);
        Assert.Equal("{not json", File.ReadAllText(settingsPath));
        Assert.Single(Directory.EnumerateFiles(_tempDir, "settings.json.corrupt-*.bak"));
    }

    [Fact]
    public void TraySettingsConfig_CorruptExistingFile_BackupNamesDoNotCollide()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, "{not json");

        Assert.Throws<InvalidDataException>(() => new TraySettingsConfig().MergeIntoSettingsFile(settingsPath));
        Assert.Throws<InvalidDataException>(() => TraySettingsConfig.UpdateAutoStartInSettingsFile(settingsPath, autoStart: true));

        Assert.Equal(2, Directory.EnumerateFiles(_tempDir, "settings.json.corrupt-*.bak").Count());
    }

    [Fact]
    public void TraySettingsConfig_CreatesNewFile_WhenMissing()
    {
        var settingsPath = Path.Combine(_tempDir, "newsettings", "settings.json");
        var traySettings = new TraySettingsConfig();
        traySettings.MergeIntoSettingsFile(settingsPath);

        Assert.True(File.Exists(settingsPath));
        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeTtsEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("NodeSttEnabled").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_PreservesNodeSettings_WhenGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: true);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.True(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.True(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TrayArtifactCleanup_ResetOnboardingSettings_DisablesNodeSettings_WhenNoGatewaysRemain()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{"GatewayUrl": "ws://localhost:18789", "EnableNodeMode": true, "AutoStart": true}""");

        TrayArtifactCleanup.ResetOnboardingSettings(_tempDir, new SetupLogger(filePath: null), preserveNodeSettings: false);

        var result = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.False(result.RootElement.TryGetProperty("GatewayUrl", out _));
        Assert.False(result.RootElement.GetProperty("EnableNodeMode").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AutoStart").GetBoolean());
    }

    [Fact]
    public void WslConfig_Defaults()
    {
        var wsl = new WslConfig();
        Assert.Equal("openclaw", wsl.User);
        Assert.True(wsl.Systemd);
        Assert.False(wsl.Interop);
    }

    [Fact]
    public void PairingConfig_Defaults()
    {
        var pairing = new PairingConfig();
        Assert.Equal(60, pairing.TimeoutSeconds);
    }

    [Fact]
    public void WindowsNodeContextSection_ManagedBlock_ContainsMarkersAndPayload()
    {
        var block = WindowsNodeContextSection.ManagedBlock;

        Assert.StartsWith(WindowsNodeContextSection.BeginMarker + "\n", block);
        Assert.Contains("This WSL gateway may be paired", block);
        Assert.Contains("exec host=node", block);
        Assert.Contains("Share Windows Ollama", block);
        Assert.Contains("ollama.models", block);
        Assert.Contains("separate from the app-managed Local AI gateway provider", block);
        Assert.DoesNotContain("tools.exec.security full", block);
        Assert.DoesNotContain("tools.exec.ask off", block);
        Assert.EndsWith("\n" + WindowsNodeContextSection.EndMarker, block);
    }

    [Fact]
    public void StepResult_Ok_IsSuccess()
    {
        Assert.True(StepResult.Ok().IsSuccess);
        Assert.True(StepResult.Ok("msg").IsSuccess);
    }

    [Fact]
    public void StepResult_Skip_IsSuccess()
    {
        Assert.True(StepResult.Skip("reason").IsSuccess);
    }

    [Fact]
    public void StepResult_Fail_IsNotSuccess()
    {
        Assert.False(StepResult.Fail("err").IsSuccess);
    }

    [Fact]
    public void StepResult_Terminal_IsNotSuccess()
    {
        Assert.False(StepResult.Terminal("fatal").IsSuccess);
        Assert.Equal(StepOutcome.FailedTerminal, StepResult.Terminal("fatal").Outcome);
    }

    [Fact]
    public void StepResult_RestartRequired_IsTypedTerminal()
    {
        var result = StepResult.RestartRequired("restart Windows");
        var (outcome, message, error, detail) = result;

        Assert.False(result.IsSuccess);
        Assert.Equal(StepOutcome.FailedTerminal, outcome);
        Assert.Equal("restart Windows", message);
        Assert.Null(error);
        Assert.Null(detail);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public void PipelineResult_ExitCodes()
    {
        Assert.Equal(0, new PipelineResult(PipelineOutcome.Success).ExitCode);
        Assert.Equal(1, new PipelineResult(PipelineOutcome.Failed).ExitCode);
        Assert.Equal(3, new PipelineResult(PipelineOutcome.Cancelled).ExitCode);
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "src", "OpenClaw.SetupEngine", "default-config.json")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root for default-config.json.");
    }
}
