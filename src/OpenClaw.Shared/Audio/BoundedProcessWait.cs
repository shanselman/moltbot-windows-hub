using System.ComponentModel;
using System.Diagnostics;

namespace OpenClaw.Shared.Audio;

internal sealed record BoundedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Waits for a short-lived child process and its redirected output using one deadline.
/// Timeout and cancellation cleanup is best effort, bounded, and observes any
/// read failures caused by closing inherited pipe handles.
/// </summary>
internal static class BoundedProcessWait
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);

    internal static async Task<BoundedProcessResult> WaitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        var stdoutTask = StartDrain(process, standardOutput: true);
        var stderrTask = StartDrain(process, standardOutput: false);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var completionTask = Task.WhenAll(exitTask, stdoutTask, stderrTask);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await completionTask.WaitAsync(deadlineSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || timeoutSource.IsCancellationRequested)
        {
            await CleanupAsync(process, exitTask, stdoutTask, stderrTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            throw new TimeoutException($"Process did not exit and drain output within {timeout.TotalMilliseconds:F0}ms.");
        }
        catch
        {
            await CleanupAsync(process, exitTask, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        return new BoundedProcessResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static Task<string> StartDrain(Process process, bool standardOutput)
    {
        if (standardOutput)
        {
            return process.StartInfo.RedirectStandardOutput
                ? process.StandardOutput.ReadToEndAsync()
                : Task.FromResult(string.Empty);
        }

        return process.StartInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);
    }

    private static async Task CleanupAsync(
        Process process,
        Task exitTask,
        Task stdoutTask,
        Task stderrTask)
    {
        var killTask = Task.Run(() => TryKillTree(process));
        ObserveFault(killTask);
        ObserveFault(exitTask);
        ObserveFault(stdoutTask);
        ObserveFault(stderrTask);

        DisposeRedirectedReaders(process);

        var cleanupTask = Task.WhenAll(killTask, exitTask, stdoutTask, stderrTask);
        ObserveFault(cleanupTask);
        await Task.WhenAny(cleanupTask, Task.Delay(CleanupTimeout)).ConfigureAwait(false);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            // Process can only enumerate and terminate descendants while the
            // root is alive. Closing our readers still bounds inherited pipes.
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
    }

    private static void DisposeRedirectedReaders(Process process)
    {
        if (process.StartInfo.RedirectStandardOutput)
        {
            try
            {
                process.StandardOutput.Dispose();
            }
            catch (InvalidOperationException ex)
            {
                TraceCleanupFailure("stdout close", ex);
            }
            catch (IOException ex)
            {
                TraceCleanupFailure("stdout close", ex);
            }
        }

        if (process.StartInfo.RedirectStandardError)
        {
            try
            {
                process.StandardError.Dispose();
            }
            catch (InvalidOperationException ex)
            {
                TraceCleanupFailure("stderr close", ex);
            }
            catch (IOException ex)
            {
                TraceCleanupFailure("stderr close", ex);
            }
        }
    }

    internal static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void TraceCleanupFailure(string operation, Exception exception) =>
        Trace.WriteLine(
            $"BoundedProcessWait: {operation} failed: {exception.GetType().Name}: {exception.Message}");
}
