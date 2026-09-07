using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public class CommandRunnerTests
{
    private static readonly string s_largeStdin = new('x', 8 * 1024 * 1024);

    [Fact]
    public async Task RunAsync_WslVersion_DecodesOutputBeforeLogging()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(WslConstants.WslExePath))
            return;

        var logPath = Path.Combine(Path.GetTempPath(), $"openclaw-wsl-output-{Guid.NewGuid():N}.jsonl");

        try
        {
            CommandResult result;
            using (var logger = new SetupLogger(logPath, LogLevel.Trace))
            {
                var runner = new CommandRunner(logger);
                result = await runner.RunAsync(
                    WslConstants.WslExePath,
                    ["--version"],
                    TimeSpan.FromSeconds(15),
                    environment: new Dictionary<string, string>
                    {
                        ["WSL_UTF8"] = "0",
                    });
            }

            var output = result.Stdout + result.Stderr;
            Assert.NotEmpty(output);
            Assert.Contains("WSL version", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('\0', output);

            var jsonl = await File.ReadAllTextAsync(logPath);
            Assert.DoesNotContain("\\u0000", jsonl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Theory]
    [InlineData("wsl.exe")]
    [InlineData(@"C:\Windows\System32\WSL.EXE")]
    public void IsWslExecutable_WslPath_ReturnsTrue(string executable)
    {
        Assert.True(CommandRunner.IsWslExecutable(executable));
    }

    [Theory]
    [InlineData("wsl")]
    [InlineData("powershell.exe")]
    [InlineData("")]
    public void IsWslExecutable_OtherPath_ReturnsFalse(string executable)
    {
        Assert.False(CommandRunner.IsWslExecutable(executable));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysTimeout()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromMilliseconds(250),
            stdinInput: s_largeStdin);

        Assert.True(result.TimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysCallerCancellation()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30),
            stdinInput: s_largeStdin,
            ct: cts.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_StreamStdinPreservesBinaryBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-stdin-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 0, 1, 2, 0x7F, 0x80, 0xFF };
        var (executable, arguments) = CopyStdinCommand(path);
        await using var input = new MemoryStream(bytes);

        try
        {
            var result = await CreateRunner().RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(15),
                stdinStream: input);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsMultipleStdinSources()
    {
        var (executable, arguments) = SleepingCommand();
        await using var input = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(1),
            stdinInput: "text",
            stdinStream: input));
    }

    [Fact]
    public async Task RunAsync_ReturnsWhenDescendantKeepsOutputPipesOpen()
    {
        var runner = CreateRunner();
        var (executable, arguments) = ExitsLeavingPipeHolderCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsyncAllowingInheritedPipeHandleEscape(
            executable,
            arguments,
            TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForOutputDrainAsync_WaitsPastCommandDeadlineOnNormalExit()
    {
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = CommandRunner.WaitForOutputDrainAsync(
            outputClosed.Task,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero,
            timedOut: false,
            allowInheritedPipeHandleEscape: false);

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(wait, completed);

        outputClosed.SetResult();
        await wait;
    }

    [Fact]
    public async Task WaitForOutputDrainAsync_CancellationInterruptsNormalPostExitDrain()
    {
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandRunner.WaitForOutputDrainAsync(
                outputClosed.Task,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero,
                timedOut: false,
                allowInheritedPipeHandleEscape: false,
                cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_DrainsHighVolumeStdoutAndStderrThroughTrailingMarkers()
    {
        const int lineCount = 8_000;
        var (executable, arguments) = HighVolumeOutputCommand(lineCount);

        var result = await CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stdout, "stdout-"));
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stderr, "stderr-"));
        Assert.EndsWith($"STDOUT_MARKER{Environment.NewLine}", result.Stdout, StringComparison.Ordinal);
        Assert.EndsWith($"STDERR_MARKER{Environment.NewLine}", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, 175, false, 25)]
    [InlineData(200, 250, false, 0)]
    [InlineData(5_000, 1_000, false, 3_000)]
    [InlineData(200, 250, true, 250)]
    public void OutputDrainBudget_ClampsToDeadlineAndCap(
        int timeoutMilliseconds,
        int elapsedMilliseconds,
        bool timedOut,
        int expectedMilliseconds)
    {
        TimeSpan budget = CommandRunner.GetOutputDrainBudget(
            TimeSpan.FromMilliseconds(timeoutMilliseconds),
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            timedOut);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), budget);
    }

    private static CommandRunner CreateRunner()
        => new(new SetupLogger(filePath: null, LogLevel.Trace));

    private static (string Executable, string[] Arguments) ExitsLeavingPipeHolderCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "start /b ping 127.0.0.1 -n 20"])
            : ("/bin/sh", ["-c", "sleep 20 & exit 0"]);

    private static (string Executable, string[] Arguments) SleepingCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"])
            : ("/bin/sh", ["-c", "sleep 30"]);

    private static (string Executable, string[] Arguments) HighVolumeOutputCommand(int lineCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("/bin/sh",
            [
                "-c",
                "i=0; while [ $i -lt " + lineCount + " ]; do printf 'stdout-%s\\n' \"$i\"; i=$((i+1)); done; " +
                "printf 'STDOUT_MARKER\\n'; " +
                "i=0; while [ $i -lt " + lineCount + " ]; do printf 'stderr-%s\\n' \"$i\" >&2; i=$((i+1)); done; " +
                "printf 'STDERR_MARKER\\n' >&2"
            ]);
        }

        var script =
            $"for ($i = 0; $i -lt {lineCount}; $i++) {{ [Console]::Out.WriteLine(\"stdout-$i\") }}; " +
            "[Console]::Out.WriteLine(\"STDOUT_MARKER\"); " +
            $"for ($i = 0; $i -lt {lineCount}; $i++) {{ [Console]::Error.WriteLine(\"stderr-$i\") }}; " +
            "[Console]::Error.WriteLine(\"STDERR_MARKER\")";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }

    private static int CountLinesStartingWith(string value, string prefix) =>
        value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static (string Executable, string[] Arguments) CopyStdinCommand(string path)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/sh", ["-c", $"cat > '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);

        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$inputStream = [Console]::OpenStandardInput(); $output = [IO.File]::Create('{escapedPath}'); " +
            "try { $inputStream.CopyTo($output) } finally { $output.Dispose() }";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }
}
