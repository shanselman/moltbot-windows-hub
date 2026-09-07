using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// Tests for <see cref="MxcConfigBuilder"/>. Includes:
/// <list type="bullet">
/// <item>Golden tests vs SDK output captured by <c>tools/mxc/dump-sdk-config.cjs</c>.</item>
/// <item>Per-Sandbox-UI-setting audit tests.</item>
/// <item>Round-trip JSON shape (camelCase, no orphan fields).</item>
/// <item>Explicit env limitation for the Windows MXC 0.7 processcontainer backend.</item>
/// <item><c>ResolveToolDirsFromPath</c> via synthetic PATH.</item>
/// </list>
/// </summary>
public class MxcConfigBuilderTests
{
    private const string GoldenContainerId = "golden-test";

    // The harness used these placeholder paths; mirror them here so the C#
    // builder's filesystem grants match the golden fixtures.
    private static class P
    {
        public const string Documents = "C:\\Golden\\Documents";
        public const string Downloads = "C:\\Golden\\Downloads";
        public const string Desktop   = "C:\\Golden\\Desktop";
        public const string Custom    = "C:\\Golden\\Custom";
        public const string Settings  = "C:\\Golden\\Settings";
        public const string Ssh       = "C:\\Golden\\.ssh";
        public const string Chrome    = "C:\\Golden\\Chrome";
        public const string Edge      = "C:\\Golden\\Edge";
        public const string Brave     = "C:\\Golden\\Brave";
        public const string Firefox   = "C:\\Golden\\Firefox";
        public const string PsRead    = "C:\\Golden\\PSReadLine";
        public const string Scratch   = "C:\\Golden\\Scratch";
    }

    private static readonly string[] AlwaysDenied =
    {
        P.Settings, P.Ssh, P.Chrome, P.Edge, P.Brave, P.Firefox, P.PsRead,
    };

    private static SandboxPolicy LockedDownPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: Array.Empty<string>(),
            ReadonlyPaths: Array.Empty<string>(),
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: false, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.None, AllowInputInjection: false),
        TimeoutMs: 30_000);

    private static SandboxPolicy BalancedPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: Array.Empty<string>(),
            ReadonlyPaths: new[] { P.Documents, P.Downloads, P.Desktop },
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
        TimeoutMs: 60_000);

    private static SandboxPolicy PermissivePolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: new[] { P.Documents, P.Downloads, P.Desktop },
            ReadonlyPaths: Array.Empty<string>(),
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.All, AllowInputInjection: false),
        TimeoutMs: 300_000);

    private static SandboxPolicy CustomPolicy() => new(
        Version: MxcPolicyBuilder.SupportedPolicyVersion,
        Filesystem: new FilesystemPolicy(
            ReadwritePaths: new[] { P.Custom },
            ReadonlyPaths: new[] { P.Documents },
            DeniedPaths: AlwaysDenied,
            ClearPolicyOnExit: true),
        Network: new NetworkPolicy(AllowOutbound: true, AllowLocalNetwork: false),
        Ui: new UiPolicy(AllowWindows: false, Clipboard: ClipboardPolicy.All, AllowInputInjection: false),
        TimeoutMs: 60_000);

    private static SandboxExecutionRequest RequestFor(SandboxPolicy policy) => new(
        CapabilityCommand: "system.run",
        Args: JsonDocument.Parse("{}").RootElement,
        Policy: policy,
        TimeoutMs: 0); // explicitly zero so timeout in builder uses request.TimeoutMs=0 → default 30s

    private static MxcConfig BuildConfig(
        SandboxExecutionRequest request,
        string scratchDir = P.Scratch,
        string? containerId = null,
        string? pathEnvVar = "",
        Func<string, bool>? readonlyGrantIsBackendSafe = null) =>
        MxcConfigBuilder.Build(
            request,
            scratchDir,
            new MxcConfigBuildContext(
                ContainerId: containerId,
                PathEnvVar: pathEnvVar,
                ReadonlyGrantIsBackendSafe: readonlyGrantIsBackendSafe));

    private static string ExpectedSystemCmdExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "cmd.exe"
            : Path.Combine(systemRoot, "System32", "cmd.exe");
    }

    [Theory]
    [InlineData("locked-down", "LockedDown")]
    [InlineData("balanced", "Balanced")]
    [InlineData("permissive", "Permissive")]
    [InlineData("custom", "Custom")]
    public void BuiltConfig_MatchesSdkGolden(string preset, string presetMethod)
    {
        SandboxPolicy policy = presetMethod switch
        {
            "LockedDown" => LockedDownPolicy(),
            "Balanced"   => BalancedPolicy(),
            "Permissive" => PermissivePolicy(),
            _            => CustomPolicy(),
        };

        // The golden test reproduces the exact harness recipe: no commandLine,
        // no cwd, no shell PATH dirs. We pass an empty PATH and no
        // caller env. The C# config still emits env: [] to make the sandbox env
        // boundary explicit and bootstraps shell env in commandLine; the
        // harness stripped process.* (commandLine, cwd, env, timeout), so do
        // the same on the C# side before comparing. Use cmd so this pure
        // policy golden stays independent from shell command-line encoding.
        using var argsDoc = JsonDocument.Parse("""{"shell":"cmd"}""");
        var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };
        var config = BuildConfig(
            request,
            scratchDir: P.Scratch,
            containerId: GoldenContainerId,
            pathEnvVar: "");

        // Strip the process bag the harness dropped, plus normalize containerId.
        var stripped = config with
        {
            ContainerId = $"golden-{preset}",
            Process = config.Process with { CommandLine = "", Cwd = null, Env = null, TimeoutMs = null },
            Filesystem = config.Filesystem is null ? null : config.Filesystem with
            {
                ReadonlyPaths = config.Filesystem.ReadonlyPaths?
                    .Where(p => !IsDriveRoot(p))
                    .ToArray(),
            },
        };

        var actualJson = JsonSerializer.Serialize(stripped, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        // Drop "commandLine":"" because the SDK output has the process bag completely empty.
        using var actualDoc = JsonDocument.Parse(actualJson);
        var actualObj = JsonObjectNode.FromJsonElement(actualDoc.RootElement);
        if (actualObj.Contains("process") && actualObj["process"] is JsonObjectNode procNode &&
            procNode.TryGetString("commandLine", out var cmd) && cmd == string.Empty)
        {
            procNode.Remove("commandLine");
        }

        var goldenPath = ResolveGoldenPath(preset);
        var expectedJson = File.ReadAllText(goldenPath);
        using var expectedDoc = JsonDocument.Parse(expectedJson);
        var expectedObj = JsonObjectNode.FromJsonElement(expectedDoc.RootElement);

        // Deep structural equality, tolerant of property order.
        AssertJsonEqual(expectedObj, actualObj, path: "$");
    }

    private static string ResolveGoldenPath(string preset)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Mxc", "Golden", $"sdk-config-{preset}.json");
        if (File.Exists(local)) return local;
        // Fallback: walk up from the assembly dir to find the source path.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var probe = Path.Combine(dir, "tests", "OpenClaw.Shared.Tests", "Mxc", "Golden", $"sdk-config-{preset}.json");
            if (File.Exists(probe)) return probe;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Golden not found for preset {preset}");
    }

    [Fact]
    public void Build_OutboundOn_AddsInternetClientCapability()
    {
        var policy = BalancedPolicy();
        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");
        Assert.Contains("internetClient", config.ProcessContainer!.Capabilities!);
        Assert.Equal("allow", config.Network!.DefaultPolicy);
    }

    [Fact]
    public void Build_OutboundOff_OmitsInternetClient_AndNetworkBlocks()
    {
        var policy = LockedDownPolicy();
        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");
        Assert.DoesNotContain("internetClient", config.ProcessContainer!.Capabilities!);
        Assert.Equal("block", config.Network!.DefaultPolicy);
    }

    [Theory]
    [InlineData(ClipboardPolicy.None,  "none")]
    [InlineData(ClipboardPolicy.Read,  "read")]
    [InlineData(ClipboardPolicy.Write, "write")]
    [InlineData(ClipboardPolicy.All,   "all")]
    public void Build_ClipboardMode_RoundTripsToWxcExecString(ClipboardPolicy mode, string expected)
    {
        var policy = LockedDownPolicy() with { Ui = new UiPolicy(false, mode, false) };
        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");
        Assert.Equal(expected, config.Ui!.Clipboard);
    }

    [Fact]
    public void Build_AddsScratchDirToReadwritePaths()
    {
        var config = BuildConfig(RequestFor(BalancedPolicy()), pathEnvVar: "");
        Assert.Contains(P.Scratch, config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void WithNonCascadingVolumeRootGrants_AddsSystemVolumeRoot()
    {
        var original = BuildConfig(RequestFor(BalancedPolicy()), pathEnvVar: "");
        var config = MxcConfigBuilder.WithNonCascadingVolumeRootGrants(original);

        Assert.DoesNotContain(
            @"C:\",
            original.Filesystem!.ReadonlyPaths!,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            @"C:\",
            config.Filesystem!.ReadonlyPaths!,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_BaseContainer_AddsEveryGrantedVolumeRoot()
    {
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: [@"D:\Workspace"],
                ReadonlyPaths: [@"E:\Inputs"],
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var original = BuildConfig(RequestFor(policy), pathEnvVar: "");
        var config = MxcConfigBuilder.WithNonCascadingVolumeRootGrants(original);

        Assert.Contains(@"C:\", config.Filesystem!.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"D:\", config.Filesystem.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"E:\", config.Filesystem.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RejectsExplicitEnvironmentUntilBackendSupportsIt()
    {
        var request = RequestFor(BalancedPolicy()) with
        {
            Env = new Dictionary<string, string>
            {
                ["TEMP"] = "C:\\real-temp",
                ["TMP"] = "C:\\real-tmp",
                ["TMPDIR"] = "C:\\real-tmpdir",
            },
        };

        var ex = Assert.Throws<NotSupportedException>(() =>
            BuildConfig(request, pathEnvVar: ""));
        Assert.Contains("Explicit environment variables", ex.Message);
    }

    [Fact]
    public void Build_AutoGrantsCwdAsReadonly_WhenNotAlreadyCovered()
    {
        var request = RequestFor(BalancedPolicy()) with { Cwd = "C:\\unrelated\\workdir" };
        var config = BuildConfig(request, pathEnvVar: "");
        Assert.Contains("C:\\unrelated\\workdir", config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain("C:\\unrelated\\workdir", config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void Build_DoesNotDowngradeCwd_WhenAlreadyCoveredByReadwrite()
    {
        var policy = PermissivePolicy(); // Documents already in readwrite
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Documents, "subfolder") };
        var config = BuildConfig(request, pathEnvVar: "");

        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void Build_DoesNotAutoGrantCwd_WhenAlreadyCovered()
    {
        var policy = BalancedPolicy(); // Documents already in readonly
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Documents, "subfolder") };
        var config = BuildConfig(request, pathEnvVar: "");
        // Should not have added the subfolder explicitly (parent already grants).
        Assert.DoesNotContain(Path.Combine(P.Documents, "subfolder"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_DoesNotAutoGrantCwd_WhenOverlapsDenied()
    {
        var policy = BalancedPolicy();
        var request = RequestFor(policy) with { Cwd = Path.Combine(P.Ssh, "keys") };
        var config = BuildConfig(request, pathEnvVar: "");
        Assert.DoesNotContain(Path.Combine(P.Ssh, "keys"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_OmitsDeniedPathsBeforeBackendEmissionButStillFiltersAllows()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return;

        var chromeProfile = Path.Combine(localAppData, "Google", "Chrome", "User Data");
        var sshPath = Path.Combine(userProfile, ".ssh");
        var settingsDeny = Path.Combine(P.Settings, "openclaw-settings");
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { chromeProfile, userProfile },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: new[] { chromeProfile, sshPath, settingsDeny },
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");

        Assert.DoesNotContain(chromeProfile, config.Filesystem!.ReadwritePaths!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(userProfile, config.Filesystem.ReadwritePaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Null(config.Filesystem.DeniedPaths);
    }

    [Fact]
    public void Build_RemovesParentReadwriteGrantContainingDeniedSettingsDirectory_WhenDeniedPathsAreNotEmitted()
    {
        var parent = "C:\\Users\\example\\AppData\\Roaming";
        var settingsDeny = Path.Combine(parent, "OpenClawTray");
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { parent, settingsDeny },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: new[] { settingsDeny },
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = BuildConfig(
            RequestFor(policy),
            pathEnvVar: "");

        Assert.DoesNotContain(parent, config.Filesystem!.ReadwritePaths!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(settingsDeny, config.Filesystem.ReadwritePaths!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(settingsDeny, config.Filesystem.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Null(config.Filesystem.DeniedPaths);
    }

    [Fact]
    public void Build_RemovesParentReadonlyGrantContainingDeniedSettingsDirectory_WhenDeniedPathsAreNotEmitted()
    {
        var parent = "C:\\Users\\example\\AppData\\Roaming";
        var settingsDeny = Path.Combine(parent, "OpenClawTray");
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: Array.Empty<string>(),
                ReadonlyPaths: new[] { parent, settingsDeny },
                DeniedPaths: new[] { settingsDeny },
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = BuildConfig(
            RequestFor(policy),
            pathEnvVar: "");

        Assert.DoesNotContain(parent, config.Filesystem!.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(settingsDeny, config.Filesystem.ReadwritePaths!, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(settingsDeny, config.Filesystem.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        Assert.Null(config.Filesystem.DeniedPaths);
    }

    [Fact]
    public void ResolvePathDirsForShellPath_ReturnsExistingPathDirs()
    {
        // Synthesize an existing dir on PATH; ensure it shows up in the shell
        // PATH bootstrap.
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-tool-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var dirs = MxcConfigBuilder.ResolvePathDirsForShellPath(pathEnvVar: tempDir);
            Assert.Contains(tempDir, dirs);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_BootstrapsShellPathAndGrantsBackendSafePathDirsReadonly()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-path-env-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var argsDoc = JsonDocument.Parse("""{"command":"git --version","shell":"cmd"}""");
            var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: tempDir);

            Assert.NotNull(config.Process.Env);
            Assert.Empty(config.Process.Env);
            Assert.Contains(tempDir, config.Filesystem!.ReadonlyPaths!);
            Assert.Contains($"set \"TEMP={P.Scratch}\"", config.Process.CommandLine);
            Assert.Contains($"set \"TMP={P.Scratch}\"", config.Process.CommandLine);
            Assert.Contains($"set \"TMPDIR={P.Scratch}\"", config.Process.CommandLine);
            Assert.Contains($"set \"PATH={tempDir}\"", config.Process.CommandLine);
            Assert.Contains("git --version", config.Process.CommandLine);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_BootstrapsUnsafePathDirsWithoutGrantingThem()
    {
        var unsafeDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-path-env-unsafe-" + Guid.NewGuid().ToString("N"))).FullName;
        var safeDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-path-env-safe-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var argsDoc = JsonDocument.Parse("""{"command":"tool --version","shell":"cmd"}""");
            var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };
            var pathEnv = string.Join(Path.PathSeparator, unsafeDir, safeDir);

            var config = BuildConfig(
                request,
                pathEnvVar: pathEnv,
                readonlyGrantIsBackendSafe: dir => !string.Equals(dir, unsafeDir, StringComparison.OrdinalIgnoreCase));

            Assert.Contains($"set \"PATH={pathEnv}\"", config.Process.CommandLine);
            Assert.DoesNotContain(unsafeDir, config.Filesystem!.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(safeDir, config.Filesystem.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(unsafeDir, true); } catch { }
            try { Directory.Delete(safeDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_DefaultsMissingCwdToWritableScratchDirectory()
    {
        var config = BuildConfig(RequestFor(LockedDownPolicy()), pathEnvVar: "");

        Assert.Equal(P.Scratch, config.Process.Cwd);
        Assert.Contains(P.Scratch, config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HasDirectUserChangePermissions_IgnoresGroupOnlyAllow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rules = new[]
        {
            new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                AccessControlType.Allow),
        };

        Assert.False(MxcConfigBuilder.HasDirectUserChangePermissions(rules, user, _ => true));

        var readOnlyUserRule = new FileSystemAccessRule(
            user,
            FileSystemRights.Read,
            AccessControlType.Allow);
        Assert.False(MxcConfigBuilder.HasDirectUserChangePermissions([readOnlyUserRule], user, _ => false));

        var directUserRule = new FileSystemAccessRule(
            user,
            FileSystemRights.ChangePermissions,
            AccessControlType.Allow);
        Assert.True(MxcConfigBuilder.HasDirectUserChangePermissions([directUserRule], user, _ => false));

        var groupDenyRule = new FileSystemAccessRule(
            administrators,
            FileSystemRights.ChangePermissions,
            AccessControlType.Deny);
        Assert.False(
            MxcConfigBuilder.HasDirectUserChangePermissions(
                [directUserRule, groupDenyRule],
                user,
                sid => sid.Equals(administrators)));
    }

    [Fact]
    public void Build_CmdShell_RewritesBootstrapPercentEnvRefsToDelayedExpansion()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-path-env-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var argsDoc = JsonDocument.Parse("""
            {
                "command": "echo %TEMP% %TMP% %TMPDIR% %PATH%",
                "shell": "cmd",
                "args": ["%TEMP%\\out.txt"]
            }
            """);
            var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: tempDir);

            Assert.Contains(" /V:ON /D /S /C \"", config.Process.CommandLine, StringComparison.Ordinal);
            Assert.Contains("echo !TEMP! !TMP! !TMPDIR! !PATH!", config.Process.CommandLine, StringComparison.Ordinal);
            Assert.Contains("!TEMP!\\out.txt", config.Process.CommandLine, StringComparison.Ordinal);
            Assert.DoesNotContain("%TEMP%", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%TMP%", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%TMPDIR%", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%PATH%", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Build_DoesNotAddDriveRootCompatibilityGrant()
    {
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { "C:\\workspace\\out" },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");
        Assert.DoesNotContain("C:\\", config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_DoesNotTreatReadwriteChildAsCoveringParentCwd()
    {
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { "C:\\workspace\\out" },
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);

        var config = BuildConfig(RequestFor(policy) with { Cwd = "C:\\workspace" }, pathEnvVar: "");
        Assert.Contains("C:\\workspace", config.Filesystem!.ReadonlyPaths!);
        Assert.DoesNotContain("C:\\workspace", config.Filesystem!.ReadwritePaths!);
    }

    [Fact]
    public void ResolvePathDirsForShellPath_SkipsNonExistentDirs()
    {
        var fake = Path.Combine(Path.GetTempPath(), "definitely-not-real-xyzqq-" + Guid.NewGuid().ToString("N"));
        var dirs = MxcConfigBuilder.ResolvePathDirsForShellPath(pathEnvVar: fake);
        Assert.Empty(dirs);
    }

    [Fact]
    public void ResolvePathDirsForShellPath_SkipsDriveRoots()
    {
        var dirs = MxcConfigBuilder.ResolvePathDirsForShellPath(pathEnvVar: "C:\\");
        Assert.Empty(dirs);
    }

    [Fact]
    public void ResolvePathDirsForShellPath_KeepsProtectedDirsInPathOnly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            return;

        var dirs = MxcConfigBuilder.ResolvePathDirsForShellPath(pathEnvVar: programFiles);
        Assert.Contains(programFiles, dirs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_BootstrapsProtectedPathDirsWithoutGrantingThem()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            return;

        using var argsDoc = JsonDocument.Parse("""{"command":"tool --version","shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: programFiles);

        Assert.Contains($"set \"PATH={programFiles}\"", config.Process.CommandLine);
        Assert.DoesNotContain(programFiles, config.Filesystem!.ReadonlyPaths!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_DefensiveFilterStripsAllowEntriesOverlappingDenied()
    {
        // Caller (somehow) provides a custom RW folder pointing inside ~/.ssh.
        var policy = new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: new[] { Path.Combine(P.Ssh, "keys") },
                ReadonlyPaths: new[] { Path.Combine(P.Chrome, "Profile 1") },
                DeniedPaths: AlwaysDenied,
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000);
        var config = BuildConfig(RequestFor(policy), pathEnvVar: "");
        Assert.DoesNotContain(Path.Combine(P.Ssh, "keys"), config.Filesystem!.ReadwritePaths!);
        Assert.DoesNotContain(Path.Combine(P.Chrome, "Profile 1"), config.Filesystem!.ReadonlyPaths!);
    }

    [Fact]
    public void Build_TimeoutDefaultsTo30sWhenRequestZero()
    {
        var config = BuildConfig(RequestFor(BalancedPolicy()), pathEnvVar: "");
        Assert.Equal(30_000, config.Process.TimeoutMs);
    }

    [Fact]
    public void Build_TimeoutHonorsRequestValue()
    {
        var req = RequestFor(BalancedPolicy()) with { TimeoutMs = 12_345 };
        var config = BuildConfig(req, pathEnvVar: "");
        Assert.Equal(12_345, config.Process.TimeoutMs);
    }

    [Fact]
    public void Build_DefaultShell_UsesCmdAndPreservesUiDeny()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"echo hi"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.StartsWith(ExpectedSystemCmdExe(), config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" /D /S /C \"set \"TEMP=", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.Contains("echo hi\"", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.True(config.Ui!.Disable);
        Assert.Equal("container", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void DirectArgvCommandLine_RoundTripsExactlyThroughCommandLineToArgvW()
    {
        string[] argv =
        [
            @"C:\path with space\x.exe",
            "",
            "plain",
            "two words",
            "embedded\"quote",
            "a\\\"b",
            @"trailing\",
            @"slashes\\before quote""",
            "\t",
            "\n",
            "\v",
        ];

        var commandLine = DirectArgvCommandLine.Build(argv);

        Assert.Equal(argv, ParseCommandLine(commandLine));
    }

    [Fact]
    public void Build_DirectArgv_CanonicalCmdWrapper_UsesCmdAwareSerializer()
    {
        var command = @"echo READY && echo payload > ""C:\Users\owner\AppData\Roaming\OpenClaw\blocked.txt""";
        string[] argv = [ExpectedSystemCmdExe(), "/d", "/s", "/c", command];
        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { argv }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.StartsWith(
            ExpectedSystemCmdExe(),
            config.Process.CommandLine,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" /D /S /C \"set \"TEMP=", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.Contains(command, config.Process.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", config.Process.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DirectArgv_NonCanonicalCmdCommandWrapper_FailsClosed()
    {
        string[] argv = [ExpectedSystemCmdExe(), "/c", "echo hi"];
        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { argv }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var error = Assert.Throws<NotSupportedException>(
            () => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("canonical argv", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/cecho hi")]
    [InlineData("/d/s/cecho hi")]
    [InlineData("/kecho hi")]
    public void Build_DirectArgv_AttachedCmdCommandMode_FailsClosed(string commandMode)
    {
        string[] argv = [ExpectedSystemCmdExe(), commandMode];
        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { argv }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var error = Assert.Throws<NotSupportedException>(
            () => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("canonical argv", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DirectArgv_LateCmdCommandMode_FailsClosed()
    {
        string[] argv = [ExpectedSystemCmdExe(), "/d", "ignored", "/c", "echo hi"];
        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { argv }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var error = Assert.Throws<NotSupportedException>(
            () => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("canonical argv", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CmdBootstrapRefsWithDelayedExpansionSyntax_FailsClosed()
    {
        using var argsDoc = JsonDocument.Parse(
            """{"command":"echo %TEMP% && echo !PATH!","shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var error = Assert.Throws<NotSupportedException>(
            () => BuildConfig(request, pathEnvVar: @"C:\Windows\System32"));

        Assert.Contains("delayed-expansion syntax", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_CmdBootstrapRefsWithBangInScratchPath_FailsClosed()
    {
        using var argsDoc = JsonDocument.Parse(
            """{"command":"echo %TEMP%","shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var error = Assert.Throws<NotSupportedException>(
            () => BuildConfig(
                request,
                scratchDir: @"C:\Bang!PATH!\scratch",
                pathEnvVar: ""));

        Assert.Contains("bootstrap paths", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DirectArgv_UsesDirectCommandLineWithoutShellWrapper()
    {
        string[] argv = [@"C:\Program Files\Tool\tool.exe", "alpha", "two words"];
        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            command = "should-be-ignored",
            shell = "powershell",
            args = new[] { "should-be-ignored" },
            argv,
        }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.Equal(
            "\"C:\\Program Files\\Tool\\tool.exe\" alpha \"two words\"",
            config.Process.CommandLine);
        Assert.DoesNotContain(" /S /C ", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-EncodedCommand", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(config.Process.Env!);
        Assert.Contains(P.Scratch, config.Filesystem!.ReadwritePaths!);
        Assert.True(config.Ui!.Disable);
        Assert.Equal("container", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void Build_MalformedDirectArgv_FailsClosedInsteadOfUsingLegacyShellFields()
    {
        using var argsDoc = JsonDocument.Parse(
            """{"command":"should-not-run","shell":"cmd","argv":"not-an-array"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("Direct argv must be an array", ex.Message);
    }

    [Fact]
    public void Build_PowerShellShell_WhenPolicyAllowsWindows_EnablesDesktopIsolation()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output hi","shell":"powershell"}""");
        var policy = BalancedPolicy() with
        {
            Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
        };
        var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.False(config.Ui!.Disable);
        Assert.Equal("desktop", config.ProcessContainer!.Ui!.Isolation);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public void Build_PowerShellFamilyShell_WhenUiDenied_FailsClosed(string shell)
    {
        using var argsDoc = JsonDocument.Parse($$"""{"command":"Write-Output hi","shell":"{{shell}}"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("PowerShell-family shells require UI access", ex.Message);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    [InlineData("pwsh.exe")]
    public void Build_PowerShellFamilyDirectArgv_WhenUiDenied_FailsClosed(string executable)
    {
        using var argsDoc = JsonDocument.Parse(
            JsonSerializer.Serialize(new { argv = new[] { executable, "-NoProfile", "-Command", "Write-Output hi" } }));
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("PowerShell-family shells require UI access", ex.Message);
    }

    [Fact]
    public void Build_PowerShellDirectArgv_WhenPolicyAllowsWindows_EnablesDesktopIsolation()
    {
        using var argsDoc = JsonDocument.Parse(
            """{"argv":["powershell.exe","-NoProfile","-Command","Write-Output hi"]}""");
        var policy = BalancedPolicy() with
        {
            Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.None, AllowInputInjection: false),
        };
        var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.False(config.Ui!.Disable);
        Assert.Equal("desktop", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void Build_UnsupportedShell_FailsClosedBeforeCommandLineFallback()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"echo hi","shell":"bash"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("Unsupported shell 'bash'", ex.Message);
    }

    [Fact]
    public void Build_CmdShell_UsesResolvedCmdExe()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"echo hi","shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.StartsWith(ExpectedSystemCmdExe(), config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" /S /C \"set \"TEMP=", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.Contains("echo hi\"", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.True(config.Ui!.Disable);
        Assert.Equal("container", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void Build_CmdShell_CommandWithLineBreak_FailsClosed()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"echo ok\r\nwhoami","shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("cannot contain CR or LF", ex.Message);
    }

    [Fact]
    public void Build_CmdShell_ArgvWithLineBreak_FailsClosed()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"echo","args":["ok\r\nwhoami"],"shell":"cmd"}""");
        var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

        var ex = Assert.Throws<NotSupportedException>(() => BuildConfig(request, pathEnvVar: ""));

        Assert.Contains("cannot contain CR or LF", ex.Message);
    }

    [Fact]
    public void Build_CmdShell_IgnoresHostComSpec()
    {
        var previous = Environment.GetEnvironmentVariable("ComSpec");
        try
        {
            Environment.SetEnvironmentVariable("ComSpec", "C:\\malicious\\cmd.exe");
            using var argsDoc = JsonDocument.Parse("""{"command":"echo hi","shell":"cmd"}""");
            var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: "");

            Assert.StartsWith(ExpectedSystemCmdExe(), config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\malicious\\cmd.exe", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ComSpec", previous);
        }
    }

    [Fact]
    public void Build_PwshShell_WhenPolicyAllowsWindows_UsesPwshAndEnablesDesktopIsolation()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output hi","shell":"pwsh"}""");
        var policy = BalancedPolicy() with
        {
            Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
        };
        var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        Assert.StartsWith("pwsh.exe", config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" -NoProfile -NonInteractive -EncodedCommand ", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.False(config.Ui!.Disable);
        Assert.Equal("desktop", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void Build_PwshShell_ResolvesPwshFromPathBeforeClearingProcessEnvironment()
    {
        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-pwsh-resolve-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var binDir = Directory.CreateDirectory(Path.Combine(tempRoot, "bin")).FullName;
            var pwshPath = Path.Combine(binDir, "pwsh.exe");
            File.WriteAllBytes(pwshPath, Array.Empty<byte>());
            using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output hi","shell":"pwsh"}""");
            var policy = BalancedPolicy() with
            {
                Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
            };
            var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: binDir);

            var expectedPrefix = pwshPath.IndexOfAny(new[] { ' ', '\t', '"' }) < 0
                ? pwshPath
                : "\"" + pwshPath.Replace("\"", "\\\"") + "\"";
            Assert.StartsWith(expectedPrefix, config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(config.Process.Env!);
            Assert.Contains(" -NoProfile -NonInteractive -EncodedCommand ", config.Process.CommandLine, StringComparison.Ordinal);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void Build_PowerShellShell_UsesResolvedWindowsPowerShellExe()
    {
        using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output hi","shell":"powershell"}""");
        var policy = BalancedPolicy() with
        {
            Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
        };
        var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

        var config = BuildConfig(request, pathEnvVar: "");

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        var expected = string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        Assert.StartsWith(expected, config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" -NoProfile -NonInteractive -EncodedCommand ", config.Process.CommandLine, StringComparison.Ordinal);
        Assert.False(config.Ui!.Disable);
        Assert.Equal("desktop", config.ProcessContainer!.Ui!.Isolation);
    }

    [Fact]
    public void Build_PowerShellShell_QuotesPathBootstrapValue()
    {
        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-pwsh-path-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var dir1 = Directory.CreateDirectory(Path.Combine(tempRoot, "bin1")).FullName;
            var dir2 = Directory.CreateDirectory(Path.Combine(tempRoot, "bin2")).FullName;
            var pathEnv = string.Join(Path.PathSeparator, dir1, dir2);
            using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output $env:PATH","shell":"powershell"}""");
            var policy = BalancedPolicy() with
            {
                Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
            };
            var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: pathEnv);
            var script = DecodePowershellEncodedCommand(config.Process.CommandLine);

            Assert.Contains("$env:PATH = '" + pathEnv.Replace("'", "''") + "';", script);
            Assert.DoesNotContain("$env:PATH = " + pathEnv + ";", script);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void Build_CmdShell_BoundsPathBootstrapBeforeCommandLine()
    {
        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-cmd-long-path-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var dirs = Enumerable.Range(0, 180)
                .Select(i => Directory.CreateDirectory(Path.Combine(tempRoot, $"bin{i:D3}")).FullName)
                .ToArray();
            var pathEnv = string.Join(Path.PathSeparator, dirs);
            using var argsDoc = JsonDocument.Parse("""{"command":"echo %PATH%","shell":"cmd"}""");
            var request = RequestFor(BalancedPolicy()) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: pathEnv);

            Assert.Contains("set \"PATH=", config.Process.CommandLine, StringComparison.Ordinal);
            Assert.Contains(dirs[0], config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(dirs[^1], config.Process.CommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.True(config.Process.CommandLine.Length < 12_000, config.Process.CommandLine);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void Build_PowerShellShell_BoundsPathBootstrapBeforeEncoding()
    {
        var tempRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mxc-pwsh-long-path-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var dirs = Enumerable.Range(0, 180)
                .Select(i => Directory.CreateDirectory(Path.Combine(tempRoot, $"bin{i:D3}")).FullName)
                .ToArray();
            var pathEnv = string.Join(Path.PathSeparator, dirs);
            using var argsDoc = JsonDocument.Parse("""{"command":"Write-Output $env:PATH","shell":"powershell"}""");
            var policy = BalancedPolicy() with
            {
                Ui = new UiPolicy(AllowWindows: true, Clipboard: ClipboardPolicy.Read, AllowInputInjection: false),
            };
            var request = RequestFor(policy) with { Args = argsDoc.RootElement.Clone() };

            var config = BuildConfig(request, pathEnvVar: pathEnv);
            var script = DecodePowershellEncodedCommand(config.Process.CommandLine);

            Assert.Contains("$env:PATH = '", script, StringComparison.Ordinal);
            Assert.Contains(dirs[0], script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(dirs[^1], script, StringComparison.OrdinalIgnoreCase);
            Assert.True(script.Length < 8_000, script);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // ---- helpers for tolerant JSON comparison ----

    private static string DecodePowershellEncodedCommand(string commandLine)
    {
        const string marker = " -EncodedCommand ";
        var markerIndex = commandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(markerIndex >= 0, commandLine);
        var encoded = commandLine[(markerIndex + marker.Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            return Enumerable.Range(0, argc)
                .Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)
                .ToArray();
        }
        finally
        {
            Assert.Equal(IntPtr.Zero, LocalFree(argv));
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static void AssertJsonEqual(JsonObjectNode expected, JsonObjectNode actual, string path)
    {
        foreach (var key in expected.Keys)
        {
            Assert.True(actual.Contains(key), $"Missing key {path}.{key} in actual; actual keys=[{string.Join(",", actual.Keys)}]");
            var ev = expected[key];
            var av = actual[key];
            AssertNodeEqual(ev, av, $"{path}.{key}");
        }

        // Symmetric check: any extra keys on `actual` that aren't in `expected`
        // are reported, except for the small allow-list of fields our C# emits
        // that the SDK doesn't (and that we explicitly want to surface) and the
        // per-invocation random fields we already strip from the golden.
        foreach (var key in actual.Keys)
        {
            if (expected.Contains(key)) continue;
            if (IsToleratedExtraKey($"{path}.{key}")) continue;
            Assert.Fail($"Unexpected extra key in actual at {path}.{key}; expected keys=[{string.Join(",", expected.Keys)}]");
        }
    }

    private static bool IsDriveRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsToleratedExtraKey(string fullPath)
    {
        // commandLine/cwd/env/timeoutMs live under "process" — the SDK leaves
        // process empty when called with createConfigFromPolicy and we add
        // these ourselves. processContainer.name is a per-invocation random hex
        // that we stripped from the goldens.
        return fullPath is
            "$.process.commandLine" or
            "$.process.cwd" or
            "$.process.env" or
            "$.process.timeoutMs" or
            "$.processContainer.name";
    }

    private static void AssertNodeEqual(object? expected, object? actual, string path)
    {
        switch (expected)
        {
            case JsonObjectNode eo when actual is JsonObjectNode ao:
                AssertJsonEqual(eo, ao, path); break;
            case List<object?> el when actual is List<object?> al:
                Assert.True(el.Count == al.Count, $"{path}: expected length {el.Count}, got {al.Count}");
                for (int i = 0; i < el.Count; i++) AssertNodeEqual(el[i], al[i], $"{path}[{i}]");
                break;
            default:
                Assert.True(Equals(expected, actual) || string.Equals(expected?.ToString(), actual?.ToString(), StringComparison.Ordinal),
                    $"{path}: expected {expected} ({expected?.GetType().Name ?? "null"}), got {actual} ({actual?.GetType().Name ?? "null"})");
                break;
        }
    }

    /// <summary>Trivial ordered-key JSON object/array tree we can compare and mutate.</summary>
    private sealed class JsonObjectNode
    {
        private readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();
        public IEnumerable<string> Keys => _order;
        public bool Contains(string key) => _map.ContainsKey(key);
        public object? this[string key] { get => _map[key]; set { if (!_map.ContainsKey(key)) _order.Add(key); _map[key] = value; } }
        public bool TryGetString(string key, out string value)
        {
            if (_map.TryGetValue(key, out var v) && v is string s) { value = s; return true; }
            value = string.Empty; return false;
        }
        public void Remove(string key) { _map.Remove(key); _order.Remove(key); }

        public static JsonObjectNode FromJsonElement(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected object root");
            var node = new JsonObjectNode();
            foreach (var prop in root.EnumerateObject()) node[prop.Name] = Convert(prop.Value);
            return node;
        }

        private static object? Convert(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Object => FromJsonElement(el),
            JsonValueKind.Array => el.EnumerateArray().Select(Convert).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var i) ? (object)i : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
