using System.Diagnostics;
using System.Text;

namespace OpenClaw.SetupEngine;

// ─── Command Runner ───

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        string? stdinInput = null,
        CancellationToken ct = default,
        Stream? stdinStream = null);

    /// <summary>
    /// Run a command inside a WSL distro.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default (<paramref name="inputViaStdin"/> = false) the script is passed
    /// as argv to <c>wsl.exe -- bash -c &lt;script&gt;</c>. wsl.exe performs shell
    /// variable expansion on argv before invoking bash, which drops any
    /// <c>$var</c> or <c>${var}</c> reference that is not defined in the Windows
    /// process environment. See <c>docs/WSL_EXE_ARGV_PITFALL.md</c> for the full
    /// writeup.
    /// </para>
    /// <para>
    /// For multi-line scripts that use bash variables, set
    /// <paramref name="inputViaStdin"/> to <c>true</c>. The script is then piped
    /// to <c>bash -s</c> via stdin, which wsl.exe does not touch.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Safe: bash variables survive because the script arrives via stdin.
    /// await runner.RunInWslAsync(distro, """
    ///     workspace='/home/me/ws'
    ///     mkdir -p "$workspace"
    ///     """, TimeSpan.FromSeconds(15), inputViaStdin: true);
    /// </code>
    /// </example>
    /// <param name="distroName">The WSL distribution name passed to <c>wsl.exe -d</c>.</param>
    /// <param name="command">The bash script or command to run.</param>
    /// <param name="timeout">The maximum time to wait before killing the process.</param>
    /// <param name="environment">Optional environment variables to forward into WSL via WSLENV.</param>
    /// <param name="ct">A cancellation token for aborting the process.</param>
    /// <param name="user">Optional WSL user passed to <c>wsl.exe -u</c>.</param>
    /// <param name="inputViaStdin">
    /// When true, the script is piped to <c>bash -s</c> via stdin instead of
    /// being passed as argv to <c>bash -c</c>. Use this for any multi-line
    /// script that uses bash variables or <c>$$</c>.
    /// </param>
    Task<CommandResult> RunInWslAsync(
        string distroName,
        string command,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        string? user = null,
        bool inputViaStdin = false);
}

public sealed class CommandRunner : ICommandRunner
{
    private readonly SetupLogger _logger;
    private static readonly TimeSpan s_outputDrainGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Upper bound on how long an exited process is given to reach EOF on its redirected
    /// pipes. Large enough that a saturated thread pool cannot make a process that did
    /// write output look silent, small enough that a descendant holding the pipes open
    /// cannot stall the caller.
    /// </summary>
    private static readonly TimeSpan s_outputDrainCap = TimeSpan.FromSeconds(3);
    private const int MaxCapturedStreamChars = 1_048_576;

    public CommandRunner(SetupLogger logger) => _logger = logger;

    /// <summary>
    /// Run a process and capture output. Timeout kills the process.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        string? stdinInput = null,
        CancellationToken ct = default,
        Stream? stdinStream = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (stdinInput != null && stdinStream != null)
            throw new ArgumentException("Only one standard input source may be provided.");

        _logger.CommandStarted(executable, arguments, timeout);
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinInput != null || stdinStream != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? ""
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment != null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        if (IsWslExecutable(executable))
        {
            // Redirected wsl.exe output defaults to UTF-16LE unless WSL_UTF8=1.
            // Force the documented UTF-8 mode so every WSL invocation has one
            // deterministic redirected-output encoding.
            psi.Environment["WSL_UTF8"] = "1";
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new BoundedOutputBuffer(MaxCapturedStreamChars);
        var stderr = new BoundedOutputBuffer(MaxCapturedStreamChars);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutClosed.TrySetResult();
            else stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrClosed.TrySetResult();
            else stderr.AppendLine(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            sw.Stop();
            var errorResult = new CommandResult(-1, "", $"Failed to start process '{executable}': {ex.Message}", sw.Elapsed, false);
            _logger.CommandCompleted(executable, errorResult, sw.Elapsed);
            return errorResult;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            if (stdinInput != null || stdinStream != null)
            {
                try
                {
                    if (stdinStream != null)
                    {
                        await stdinStream.CopyToAsync(process.StandardInput.BaseStream, timeoutCts.Token);
                        await process.StandardInput.BaseStream.FlushAsync(timeoutCts.Token);
                    }
                    else
                    {
                        await process.StandardInput.WriteAsync(stdinInput!.AsMemory(), timeoutCts.Token);
                        await process.StandardInput.FlushAsync(timeoutCts.Token);
                    }

                    process.StandardInput.Close();
                }
                catch (IOException) when (process.HasExited)
                {
                    // The child exited before consuming all input. Preserve its exit status/output.
                }
            }

            await WaitForProcessExitOnlyAsync(process, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            // User cancelled
            TryKill(process);
            throw;
        }

        // A surviving descendant can keep inherited pipe handles open after the child
        // exits, so the EOF wait must stay bounded and cannot simply run to the command
        // timeout. It also cannot be as tight as a few hundred milliseconds: the
        // OutputDataReceived callbacks are queued to the thread pool, and on a saturated
        // pool they can miss that window even though the process already exited and
        // wrote its output. That surfaced as an exited process being reported with empty
        // stdout and stderr, which every caller that classifies command output then
        // misreads as silence. Wait up to a short cap instead, clamped by whatever
        // remains of the caller's own deadline so the accepted timeout is never exceeded.
        TimeSpan drainBudget = GetOutputDrainBudget(timeout, sw.Elapsed, timedOut);

        await Task.WhenAny(
            Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
            Task.Delay(drainBudget));

        sw.Stop();
        var result = new CommandResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            sw.Elapsed,
            timedOut);

        _logger.CommandCompleted(executable, result, sw.Elapsed);
        return result;
    }

    internal static TimeSpan GetOutputDrainBudget(
        TimeSpan timeout,
        TimeSpan elapsed,
        bool timedOut)
    {
        if (timedOut)
            return s_outputDrainGrace;

        TimeSpan remaining = timeout - elapsed;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining < s_outputDrainCap ? remaining : s_outputDrainCap;
    }

    internal static bool IsWslExecutable(string executable) =>
        string.Equals(Path.GetFileName(executable), "wsl.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Run a command inside a WSL distro.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default (<paramref name="inputViaStdin"/> = false) the script is passed
    /// as argv to <c>wsl.exe -- bash -c &lt;script&gt;</c>. wsl.exe performs shell
    /// variable expansion on argv before invoking bash, which drops any
    /// <c>$var</c> or <c>${var}</c> reference that is not defined in the Windows
    /// process environment. See <c>docs/WSL_EXE_ARGV_PITFALL.md</c> for the full
    /// writeup.
    /// </para>
    /// <para>
    /// For multi-line scripts that use bash variables, set
    /// <paramref name="inputViaStdin"/> to <c>true</c>. The script is then piped
    /// to <c>bash -s</c> via stdin, which wsl.exe does not touch.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Safe: bash variables survive because the script arrives via stdin.
    /// await runner.RunInWslAsync(distro, """
    ///     workspace='/home/me/ws'
    ///     mkdir -p "$workspace"
    ///     """, TimeSpan.FromSeconds(15), inputViaStdin: true);
    /// </code>
    /// </example>
    /// <param name="distroName">The WSL distribution name passed to <c>wsl.exe -d</c>.</param>
    /// <param name="command">The bash script or command to run.</param>
    /// <param name="timeout">The maximum time to wait before killing the process.</param>
    /// <param name="environment">Optional environment variables to forward into WSL via WSLENV.</param>
    /// <param name="ct">A cancellation token for aborting the process.</param>
    /// <param name="user">Optional WSL user passed to <c>wsl.exe -u</c>.</param>
    /// <param name="inputViaStdin">
    /// When true, the script is piped to <c>bash -s</c> via stdin instead of
    /// being passed as argv to <c>bash -c</c>. Use this for any multi-line
    /// script that uses bash variables or <c>$$</c>.
    /// </param>
    public Task<CommandResult> RunInWslAsync(
        string distroName,
        string command,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        string? user = null,
        bool inputViaStdin = false)
    {
        // Strip Windows \r to avoid bash "$'\r': command not found" errors
        command = command.Replace("\r", "");

        // Build wsl.exe arguments: -d <distro> [-u <user>] -- <shell command>
        var args = new List<string> { "-d", distroName };
        if (!string.IsNullOrWhiteSpace(user))
        {
            args.Add("-u");
            args.Add(user);
        }

        if (inputViaStdin)
            args.AddRange(["--", "bash", "-s"]);
        else
            args.AddRange(["--", "bash", "-c", command]);

        // Pass WSL environment variables via WSLENV
        Dictionary<string, string>? env = null;
        if (environment is { Count: > 0 })
        {
            env = new Dictionary<string, string>(environment);
            var wslEnvKeys = string.Join(":", environment.Keys);
            env["WSLENV"] = env.TryGetValue("WSLENV", out var existing)
                ? $"{existing}:{wslEnvKeys}"
                : wslEnvKeys;
        }

        if (inputViaStdin)
            return RunAsync("wsl.exe", args.ToArray(), timeout, env, stdinInput: command, ct: ct);

        return RunAsync("wsl.exe", args.ToArray(), timeout, env, ct: ct);
    }

    private static async Task WaitForProcessExitOnlyAsync(Process process, CancellationToken ct)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs e) => exited.TrySetResult();

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        try
        {
            if (!process.HasExited)
            {
                using var registration = ct.Register(() => exited.TrySetCanceled(ct));
                await exited.Task;
            }
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            // Best effort — process may already be exiting; no logger in this static helper.
            Trace.WriteLine($"CommandRunner.TryKill: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class BoundedOutputBuffer(int maxChars)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _lock = new();
        private int _droppedChars;

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                if (_builder.Length < maxChars)
                {
                    var remaining = maxChars - _builder.Length;
                    if (line.Length + Environment.NewLine.Length <= remaining)
                    {
                        _builder.AppendLine(line);
                        return;
                    }

                    if (remaining > 0)
                        _builder.Append(line[..Math.Min(line.Length, remaining)]);
                }

                _droppedChars += line.Length + Environment.NewLine.Length;
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_droppedChars == 0)
                    return _builder.ToString();

                return _builder.ToString() + Environment.NewLine + $"... [truncated {_droppedChars} chars]";
            }
        }
    }
}
