using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Wait for a short-lived setup probe, then drain leftover redirected
/// stdout. Synchronous ReadToEnd before WaitForExit never reaches the
/// timeout when the child hangs or a descendant holds the pipe open.
/// </summary>
internal static class BoundedProcessOutput
{
    internal const int DefaultTimeoutMs = 5_000;
    internal const int MaxCapturedStreamChars = 1_048_576;

    internal static async Task<(int ExitCode, string Output)> ReadAsync(
        ProcessStartInfo startInfo,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be positive.");
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, string.Empty);

        var stdout = startInfo.RedirectStandardOutput
            ? new RedirectedOutputCapture(process.StandardOutput, MaxCapturedStreamChars)
            : null;
        var stdoutTask = stdout?.Completion ?? Task.CompletedTask;
        var stderr = startInfo.RedirectStandardError
            ? new RedirectedOutputCapture(process.StandardError, MaxCapturedStreamChars)
            : null;
        var stderrTask = stderr?.Completion;

        try
        {
            var waitResult = await AwaitRedirectedOutputAsync(
                process,
                stdoutTask,
                timeoutMs,
                cancellationToken).ConfigureAwait(false);
            return waitResult.ProcessExited && process.HasExited
                ? (process.ExitCode, stdout?.Output ?? string.Empty)
                : (-1, string.Empty);
        }
        finally
        {
            if (!stdoutTask.IsCompleted)
                DisposeQuietly(process.StandardOutput, "stdout close");
            ObserveQuietly(stdoutTask);
            if (stderrTask is not null)
            {
                if (!stderrTask.IsCompleted)
                    DisposeQuietly(process.StandardError, "stderr close");
                ObserveQuietly(stderrTask);
            }
        }
    }

    internal static async Task<RedirectedOutputWaitResult> AwaitRedirectedOutputAsync(
        Process process,
        Task readTask,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readTask);
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be positive.");

        var deadline = new MonotonicDeadline(TimeSpan.FromMilliseconds(timeoutMs));
        using var exitSignalCancellation = new CancellationTokenSource();
        var exitTask = WaitForProcessExitOnlyAsync(process, exitSignalCancellation.Token);
        try
        {
            try
            {
                await WaitWithinDeadlineAsync(
                    exitTask,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                StopAndAbandon(process, readTask);
                return new RedirectedOutputWaitResult(ProcessExited: false, OutputDrained: false);
            }
            catch (OperationCanceledException)
            {
                StopAndAbandon(process, readTask);
                throw;
            }

            try
            {
                await WaitWithinDeadlineAsync(
                    readTask,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                return new RedirectedOutputWaitResult(ProcessExited: true, OutputDrained: true);
            }
            catch (TimeoutException)
            {
                AbandonRead(process, readTask);
                return new RedirectedOutputWaitResult(ProcessExited: true, OutputDrained: false);
            }
            catch (OperationCanceledException)
            {
                // The immediate process has exited. Closing this reader is the only
                // available bounded cleanup for a descendant-held pipe.
                AbandonRead(process, readTask);
                throw;
            }
        }
        finally
        {
            exitSignalCancellation.Cancel();
            ObserveQuietly(exitTask);
            ObserveQuietly(readTask);
        }
    }

    private static async Task WaitWithinDeadlineAsync(
        Task task,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException();

        await task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
    }

    private static void StopAndAbandon(Process process, Task readTask)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException ex)
        {
            TraceCleanupFailure("kill", ex);
        }
        catch (Win32Exception ex)
        {
            TraceCleanupFailure("kill", ex);
        }
        catch (NotSupportedException ex)
        {
            TraceCleanupFailure("kill", ex);
        }
        catch (AggregateException ex)
        {
            TraceCleanupFailure("kill", ex);
        }

        AbandonRead(process, readTask);
    }

    private static void AbandonRead(Process process, Task readTask)
    {
        if (!readTask.IsCompleted)
            DisposeQuietly(process.StandardOutput, "stdout close");
    }

    private static async Task WaitForProcessExitOnlyAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs e) => exited.TrySetResult();

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        try
        {
            if (!process.HasExited)
                await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static void DisposeQuietly(IDisposable disposable, string operation)
    {
        try
        {
            disposable.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            TraceCleanupFailure(operation, ex);
        }
        catch (IOException ex)
        {
            TraceCleanupFailure(operation, ex);
        }
        catch (InvalidOperationException ex)
        {
            TraceCleanupFailure(operation, ex);
        }
    }

    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void TraceCleanupFailure(string operation, Exception exception) =>
        Trace.WriteLine(
            $"BoundedProcessOutput: {operation} failed ({exception.GetType().Name}).");

    internal readonly record struct RedirectedOutputWaitResult(
        bool ProcessExited,
        bool OutputDrained);

    private sealed class RedirectedOutputCapture
    {
        private readonly StreamReader _reader;
        private readonly int _maxChars;
        private readonly Lock _lock = new();
        private readonly StringBuilder _output = new();

        internal RedirectedOutputCapture(StreamReader reader, int maxChars)
        {
            _reader = reader;
            _maxChars = maxChars;
            Completion = CaptureAsync();
        }

        internal Task Completion { get; }

        internal string Output
        {
            get
            {
                lock (_lock)
                    return _output.ToString();
            }
        }

        private async Task CaptureAsync()
        {
            var buffer = new char[4_096];
            while (await _reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
            {
                lock (_lock)
                {
                    var remaining = _maxChars - _output.Length;
                    if (remaining > 0)
                        _output.Append(buffer, 0, Math.Min(count, remaining));
                }
            }
        }
    }

    private readonly struct MonotonicDeadline
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly TimeSpan _timeout;

        internal MonotonicDeadline(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        internal TimeSpan Remaining
        {
            get
            {
                var remaining = _timeout - Stopwatch.GetElapsedTime(_startedAt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
