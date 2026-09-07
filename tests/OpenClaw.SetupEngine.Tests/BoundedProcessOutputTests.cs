using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task AwaitRedirectedOutputAsync_ReportsUndrainedWhenStdoutNeverCloses()
    {
        using var process = StartExitingProcess();
        Assert.NotNull(process);

        var never = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            never,
            timeoutMs: 400);

        Assert.True(result.ProcessExited);
        Assert.False(result.OutputDrained);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_UsesOneDeadlineForExitKillAndDrain()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var readTask = process.StandardOutput.ReadToEndAsync();
        var stopwatch = Stopwatch.StartNew();
        var result = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            readTask,
            timeoutMs: 400);

        Assert.False(result.ProcessExited);
        Assert.False(result.OutputDrained);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300));
        Assert.True(
            SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(2)),
            "The timed-out Tailscale probe process was not killed.");
        var readException = await Record.ExceptionAsync(
            () => readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(
            readException is null or ObjectDisposedException,
            $"Abandoned stdout read failed unexpectedly: {readException}");
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_PreservesOutputCompletedBeforeDeadline()
    {
        using var process = StartExitingProcess();
        Assert.True(process.WaitForExit(3_000));
        var outputSource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            outputSource.Task,
            timeoutMs: 5_000);

        await Task.Delay(50);
        outputSource.SetResult("{\"BackendState\":\"Running\"}");

        var result = await helper;
        Assert.True(result.ProcessExited);
        Assert.True(result.OutputDrained);
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_CancellationKillsProcessAndObservesRead()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var readTask = process.StandardOutput.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedProcessOutput.AwaitRedirectedOutputAsync(
                process,
                readTask,
                timeoutMs: 5_000,
                cancellation.Token));

        Assert.True(
            SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(2)),
            "The cancelled Tailscale probe process was not killed.");
        _ = await Record.ExceptionAsync(() => readTask);
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_BoundsInheritedHandleDrainOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var process = Process.Start(InheritedHandleCommand(childSleepSeconds: 20));
        Assert.NotNull(process);
        var readTask = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(15_000), "The parent probe process did not exit.");
        Assert.False(readTask.IsCompleted, "The descendant did not retain the redirected output handle.");
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            readTask,
            timeoutMs: 300);

        Assert.True(result.ProcessExited);
        Assert.False(result.OutputDrained);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(200));
        _ = await Record.ExceptionAsync(() => readTask.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ReadAsync_CapturesStdoutFromExitingProcess()
    {
        var (fileName, arguments) = EchoCommand("status-ok");
        var result = await BoundedProcessOutput.ReadAsync(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, timeoutMs: 5_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("status-ok", result.Output);
    }

    [Fact]
    public async Task ReadAsync_PreservesValidStatusWhenDescendantRetainsOutputHandle()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string status = """
            {"BackendState":"Running","Self":{"DNSName":"node.tailnet.ts.net."}}
            """;
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcessOutput.ReadAsync(
            InheritedHandleCommand(status, childSleepSeconds: 8),
            timeoutMs: 5_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(status, result.Output, StringComparison.Ordinal);
        Assert.True(TailscaleSetupPolicy.TryParseStatus(result.Output, out var parsedStatus));
        Assert.True(parsedStatus.IsRunning);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(4.5));
    }

    [Fact]
    public async Task ReadAsync_HungWindowsTailscaleProbeHonorsFiveSecondDeadline()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (fileName, arguments) = LongRunningCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcessOutput.ReadAsync(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(4.5),
            TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ReadAsync_CapsCapturedStdout()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(
                path,
                new string('x', BoundedProcessOutput.MaxCapturedStreamChars + 4_096));

            var result = await BoundedProcessOutput.ReadAsync(
                ReadFileCommand(path),
                timeoutMs: 5_000);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(BoundedProcessOutput.MaxCapturedStreamChars, result.Output.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsNonPositiveTimeout()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedProcessOutput.ReadAsync(startInfo, timeoutMs: 0));
    }

    private static ProcessStartInfo InheritedHandleCommand(
        string output = "inherited-output",
        int childSleepSeconds = 2)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$null = Start-Process powershell.exe " +
            $"-ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds {childSleepSeconds}' " +
            $"-NoNewWindow -PassThru; Write-Output '{output.Replace("'", "''", StringComparison.Ordinal)}'");
        return startInfo;
    }

    private static Process StartExitingProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/d /c exit 0" : "-c \"exit 0\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static ProcessStartInfo ReadFileCommand(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("type");
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("cat \"$1\"");
            startInfo.ArgumentList.Add("sh");
            startInfo.ArgumentList.Add(path);
        }

        return startInfo;
    }

    private static (string FileName, string Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /c ping 127.0.0.1 -n 20")
            : ("/bin/sh", "-c \"sleep 20\"");

    private static (string FileName, string Arguments) EchoCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /c echo {text}")
            : ("/bin/sh", $"-c \"printf '%s\\n' '{text}'\"");
}
