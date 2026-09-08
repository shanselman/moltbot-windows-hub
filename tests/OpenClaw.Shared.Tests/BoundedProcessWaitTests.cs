using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class BoundedProcessWaitTests
{
    [Fact]
    public async Task WaitAsync_ReturnsCompleteOutput_WhenProcessSucceeds()
    {
        using var process = StartFixture("success");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stdout-first-stdout-last", result.StandardOutput);
        Assert.Equal("stderr-first-stderr-last", result.StandardError);
    }

    [Fact]
    public async Task WaitAsync_TimesOutAndKillsProcess()
    {
        using var process = StartFixture("hold", "30000");
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(
            () => BoundedProcessWait.WaitAsync(process, TimeSpan.FromMilliseconds(200)));

        stopwatch.Stop();
        Assert.True(process.HasExited);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Timeout cleanup took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WaitAsync_CancellationKillsProcessWithoutUsingTimeoutBudget()
    {
        using var process = StartFixture("hold", "30000");
        using var cancellation = new CancellationTokenSource();
        var wait = BoundedProcessWait.WaitAsync(
            process,
            BoundedProcessWait.DefaultTimeout,
            cancellation.Token);
        await Task.Delay(100);
        var stopwatch = Stopwatch.StartNew();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        stopwatch.Stop();
        Assert.True(process.HasExited);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Cancellation cleanup took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task WaitAsync_AlreadyCanceledTokenStillKillsStartedProcess()
    {
        using var process = StartFixture("hold", "30000");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedProcessWait.WaitAsync(
                process,
                BoundedProcessWait.DefaultTimeout,
                cancellation.Token));

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task WaitAsync_PreservesOutputWrittenLateWithinDeadline()
    {
        using var process = StartFixture("late-output", "250");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(3));

        Assert.Equal("late-stdout", result.StandardOutput);
        Assert.Equal("late-stderr", result.StandardError);
    }

    [Fact]
    public async Task WaitAsync_CancellationDoesNotWaitForInheritedPipeHandles()
    {
        var pidFile = Path.GetTempFileName();
        var childPid = 0;
        try
        {
            using var process = StartFixture("inherit-handles", "30000", pidFile);
            using var cancellation = new CancellationTokenSource();
            var wait = BoundedProcessWait.WaitAsync(
                process,
                BoundedProcessWait.DefaultTimeout,
                cancellation.Token);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(wait.IsCompleted);
            childPid = await ReadChildPidAsync(pidFile);
            var stopwatch = Stopwatch.StartNew();

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"Inherited-handle cancellation took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            KillProcessTree(childPid);
            File.Delete(pidFile);
        }
    }

    private static Process StartFixture(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindTestHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--process-fixture");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start process fixture.");
    }

    private static string FindTestHost()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var executableName = OperatingSystem.IsWindows()
            ? "OpenClaw.Shared.TestHost.exe"
            : "OpenClaw.Shared.TestHost";
        var hostPath = Path.Combine(
            current.FullName,
            "tests",
            "OpenClaw.Shared.TestHost",
            "bin",
            configuration,
            "net10.0",
            executableName);
        Assert.True(File.Exists(hostPath), $"Process test host was not built: {hostPath}");
        return hostPath;
    }

    private static async Task<int> ReadChildPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var text = await File.ReadAllTextAsync(pidFile);
            if (int.TryParse(text, out var processId))
                return processId;

            await Task.Delay(10);
        }

        throw new TimeoutException("Inherited-handle fixture did not publish its child PID.");
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
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (AggregateException)
        {
        }
    }
}
