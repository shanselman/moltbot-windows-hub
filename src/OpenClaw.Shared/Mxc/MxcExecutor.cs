using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Runs commands inside a Windows AppContainer via wxc-exec.exe. Throws
/// <see cref="FileNotFoundException"/> on construction if the binary is absent.
/// </summary>
public sealed class MxcExecutor
{
    private const int DefaultStdoutCapBytes = 40_000;
    private const int DefaultStderrCapBytes = 5_000;
    internal const int ProcessTreeKillWorkerLimit = 8;
    private static readonly TimeSpan s_defaultCleanupTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly SemaphoreSlim s_processTreeKillWorkers = new(
        ProcessTreeKillWorkerLimit,
        ProcessTreeKillWorkerLimit);

    private readonly string _wxcExePath;
    private readonly int _stdoutCapBytes;
    private readonly int _stderrCapBytes;
    private readonly Func<ProcessStartInfo, Process> _processFactory;
    private readonly Action<Process> _processTreeKiller;
    private readonly TimeSpan _cleanupTimeout;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public MxcExecutor(string wxcExePath, int? stdoutCapBytes = null, int? stderrCapBytes = null)
        : this(
            wxcExePath,
            stdoutCapBytes,
            stderrCapBytes,
            static startInfo => new Process { StartInfo = startInfo },
            static process => process.Kill(entireProcessTree: true),
            s_defaultCleanupTimeout)
    {
    }

    internal MxcExecutor(
        string wxcExePath,
        int? stdoutCapBytes,
        int? stderrCapBytes,
        Func<ProcessStartInfo, Process> processFactory,
        Action<Process> processTreeKiller,
        TimeSpan cleanupTimeout)
    {
        if (string.IsNullOrEmpty(wxcExePath)) throw new ArgumentException("wxcExePath required", nameof(wxcExePath));
        if (!File.Exists(wxcExePath))
            throw new FileNotFoundException($"wxc-exec.exe not found at: {wxcExePath}", wxcExePath);
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(processTreeKiller);
        if (cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), "Cleanup timeout must be positive.");

        _wxcExePath = wxcExePath;
        _stdoutCapBytes = stdoutCapBytes is > 0 ? stdoutCapBytes.Value : DefaultStdoutCapBytes;
        _stderrCapBytes = stderrCapBytes is > 0 ? stderrCapBytes.Value : DefaultStderrCapBytes;
        _processFactory = processFactory;
        _processTreeKiller = processTreeKiller;
        _cleanupTimeout = cleanupTimeout;
    }

    public async Task<MxcResult> RunAsync(
        MxcConfig config,
        CancellationToken ct = default,
        bool experimental = false,
        string? workingDirectory = null)
    {
        var json = JsonSerializer.Serialize(config, s_jsonOptions);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var args = new List<string>();
        if (experimental) args.Add("--experimental");
        args.Add("--config-base64");
        args.Add(base64);
        return await RunWithArgumentsAsync(args, ct, workingDirectory);
    }

    /// <summary>
    /// Additive (OpenClaw): runs wxc-exec with <c>--config &lt;file&gt;</c> instead of
    /// <c>--config-base64</c>. Use when the serialized config approaches the Windows
    /// command-line limit (~32k chars). Caller owns the file lifetime.
    /// </summary>
    public Task<MxcResult> RunWithConfigFileAsync(
        string configFilePath,
        CancellationToken ct = default,
        bool experimental = false,
        string? workingDirectory = null)
    {
        if (string.IsNullOrEmpty(configFilePath)) throw new ArgumentException("configFilePath required", nameof(configFilePath));
        // Reject embedded quotes to avoid any argv-parsing ambiguity. NTFS allows
        // names with most punctuation but disallows '"', so this is also a
        // guard against malformed input rather than a real-world rejection.
        if (configFilePath.IndexOf('"') >= 0)
            throw new ArgumentException("configFilePath must not contain quote characters", nameof(configFilePath));
        var args = new List<string>();
        if (experimental) args.Add("--experimental");
        args.Add("--config");
        args.Add(configFilePath);
        return RunWithArgumentsAsync(args, ct, workingDirectory);
    }

    private async Task<MxcResult> RunWithArgumentsAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _wxcExePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        // ArgumentList avoids the manual-quoting trap that bites Process.Arguments
        // (each entry is escaped per Win32 CommandLineToArgvW rules by the BCL).
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        using var process = _processFactory(startInfo)
            ?? throw new InvalidOperationException("Process factory returned null.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        // StringBuilder is not thread-safe; the async event handlers can fire
        // concurrently with each other and with the post-kill ToString() read.
        var outLock = new object();
        var errLock = new object();
        var processExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            lock (outLock)
            {
                if (stdoutBuilder.Length < _stdoutCapBytes * 2)
                    stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            lock (errLock)
            {
                if (stderrBuilder.Length < _stderrCapBytes * 2)
                    stderrBuilder.AppendLine(e.Data);
            }
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => processExited.TrySetResult();

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited)
                processExited.TrySetResult();

            bool completed;
            try
            {
                await processExited.Task.WaitAsync(ct);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }

            if (!completed)
            {
                if (!await KillProcessTreeWithTimeoutAsync(process))
                    Trace.WriteLine($"MxcExecutor: process kill (cancellation path) timed out or failed (pid={process.Id}).");
                if (!await WaitForCleanupAsync(processExited.Task, stdoutClosed.Task, stderrClosed.Task, _cleanupTimeout))
                    Trace.WriteLine($"MxcExecutor: cancellation cleanup timed out (pid={process.Id}).");

                sw.Stop();
                string capturedOut;
                lock (outLock) { capturedOut = stdoutBuilder.ToString(); }
                return new MxcResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = Truncate(capturedOut, _stdoutCapBytes),
                    Error = "Execution was cancelled.",
                    TimedOut = true,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }

            // The launcher exited, but a descendant may still hold an inherited
            // stdout/stderr write handle. Bound the drain so a completed launcher
            // cannot pin the node invocation slot indefinitely.
            if (!await WaitForCleanupAsync(processExited.Task, stdoutClosed.Task, stderrClosed.Task, _cleanupTimeout))
                Trace.WriteLine($"MxcExecutor: post-exit output drain timed out (pid={process.Id}).");

            sw.Stop();
            string outRaw, errRaw;
            lock (outLock) { outRaw = stdoutBuilder.ToString().Trim(); }
            lock (errLock) { errRaw = stderrBuilder.ToString().Trim(); }
            var stdout = Truncate(outRaw, _stdoutCapBytes);
            var stderr = Truncate(errRaw, _stderrCapBytes);

            return new MxcResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = string.IsNullOrEmpty(stdout) ? null : stdout,
                Error = string.IsNullOrEmpty(stderr) ? null : stderr,
                TimedOut = false,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new MxcResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Failed to launch wxc-exec.exe: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }
    }

    internal async Task<bool> KillProcessTreeWithTimeoutAsync(Process process)
    {
        var timeoutStarted = Stopwatch.GetTimestamp();
        if (!await s_processTreeKillWorkers.WaitAsync(_cleanupTimeout))
        {
            Trace.WriteLine(
                $"MxcExecutor: timed out waiting for process kill worker capacity " +
                $"(limit={ProcessTreeKillWorkerLimit}, pid={process.Id}).");
            return false;
        }

        Task<Exception?> killTask;
        try
        {
            killTask = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        _processTreeKiller(process);
                        return (Exception?)null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                    finally
                    {
                        s_processTreeKillWorkers.Release();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            s_processTreeKillWorkers.Release();
            Trace.WriteLine($"MxcExecutor: process kill worker failed to start: {ex.Message}");
            return false;
        }

        var remainingTimeout = _cleanupTimeout - Stopwatch.GetElapsedTime(timeoutStarted);
        try
        {
            if (!killTask.IsCompleted && remainingTimeout <= TimeSpan.Zero)
                return false;

            var error = killTask.IsCompleted
                ? await killTask
                : await killTask.WaitAsync(remainingTimeout);
            if (error is null)
                return true;

            Trace.WriteLine($"MxcExecutor: process kill (cancellation path) failed: {error.Message}");
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal static async Task<bool> WaitForCleanupAsync(
        Task processExited,
        Task stdoutClosed,
        Task stderrClosed,
        TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(processExited, stdoutClosed, stderrClosed).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"\n\n... [TRUNCATED — showing first {maxLength} of {text.Length} chars]";
    }
}
