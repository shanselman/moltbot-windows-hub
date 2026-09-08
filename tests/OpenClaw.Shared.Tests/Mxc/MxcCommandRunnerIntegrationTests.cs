using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;

namespace OpenClaw.Shared.Tests.Mxc;

/// <summary>
/// End-to-end smoke test for the MxcCommandRunner pipeline. Actually spawns
/// wxc-exec.exe to run a real shell payload inside an AppContainer. Gated by
/// OPENCLAW_RUN_INTEGRATION=1 so it doesn't run by default on CI; matches the
/// existing LocalCommandRunnerIntegrationTests pattern.
///
/// Additionally skips (passes without running) when MXC is not available on the
/// host (e.g. older Windows UBR or wxc-exec.exe missing). Hosts with MXC enabled
/// will exercise the real sandbox; hosts without it will see a clear skip log.
/// </summary>
public class MxcCommandRunnerIntegrationTests
{
    private static MxcCommandRunner? TryBuildRunner(bool sandboxEnabled = true, Action<SettingsData>? configure = null)
    {
        if (IsGitHubActions())
        {
            Console.WriteLine(
                "[mxc-integration] SKIPPING: GitHub Actions does not provide the required local sandbox environment.");
            return null;
        }

        var availability = MxcAvailability.Probe(NullLogger.Instance);
        if (!availability.CanRunSystemRunSandbox)
        {
            Console.WriteLine(
                $"[mxc-integration] SKIPPING: MXC BaseContainer not available. Reasons: " +
                string.Join("; ", availability.SystemRunSandboxUnsupportedReasons));
            return null;
        }

        if (!HasSupportedSandboxPath(AppContext.BaseDirectory))
        {
            Console.WriteLine(
                "[mxc-integration] SKIPPING: test output path is not in a supported local sandbox location.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(availability.WxcExecPath)
            && !HasSupportedSandboxPath(availability.WxcExecPath))
        {
            Console.WriteLine(
                "[mxc-integration] SKIPPING: sandbox helper path is not in a supported local sandbox location.");
            return null;
        }

        var executor = new DirectAppContainerExecutor(() => availability, new ConsoleLogger());

        var settings = new SettingsData
        {
            SystemRunSandboxEnabled = sandboxEnabled,
            SystemRunAllowOutbound = false,
        };
        configure?.Invoke(settings);

        var hostFallback = new LocalCommandRunner(NullLogger.Instance);

        return new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => Path.Combine(Path.GetTempPath(), "openclaw-mxc-smoke-test-settings"),
            () => true, // integration test runs only when MXC is available
            invalidateAvailability: null,
            new ConsoleLogger());
    }

    [IntegrationFact]
    public async Task SystemRun_EchoCmd_ExecutesInsideAppContainer()
    {
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hello-from-mxc",
            Shell = "cmd",
            TimeoutMs = 30_000,
        });

        // Surface full result on assertion failure for diagnosis.
        Assert.True(
            result.ExitCode == 0 && result.Stdout.Contains("hello-from-mxc"),
            $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}");
    }

    [IntegrationFact]
    public async Task SystemRun_DefaultShell_ExecutesInsideAppContainer()
    {
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo hello-default-mxc",
            TimeoutMs = 30_000,
        });

        Assert.True(
            result.ExitCode == 0 && result.Stdout.Contains("hello-default-mxc"),
            $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}");
    }

    [IntegrationFact]
    public async Task SystemRun_DirectArgvWithWindowsUiAccess_ExecutesInsideAppContainer()
    {
        var runner = TryBuildRunner(configure: settings => settings.SystemRunAllowWindowsUi = true);
        if (runner is null) return;

        var result = await runner.RunAsync(new CommandRequest
        {
            Argv = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe")],
            TimeoutMs = 30_000,
        });

        Assert.True(
            result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout),
            $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}");
    }

    [IntegrationFact]
    public async Task SystemRun_PipelineSmokeTest_WithDenyPaths_ReturnsResult()
    {
        // NOTE: This is a SMOKE TEST, not a deny-paths assertion. The actual
        // semantics of MXC's deniedPaths (does deny win over allow? subtractive
        // vs strict-deny?) are not yet validated against the alpha SDK; observed
        // behavior so far is that `dir` on a denied directory returns Access
        // Denied but a file under %TEMP% appears not denied even when its parent
        // is in deniedPaths. Possible causes:
        //   - %TEMP% has implicit AppContainer access (default capabilities)
        //   - deniedPaths is strict-subtract: only effective against paths
        //     otherwise granted by readonly/readwrite
        //   - nested-AppContainer / per-capability composition may change this
        //
        // For now we only assert the runner returns SOMETHING (not a crash).
        // A proper deny-paths integration test needs a controlled allow-grant +
        // deny-of-child scenario which the alpha SDK doesn't yet support cleanly.
        var runner = TryBuildRunner();
        if (runner is null) return; // skip — MXC unavailable on this host

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo deny-semantics-test",
            Shell = "cmd",
            TimeoutMs = 30_000,
        });

        // Pipeline returned. Detailed deny-paths assertions are out of scope here.
        Assert.True(result.DurationMs > 0, $"Result should have measurable duration: {result.DurationMs}ms");
        Assert.False(result.TimedOut, "Should not have timed out");
    }

    [IntegrationFact]
    public async Task SystemRun_CmdDir_ReadsGrantedCustomFolder()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "openclaw-mxc-grant-smoke-" + Guid.NewGuid().ToString("N"))).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "sentinel.txt"), "hello");

        try
        {
            if (!HasSupportedSandboxPath(dir))
            {
                Console.WriteLine(
                    "[mxc-integration] SKIPPING: custom grant path is not in a supported local sandbox location.");
                return;
            }

            var runner = TryBuildRunner(configure: settings =>
            {
                settings.SandboxCustomFolders = new List<SandboxCustomFolder>
                {
                    new() { Path = dir, Access = SandboxFolderAccess.ReadWrite },
                };
            });
            if (runner is null) return; // skip — MXC unavailable on this host

            var result = await runner.RunAsync(new CommandRequest
            {
                Command = "dir",
                Shell = "cmd",
                Cwd = dir,
                TimeoutMs = 30_000,
            });

            Assert.True(
                result.ExitCode == 0 && result.Stdout.Contains("sentinel.txt", StringComparison.OrdinalIgnoreCase),
                $"ExitCode={result.ExitCode}\nStdout={result.Stdout}\nStderr={result.Stderr}\nTimedOut={result.TimedOut}\nDurationMs={result.DurationMs}\nDir={dir}");
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [IntegrationFact]
    public async Task SystemRun_FilesystemAccessMatrix_EnforcesReadwriteAndReadonlyPaths()
    {
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "openclaw-mxc-fs-matrix-" + Guid.NewGuid().ToString("N"))).FullName;
        var rwDir = Directory.CreateDirectory(Path.Combine(root, "rw")).FullName;
        var roDir = Directory.CreateDirectory(Path.Combine(root, "ro")).FullName;

        var roInput = Path.Combine(roDir, "input.txt");
        var rwOutput = Path.Combine(rwDir, "rw_marker.tmp");
        var roForbidden = Path.Combine(roDir, "forbidden.tmp");

        await File.WriteAllTextAsync(roInput, "readonly test data");

        try
        {
            if (!HasSupportedSandboxPath(root))
            {
                Console.WriteLine(
                    "[mxc-integration] SKIPPING: filesystem matrix path is not in a supported local sandbox location.");
                return;
            }

            var runner = TryBuildRunner(
                configure: settings =>
                {
                    settings.SandboxCustomFolders = new List<SandboxCustomFolder>
                    {
                        new() { Path = rwDir, Access = SandboxFolderAccess.ReadWrite },
                        new() { Path = roDir, Access = SandboxFolderAccess.ReadOnly },
                    };
                });
            if (runner is null) return; // skip — MXC unavailable on this host

            var command = string.Join(" & ", new[]
            {
                $"(echo RW_WRITE_VALUE > {CmdQuote(rwOutput)} && echo RW_WRITE=PASS || echo RW_WRITE=FAIL)",
                $"(type {CmdQuote(rwOutput)} > nul && echo RW_READ=PASS || echo RW_READ=FAIL)",
                $"(type {CmdQuote(roInput)} > nul && echo RO_READ=PASS || echo RO_READ=FAIL)",
                $"(echo RO_WRITE_VALUE > {CmdQuote(roForbidden)} && echo RO_WRITE=PASS || echo RO_WRITE=FAIL)",
            });

            var result = await runner.RunAsync(new CommandRequest
            {
                Command = command,
                Shell = "cmd",
                TimeoutMs = 30_000,
            });

            Assert.False(result.TimedOut, $"Filesystem matrix timed out.\nStdout={result.Stdout}\nStderr={result.Stderr}");
            var matrix = ParseMatrix(result.Stdout);
            AssertMatrix(matrix, "RW_WRITE", "PASS", result);
            AssertMatrix(matrix, "RW_READ", "PASS", result);
            AssertMatrix(matrix, "RO_READ", "PASS", result);
            AssertMatrix(matrix, "RO_WRITE", "FAIL", result);

            Assert.True(File.Exists(rwOutput), $"RW output should exist on host: {rwOutput}");
            Assert.False(File.Exists(roForbidden), $"RO output should not exist on host: {roForbidden}");
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [IntegrationFact]
    public async Task SystemRun_CustomEnv_DeniesBeforeMxcUnavailableHostFallback()
    {
        var executor = new ThrowIfCalledSandboxExecutor();
        var hostFallback = new LocalCommandRunner(NullLogger.Instance);
        var settings = new SettingsData
        {
            SystemRunSandboxEnabled = true,
            SystemRunBlockHostFallbackWhenMxcUnavailable = false,
        };
        var runner = new MxcCommandRunner(
            executor,
            hostFallback,
            () => settings,
            () => Path.Combine(Path.GetTempPath(), "openclaw-mxc-smoke-test-settings"),
            () => false,
            invalidateAvailability: null,
            new ConsoleLogger());

        var result = await runner.RunAsync(new CommandRequest
        {
            Command = "echo %OPENCLAW_MXC_ENV_FALLBACK_MARKER%",
            Shell = "cmd",
            Env = new Dictionary<string, string>
            {
                ["OPENCLAW_MXC_ENV_FALLBACK_MARKER"] = "OPENCLAW_ENV_FALLBACK_SHOULD_NOT_RUN",
            },
            TimeoutMs = 30_000,
        });

        Console.WriteLine(
            "[mxc-integration] custom-env-deny " +
            $"exitCode={result.ExitCode}; " +
            $"fallbackMarkerSeen={result.Stdout.Contains("OPENCLAW_ENV_FALLBACK_SHOULD_NOT_RUN", StringComparison.Ordinal)}; " +
            $"stderrContainsCustomEnv={result.Stderr.Contains("custom environment variables", StringComparison.OrdinalIgnoreCase)}");

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("custom environment variables", result.Stderr);
        Assert.DoesNotContain("OPENCLAW_ENV_FALLBACK_SHOULD_NOT_RUN", result.Stdout);
        Assert.Equal(0, executor.CallCount);
    }

    private static bool HasSupportedSandboxPath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return false;

            return string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGitHubActions()
        => string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseMatrix(string stdout)
    {
        var matrix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (value is "PASS" or "FAIL")
                matrix[key] = value;
        }

        return matrix;
    }

    private static void AssertMatrix(
        IReadOnlyDictionary<string, string> matrix,
        string key,
        string expected,
        CommandResult result)
    {
        Assert.True(matrix.TryGetValue(key, out var actual),
            $"Missing matrix key {key}.\nStdout={result.Stdout}\nStderr={result.Stderr}\nExitCode={result.ExitCode}");
        Assert.Equal(expected, actual);
    }

    private static string CmdQuote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed class ThrowIfCalledSandboxExecutor : ISandboxExecutor
    {
        public string Name => "throw-if-called";
        public bool IsContained => true;
        public int CallCount { get; private set; }

        public Task<SandboxExecutionResult> ExecuteAsync(
            SandboxExecutionRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Custom-env denial should happen before sandbox execution.");
        }
    }
}
