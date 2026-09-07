using Xunit;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcAvailabilityTests
{
    [Fact]
    public void Probe_NonWindows_ReturnsUnsupported()
    {
        // We can't easily fake the OS on Windows test runs, so just exercise
        // the public probe and assert structural invariants. The concrete
        // unsupported-platform path is exercised on Linux/macOS CI (when added).
        var availability = MxcAvailability.Probe();

        // Either fully supported, or has at least one reason explaining why not.
        if (!availability.HasAnyBackend)
        {
            Assert.NotEmpty(availability.UnsupportedReasons);
        }
    }

    [Fact]
    public void Probe_Result_IsConsistent()
    {
        var availability = MxcAvailability.Probe();

        // isolation_session implies appcontainer + wxc-exec.
        if (availability.IsIsolationSessionAvailable)
        {
            Assert.True(availability.IsAppContainerAvailable);
            Assert.True(availability.IsWxcExecResolvable);
        }

        // wxc-exec resolvable implies a path is captured.
        if (availability.IsWxcExecResolvable)
        {
            Assert.False(string.IsNullOrWhiteSpace(availability.WxcExecPath));
        }

        // HasAnyBackend requires: a backend supported AND wxc-exec resolvable.
        Assert.Equal(
            (availability.IsAppContainerAvailable || availability.IsIsolationSessionAvailable)
                && availability.IsWxcExecResolvable,
            availability.HasAnyBackend);
    }

    [Fact]
    public void Constructor_StoresAllFields()
    {
        var reasons = new List<string> { "test reason" };
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\fake\\wxc-exec.exe",
            unsupportedReasons: reasons);

        Assert.True(availability.IsAppContainerAvailable);
        Assert.False(availability.IsIsolationSessionAvailable);
        Assert.True(availability.IsWxcExecResolvable);
        Assert.Equal("C:\\fake\\wxc-exec.exe", availability.WxcExecPath);
        Assert.Single(availability.UnsupportedReasons);
        Assert.True(availability.HasAnyBackend);
        Assert.Empty(availability.Warnings);
        Assert.False(availability.CanRunSystemRunSandbox);
        Assert.NotEmpty(availability.SystemRunSandboxUnsupportedReasons);
    }

    [Fact]
    public void ParseProbeOutput_ValidTier_ReportsSupported()
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 0,
            stdout: "{\"tier\":\"base-container\",\"needsDaclAugmentation\":false,\"warnings\":[]}",
            stderr: "");

        Assert.Equal(MxcProbeOutcome.Supported, result.Outcome);
        Assert.True(result.Supported);
        Assert.Equal("base-container", result.Tier);
        Assert.False(result.NeedsDaclAugmentation);
        Assert.Empty(result.Warnings);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ParseProbeOutput_CapturesWarningsAndDaclFlag()
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 0,
            stdout: "{\"tier\":\"appcontainer-dacl\",\"needsDaclAugmentation\":true,\"warnings\":[\"fell back to dacl\"]}",
            stderr: "");

        Assert.Equal(MxcProbeOutcome.Supported, result.Outcome);
        Assert.True(result.Supported);
        Assert.Equal("appcontainer-dacl", result.Tier);
        Assert.True(result.NeedsDaclAugmentation);
        Assert.Equal(new[] { "fell back to dacl" }, result.Warnings);
    }

    [Fact]
    public void ParseProbeOutput_CompletedNonZeroExit_ReportsProbeError()
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 1,
            stdout: "",
            stderr: "unknown option --probe");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.Null(result.Tier);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Could not determine", result.FailureReason!);
    }

    [Fact]
    public void ParseProbeOutput_CompletedJsonError_ReportsUnsupportedHost()
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 1,
            stdout: "{\"error\":\"unsupported Windows build\",\"warnings\":[\"need newer host\"]}",
            stderr: "");

        Assert.Equal(MxcProbeOutcome.UnsupportedHost, result.Outcome);
        Assert.False(result.Supported);
        Assert.Null(result.Tier);
        Assert.Equal(["need newer host"], result.Warnings);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("unsupported Windows build", result.FailureReason!);
    }

    [Fact]
    public void ParseProbeOutput_TimedOutStatus_ReportsProbeError()
    {
        // Timeout is our own infrastructure failure — transient, regardless of exit code.
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.TimedOut, exitCode: 0, stdout: "", stderr: "wxc-exec --probe timed out.");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Could not determine", result.FailureReason!);
    }

    [Fact]
    public void ParseProbeOutput_LaunchFailedStatus_ReportsProbeError()
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.LaunchFailed, exitCode: 0, stdout: "", stderr: "Failed to launch wxc-exec --probe: access denied");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Could not determine", result.FailureReason!);
    }

    [Fact]
    public void ParseProbeOutput_NonCompletedStatus_IgnoresExitCode()
    {
        // Even a "successful-looking" exit code must not flip a timeout/launch failure
        // into a definitive verdict — the status wins.
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.TimedOut,
            exitCode: 0,
            stdout: "{\"tier\":\"base-container\"}",
            stderr: "");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.Null(result.Tier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"needsDaclAugmentation\":false}")]
    [InlineData("{\"tier\":\"\"}")]
    [InlineData("not json at all")]
    public void ParseProbeOutput_CompletedExitZeroButNoUsableOutput_ReportsProbeError(string stdout)
    {
        // Exit 0 with missing/garbled output is a binary anomaly, not a clear
        // "unsupported" verdict — treat as retryable probe error.
        var result = MxcAvailability.ParseProbeOutput(WxcProbeStatus.Completed, exitCode: 0, stdout: stdout, stderr: "");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.Null(result.Tier);
        Assert.NotNull(result.FailureReason);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public void ParseProbeOutput_NonObjectJson_ReportsProbeError(string stdout)
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 0,
            stdout,
            stderr: "");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.Contains("non-object JSON", result.FailureReason);
    }

    [Theory]
    [InlineData("""{"tier":"base-container"}""")]
    [InlineData("""{"tier":"base-container","needsDaclAugmentation":null}""")]
    [InlineData("""{"tier":"base-container","needsDaclAugmentation":"false"}""")]
    public void ParseProbeOutput_InvalidDaclAugmentationSignal_ReportsProbeError(string stdout)
    {
        var result = MxcAvailability.ParseProbeOutput(
            WxcProbeStatus.Completed,
            exitCode: 0,
            stdout,
            stderr: "");

        Assert.Equal(MxcProbeOutcome.ProbeError, result.Outcome);
        Assert.False(result.Supported);
        Assert.Contains("boolean needsDaclAugmentation", result.FailureReason);
    }

    [Theory]
    [InlineData("base-container", false, true)]
    [InlineData("base-container", true, false)]
    [InlineData("appcontainer-bfs", false, false)]
    [InlineData("appcontainer-dacl", true, false)]
    [InlineData("some-future-tier", false, false)]
    [InlineData("BASE-CONTAINER", false, false)]
    [InlineData("base-container ", false, false)]
    public void CanRunSystemRunSandbox_RequiresUnaugmentedBaseContainer(
        string tier,
        bool needsDacl,
        bool expected)
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: true,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\fake\\wxc-exec.exe",
            unsupportedReasons: Array.Empty<string>(),
            isolationTier: tier,
            needsDaclAugmentation: needsDacl);

        Assert.True(availability.HasAnyBackend);
        Assert.Equal(expected, availability.CanRunSystemRunSandbox);
        Assert.Equal(
            expected,
            availability.SystemRunSandboxUnsupportedReasons.Count == 0);
    }

    [Fact]
    public void CanRunSystemRunSandbox_FalseWhenNoBackend()
    {
        var availability = new MxcAvailability(
            isAppContainerAvailable: false,
            isIsolationSessionAvailable: false,
            isWxcExecResolvable: true,
            wxcExecPath: "C:\\fake\\wxc-exec.exe",
            unsupportedReasons: new[] { "nope" },
            isolationTier: "appcontainer-dacl",
            needsDaclAugmentation: true);

        Assert.False(availability.HasAnyBackend);
        Assert.False(availability.CanRunSystemRunSandbox);
    }

    [Fact]
    public void Probe_WhenProbeReportsTier_ReportsAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(
                    WxcProbeStatus.Completed,
                    0,
                    "{\"tier\":\"base-container\",\"needsDaclAugmentation\":false,\"warnings\":[]}",
                    string.Empty),
                windowsServerProvider: () => false);

            Assert.True(availability.IsAppContainerAvailable);
            Assert.True(availability.IsWxcExecResolvable);
            Assert.Equal(fakeExe, availability.WxcExecPath);
            Assert.Empty(availability.UnsupportedReasons);
            Assert.False(availability.ProbeErrored);
            Assert.Equal("base-container", availability.IsolationTier);
            Assert.True(availability.CanRunSystemRunSandbox);
            Assert.Empty(availability.SystemRunSandboxUnsupportedReasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Probe_WindowsServerSku_SkipsResolverAndNativeProbe()
    {
        var resolverCalled = false;
        var probeCalled = false;

        var availability = MxcAvailability.Probe(
            NullLogger.Instance,
            _ =>
            {
                probeCalled = true;
                throw new InvalidOperationException("native probe must not run on Server");
            },
            windowsServerProvider: () => true,
            windowsProvider: () => true,
            wxcResolver: () =>
            {
                resolverCalled = true;
                return (true, @"C:\mxc\wxc-exec.exe");
            });

        Assert.False(resolverCalled);
        Assert.False(probeCalled);
        Assert.False(availability.CanRunSystemRunSandbox);
        Assert.False(availability.IsWxcExecResolvable);
        Assert.False(availability.ProbeErrored);
        Assert.True(availability.ProbeSuppressedBySkuGate);
        Assert.Contains("Windows Server", Assert.Single(availability.UnsupportedReasons));
    }

    [Fact]
    public void Probe_UnknownWindowsSku_SkipsResolverAndNativeProbe()
    {
        var resolverCalled = false;
        var probeCalled = false;

        var availability = MxcAvailability.Probe(
            NullLogger.Instance,
            _ =>
            {
                probeCalled = true;
                throw new InvalidOperationException("native probe must not run for an unknown SKU");
            },
            windowsServerProvider: () => null,
            windowsProvider: () => true,
            wxcResolver: () =>
            {
                resolverCalled = true;
                return (true, @"C:\mxc\wxc-exec.exe");
            });

        Assert.False(resolverCalled);
        Assert.False(probeCalled);
        Assert.False(availability.CanRunSystemRunSandbox);
        Assert.True(availability.ProbeErrored);
        Assert.True(availability.ProbeSuppressedBySkuGate);
        Assert.Contains("supported Windows client SKU", Assert.Single(availability.UnsupportedReasons));
    }

    [Fact]
    public void DetectWindowsServerSku_ReturnsDefinitiveResultOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.NotNull(MxcAvailability.DetectWindowsServerSku());
    }

    [Fact]
    public void Probe_NonWindows_SkipsSkuResolutionAndNativeProbe()
    {
        var skuCalled = false;
        var resolverCalled = false;
        var probeCalled = false;

        var availability = MxcAvailability.Probe(
            NullLogger.Instance,
            _ =>
            {
                probeCalled = true;
                throw new InvalidOperationException("native probe must not run");
            },
            windowsServerProvider: () =>
            {
                skuCalled = true;
                return false;
            },
            windowsProvider: () => false,
            wxcResolver: () =>
            {
                resolverCalled = true;
                return (true, @"C:\mxc\wxc-exec.exe");
            });

        Assert.False(skuCalled);
        Assert.False(resolverCalled);
        Assert.False(probeCalled);
        Assert.False(availability.ProbeSuppressedBySkuGate);
        Assert.Contains("requires Windows", Assert.Single(availability.UnsupportedReasons));
    }

    [Fact]
    public void Probe_WindowsClient_ResolvesAndRunsNativeProbe()
    {
        var resolverCalled = false;
        var probeCalled = false;

        var availability = MxcAvailability.Probe(
            NullLogger.Instance,
            path =>
            {
                probeCalled = true;
                Assert.Equal(@"C:\mxc\wxc-exec.exe", path);
                return new WxcProbeInvocation(
                    WxcProbeStatus.Completed,
                    0,
                    """{"tier":"base-container","needsDaclAugmentation":false,"warnings":[]}""",
                    string.Empty);
            },
            windowsServerProvider: () => false,
            windowsProvider: () => true,
            wxcResolver: () =>
            {
                resolverCalled = true;
                return (true, @"C:\mxc\wxc-exec.exe");
            });

        Assert.True(resolverCalled);
        Assert.True(probeCalled);
        Assert.True(availability.CanRunSystemRunSandbox);
        Assert.False(availability.ProbeSuppressedBySkuGate);
    }

    [Fact]
    public void Probe_WhenProbeReportsNoTier_ReportsUnavailableWithReason()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(WxcProbeStatus.Completed, 1, string.Empty, "unsupported os build"),
                windowsServerProvider: () => false);

            Assert.True(availability.IsWxcExecResolvable);
            Assert.False(availability.IsAppContainerAvailable);
            Assert.False(availability.HasAnyBackend);
            Assert.NotEmpty(availability.UnsupportedReasons);
            Assert.True(availability.ProbeErrored);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Probe_WhenProbeTimesOut_ReportsProbeErrored()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            // TimedOut status → transient probe error.
            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(WxcProbeStatus.TimedOut, 0, string.Empty, "wxc-exec --probe timed out."),
                windowsServerProvider: () => false);

            Assert.True(availability.IsWxcExecResolvable);
            Assert.False(availability.IsAppContainerAvailable);
            Assert.False(availability.HasAnyBackend);
            Assert.True(availability.ProbeErrored);
            Assert.NotEmpty(availability.UnsupportedReasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Probe_WhenProbeReportsDaclTier_RejectsSystemRunSandbox()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fakeExe = Path.Combine(Path.GetTempPath(), $"wxc-fake-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fakeExe, string.Empty);
        try
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, fakeExe);

            var availability = MxcAvailability.Probe(
                NullLogger.Instance,
                _ => new WxcProbeInvocation(
                    WxcProbeStatus.Completed,
                    0,
                    "{\"tier\":\"appcontainer-dacl\",\"needsDaclAugmentation\":true,\"warnings\":[\"fallback\"]}",
                    string.Empty),
                windowsServerProvider: () => false);

            // General MXC remains available, but OpenClaw does not admit the
            // DACL-backed process tier for system.run.
            Assert.True(availability.IsAppContainerAvailable);
            Assert.True(availability.HasAnyBackend);
            Assert.False(availability.CanRunSystemRunSandbox);
            Assert.Equal("appcontainer-dacl", availability.IsolationTier);
            Assert.True(availability.NeedsDaclAugmentation);
            Assert.False(availability.ProbeErrored);
            Assert.Equal(["fallback"], availability.Warnings);
            Assert.Contains(
                "requires MXC BaseContainer",
                Assert.Single(availability.SystemRunSandboxUnsupportedReasons));
        }
        finally
        {
            Environment.SetEnvironmentVariable(MxcAvailability.WxcExecOverrideEnvVar, null);
            try { File.Delete(fakeExe); } catch { /* best-effort */ }
        }
    }

}
