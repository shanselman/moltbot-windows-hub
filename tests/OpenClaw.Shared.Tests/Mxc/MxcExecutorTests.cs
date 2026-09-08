using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenClaw.Shared.Mxc;
using OpenClaw.TestSupport;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public class MxcExecutorTests
{
    [Fact]
    public async Task RunAsync_CapturesOutput_WhenLauncherExitsNormally()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateProcess(
                cmdPath,
                "echo stdout-line & echo stderr-line 1>&2"),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromSeconds(1));

        var result = await executor.RunAsync(new MxcConfig
        {
            ContainerId = "output-capture-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdout-line", result.Output);
        Assert.Contains("stderr-line", result.Error);
    }

    [Fact]
    public async Task RunAsync_CancellationCleanupIsBounded_WhenProcessTreeKillDoesNotStopLauncher()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var launcherPid = 0;
        var killAttempted = false;
        Process? launcher = null;
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => launcher = CreateProcess(
                cmdPath,
                "echo launcher-started & ping -n 31 127.0.0.1 >nul"),
            processTreeKiller: process =>
            {
                killAttempted = true;
                launcherPid = process.Id;
            },
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var run = executor.RunAsync(
                new MxcConfig
                {
                    ContainerId = "bounded-cleanup-test",
                    Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
                },
                cancellation.Token);
            await WaitForProcessStartAsync(() => launcher, TimeSpan.FromSeconds(5));
            launcherPid = launcher!.Id;
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Assert.True(killAttempted);
            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Post-cancel cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(launcherPid);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationCleanupIsBounded_WhenProcessTreeKillBlocks()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var launcherPid = 0;
        Process? launcher = null;
        using var killRelease = new ManualResetEventSlim(false);
        var killStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Process> blockingKiller = _ =>
        {
            killStarted.TrySetResult();
            killRelease.Wait();
        };
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => launcher = CreateProcess(
                cmdPath,
                "echo launcher-started & ping -n 31 127.0.0.1 >nul"),
            processTreeKiller: process =>
            {
                blockingKiller(process);
                killFinished.TrySetResult();
            },
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var run = executor.RunAsync(
                new MxcConfig
                {
                    ContainerId = "bounded-kill-test",
                    Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
                },
                cancellation.Token);
            await WaitForProcessStartAsync(() => launcher, TimeSpan.FromSeconds(5));
            launcherPid = launcher!.Id;
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();
            await killStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Assert.True(result.TimedOut);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Post-cancel cleanup took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            killRelease.Set();
            await killFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            KillProcessTree(launcherPid);
        }
    }

    [Fact]
    public async Task KillProcessTreeWithTimeoutAsync_RepeatedBlockedKillsDoNotExceedProcessWideWorkerLimit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var killStartedCount = 0;
        using var killRelease = new ManualResetEventSlim(false);
        using var killStarted = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        using var killFinished = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var attemptCount = MxcExecutor.ProcessTreeKillWorkerLimit + 2;
        var workersFinished = false;
        using var process = Process.GetCurrentProcess();

        try
        {
            var killAttempts = Enumerable.Range(0, attemptCount).Select(_ =>
            {
                var executor = new MxcExecutor(
                    cmdPath,
                    stdoutCapBytes: null,
                    stderrCapBytes: null,
                    processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                    processTreeKiller: _ =>
                    {
                        Interlocked.Increment(ref killStartedCount);
                        killStarted.Signal();
                        try
                        {
                            killRelease.Wait();
                        }
                        finally
                        {
                            killFinished.Signal();
                        }
                    },
                    cleanupTimeout: TimeSpan.FromMilliseconds(100));
                return executor.KillProcessTreeWithTimeoutAsync(process);
            }).ToArray();

            var results = await Task.WhenAll(killAttempts).WaitAsync(TimeSpan.FromSeconds(5));
            var workersStarted = killStarted.Wait(TimeSpan.FromSeconds(5));

            Assert.All(results, Assert.False);
            Assert.True(workersStarted, "Blocked kill workers did not all start within the test bound.");
            Assert.Equal(
                MxcExecutor.ProcessTreeKillWorkerLimit,
                Volatile.Read(ref killStartedCount));
        }
        finally
        {
            killRelease.Set();
            workersFinished = killFinished.Wait(TimeSpan.FromSeconds(5));
        }

        Assert.True(workersFinished, "Blocked kill workers did not exit after the test released them.");

        var recoveredKillStarted = false;
        var recoveredExecutor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => throw new InvalidOperationException("Not used by this test."),
            processTreeKiller: _ => recoveredKillStarted = true,
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        var recovered = false;
        for (var attempt = 0; attempt < 50 && !recovered; attempt++)
        {
            recovered = await recoveredExecutor.KillProcessTreeWithTimeoutAsync(process);
            if (!recovered)
                await Task.Delay(10);
        }

        Assert.True(recovered, "Process-wide kill worker capacity did not recover.");
        Assert.True(recoveredKillStarted);
    }

    [Fact]
    public async Task KillProcessTreeWithTimeoutAsync_WaitsForTransientWorkerCapacityWithinCleanupBudget()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var process = Process.GetCurrentProcess();
        using var killRelease = new SemaphoreSlim(0, MxcExecutor.ProcessTreeKillWorkerLimit);
        using var killStarted = new CountdownEvent(MxcExecutor.ProcessTreeKillWorkerLimit);
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var blockingAttempts = Array.Empty<Task<bool>>();

        try
        {
            blockingAttempts = Enumerable.Range(0, MxcExecutor.ProcessTreeKillWorkerLimit)
                .Select(_ =>
                {
                    var executor = new MxcExecutor(
                        cmdPath,
                        stdoutCapBytes: null,
                        stderrCapBytes: null,
                        processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                        processTreeKiller: _ =>
                        {
                            killStarted.Signal();
                            killRelease.Wait();
                        },
                        cleanupTimeout: TimeSpan.FromSeconds(5));
                    return executor.KillProcessTreeWithTimeoutAsync(process);
                })
                .ToArray();
            Assert.True(
                killStarted.Wait(TimeSpan.FromSeconds(5)),
                "Initial kill workers did not occupy the process-wide limit.");

            var queuedKillStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var queuedExecutor = new MxcExecutor(
                cmdPath,
                stdoutCapBytes: null,
                stderrCapBytes: null,
                processFactory: _ => throw new InvalidOperationException("Not used by this test."),
                processTreeKiller: _ => queuedKillStarted.TrySetResult(),
                cleanupTimeout: TimeSpan.FromSeconds(2));
            var queuedAttempt = queuedExecutor.KillProcessTreeWithTimeoutAsync(process);

            await Task.Delay(100);
            Assert.False(queuedKillStarted.Task.IsCompleted);
            killRelease.Release();

            Assert.True(await queuedAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
            await queuedKillStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            killRelease.Release(MxcExecutor.ProcessTreeKillWorkerLimit);
            try { await Task.WhenAll(blockingAttempts).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Fact]
    public async Task RunAsync_OutputDrainIsBounded_WhenDescendantRetainsRedirectedHandlesAfterParentExit()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var temp = new TempDirectory("mxc-drain-test-");
        var descendantPidPath = temp.Combine("descendant.pid");
        var escapedPidPath = descendantPidPath.Replace("'", "''", StringComparison.Ordinal);
        var childScript =
            $"$PID | Set-Content -LiteralPath '{escapedPidPath}'; Start-Sleep -Seconds 30";
        var encodedChildScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var command =
            "echo parent-output & start /b " +
            "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe " +
            $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedChildScript} & exit /b 0";
        var executor = new MxcExecutor(
            cmdPath,
            stdoutCapBytes: null,
            stderrCapBytes: null,
            processFactory: _ => CreateProcess(cmdPath, command),
            processTreeKiller: process => process.Kill(entireProcessTree: true),
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        var run = executor.RunAsync(new MxcConfig
        {
            ContainerId = "bounded-output-drain-test",
            Process = new MxcProcess { CommandLine = "ignored-by-test-process" },
        });
        var descendantPid = 0;

        try
        {
            await WaitForFileAsync(descendantPidPath, TimeSpan.FromSeconds(5));
            descendantPid = int.Parse(await File.ReadAllTextAsync(descendantPidPath));
            var stopwatch = Stopwatch.StartNew();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
            stopwatch.Stop();

            Assert.True(result.Success);
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("parent-output", result.Output);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Post-exit output drain took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(descendantPid);
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Fact]
    public async Task WaitForCleanupAsync_ReturnsTrue_WhenProcessAndPipesComplete()
    {
        var completed = Task.CompletedTask;

        var result = await MxcExecutor.WaitForCleanupAsync(
            completed,
            completed,
            completed,
            TimeSpan.FromSeconds(1));

        Assert.True(result);
    }

    private static async Task WaitForProcessStartAsync(
        Func<Process?> processProvider,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (processProvider()?.Id > 0)
                    return;
            }
            catch (InvalidOperationException)
            {
                // Process.Start has not completed yet.
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Test launcher did not start within the expected time.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Test descendant did not report its process ID within the expected time.");
    }

    private static Process CreateProcess(string cmdPath, string command)
    {
        var startInfo = new ProcessStartInfo(cmdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return new Process { StartInfo = startInfo };
    }

    private static void KillProcessTree(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}
