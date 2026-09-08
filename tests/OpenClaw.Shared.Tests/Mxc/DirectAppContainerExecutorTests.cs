using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// Unit tests for <see cref="DirectAppContainerExecutor"/> that don't actually
/// spawn wxc-exec. End-to-end smoke is covered by
/// <see cref="MxcCommandRunnerIntegrationTests"/>.
/// </summary>
public class DirectAppContainerExecutorTests
{
    private static SandboxExecutionRequest NewRequest() => new(
        CapabilityCommand: "system.run",
        Args: JsonDocument.Parse("{\"command\":\"echo hi\",\"shell\":\"cmd\"}").RootElement,
        Policy: new SandboxPolicy(
            Version: MxcPolicyBuilder.SupportedPolicyVersion,
            Filesystem: new FilesystemPolicy(
                ReadwritePaths: Array.Empty<string>(),
                ReadonlyPaths: Array.Empty<string>(),
                DeniedPaths: Array.Empty<string>(),
                ClearPolicyOnExit: true),
            Network: new NetworkPolicy(false, false),
            Ui: new UiPolicy(false, ClipboardPolicy.None, false),
            TimeoutMs: 30_000),
        TimeoutMs: 30_000);

    [Fact]
    public async Task ExecuteAsync_AppContainerUnavailable_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: false,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: false,
            wxcExecPath: null,
            unsupportedReasons: new[] { "test reason" });
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("test reason", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WxcExecNotResolvable_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: false,
            wxcExecPath: null,
            unsupportedReasons: Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("wxc-exec.exe not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WxcExecPathMissingOnDisk_Throws()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        // MxcExecutor's ctor throws FileNotFoundException → wrapped in SandboxUnavailableException.
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
    }

    [Fact]
    public async Task ExecuteAsync_DaclTier_IsUnavailableBeforeLaunch()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>(),
            isolationTier: "appcontainer-dacl",
            needsDaclAugmentation: true);
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(
            () => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("requires MXC BaseContainer", ex.Message);
    }

    [Theory]
    [InlineData("0.7.0-alpha", false, 0, false, true)]
    [InlineData("0.8.0-alpha", false, 0, false, false)]
    [InlineData("0.7.0-alpha", true, 0, false, false)]
    [InlineData("0.7.0-alpha", false, 1, false, false)]
    [InlineData("0.7.0-alpha", false, 2, false, false)]
    [InlineData("0.7.0-alpha", false, 0, true, false)]
    public void IsSystemRunConfigBaseContainerCompatible_RequiresExactEmittedContract(
        string version,
        bool leastPrivilege,
        int deniedPathMode,
        bool hasContainment,
        bool expected)
    {
        var config = new MxcConfig
        {
            Version = version,
            ContainerId = "test",
            Containment = hasContainment ? "process" : null,
            Process = new MxcProcess { CommandLine = "cmd.exe /c echo hi" },
            ProcessContainer = new MxcProcessContainer
            {
                LeastPrivilege = leastPrivilege,
            },
            Filesystem = new MxcFilesystem
            {
                ReadonlyPaths = [@"C:\Users\test\Downloads"],
                DeniedPaths = deniedPathMode switch
                {
                    1 => Array.Empty<string>(),
                    2 => [@"C:\secret"],
                    _ => null,
                },
            },
        };

        Assert.Equal(
            expected,
            MxcIsolationTierPolicy.IsSystemRunConfigBaseContainerCompatible(config));
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesAvailabilityPerCall_PicksUpRecovery()
    {
        // Simulates a transient startup probe error that later recovers. The executor
        // must read the provider on each call, not freeze the first snapshot — else a
        // momentary startup glitch would pin it to "unavailable" for its lifetime.
        var calls = 0;
        var unavailable = new MxcAvailability(
            isAppContainerAvailable: false,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: null,
            unsupportedReasons: new[] { "transient probe error" },
            probeErrored: true);
        var recoveredButPathMissing = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\does\\not\\exist\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>(),
            isolationTier: "base-container");

        var executor = new DirectAppContainerExecutor(
            () => { calls++; return calls == 1 ? unavailable : recoveredButPathMissing; },
            NullLogger.Instance);

        // First call: unavailable → throws with the transient reason.
        var ex1 = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("transient probe error", ex1.Message);

        // Second call: provider now reports available (path just doesn't exist on
        // disk). The DIFFERENT failure proves the executor re-resolved availability
        // instead of reusing the first (unavailable) snapshot.
        var ex2 = await Assert.ThrowsAsync<SandboxUnavailableException>(() => executor.ExecuteAsync(NewRequest()));
        Assert.Contains("wxc-exec.exe not found at", ex2.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Name_IsStableForTelemetry()
    {
        var availability = new MxcAvailability(false, false, false, null, Array.Empty<string>());
        var executor = new DirectAppContainerExecutor(() => availability, NullLogger.Instance);
        Assert.Equal("mxc-direct-appc", executor.Name);
        Assert.True(executor.IsContained);
    }

    [Fact]
    public void BuildRedactedConfigSummary_DoesNotExposeWxcExecOrRequestPaths()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\secret\\mxc\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>());
        var config = new MxcConfig
        {
            ContainerId = "test",
            Process = new MxcProcess
            {
                CommandLine = "cmd /c echo hi",
                Cwd = "C:\\secret\\work",
                Env = new[] { "SECRET_PATH=C:\\secret\\value" },
            },
            Filesystem = new MxcFilesystem
            {
                ReadwritePaths = new[] { "C:\\secret\\repo" },
                ReadonlyPaths = new[] { "C:\\secret\\docs" },
                DeniedPaths = new[] { "C:\\secret\\.ssh" },
            },
        };

        var summary = DirectAppContainerExecutor.BuildRedactedConfigSummary(
            availability,
            config,
            "{\"fake\":true}",
            NewRequest());

        Assert.Contains("wxcExec=<set>", summary);
        Assert.Contains("cwd=<set>", summary);
        Assert.Contains("envKeys=[SECRET_PATH]", summary);
        Assert.DoesNotContain("C:\\secret", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd /c echo hi", summary, StringComparison.OrdinalIgnoreCase);
    }
}
