// <summary>
// Native Windows process host for managed local AI executables. Starts inference processes
// inside a kill-on-close Job Object (no orphaned children), reports exits, and captures
// stdout/stderr into bounded, sanitized, rotating log files (BoundedRotatingLogWriter).
// </summary>
// Usage:
//   ILocalAiManagedProcessHost host = new WindowsLocalAiManagedProcessHost(logger);
//   ILocalAiManagedProcess process = await host.StartProcessAsync(
//       new LocalAiProcessStartSpec(
//           ExecutablePath: install.ExecutablePath, WorkingDirectory: Path.GetDirectoryName(install.ExecutablePath)!,
//           Arguments: launchPlan.Arguments, Environment: launchPlan.Environment,
//           StandardOutputLogPath: paths.StandardOutputLogPath, StandardErrorLogPath: paths.StandardErrorLogPath,
//           MaxLogBytes: 8 * 1024 * 1024, LogBackupCount: 2, MaxLogLineCharacters: 16 * 1024),
//       exited: exit => OnExited(exit),
//       cancellationToken);
//   await process.StopAsync(TimeSpan.FromSeconds(10), cancellationToken); // job object kills children
using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Connection.LocalAi;

internal sealed record LocalAiProcessStartSpec(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string StandardOutputLogPath,
    string StandardErrorLogPath,
    long MaxLogBytes,
    int LogBackupCount,
    int MaxLogLineCharacters);

internal sealed record LocalAiManagedProcessExit(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    int? ExitCode);

internal interface ILocalAiManagedProcess : IAsyncDisposable
{
    int ProcessId { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool HasExited { get; }
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface ILocalAiManagedProcessHost
{
    Task<ILocalAiManagedProcess> StartProcessAsync(
        LocalAiProcessStartSpec spec,
        Action<LocalAiManagedProcessExit> exited,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts a native Windows inference process in a kill-on-close Job Object and
/// captures its output in bounded, sanitized logs.
/// </summary>
internal sealed class WindowsLocalAiManagedProcessHost(IOpenClawLogger logger) : ILocalAiManagedProcessHost
{
    public Task<ILocalAiManagedProcess> StartProcessAsync(
        LocalAiProcessStartSpec spec,
        Action<LocalAiManagedProcessExit> exited,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(exited);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed local inference is supported only on Windows.");

        Directory.CreateDirectory(spec.WorkingDirectory);
        var stdout = new BoundedRotatingLogWriter(
            spec.StandardOutputLogPath,
            spec.MaxLogBytes,
            spec.LogBackupCount,
            spec.MaxLogLineCharacters,
            logger);
        var stderr = new BoundedRotatingLogWriter(
            spec.StandardErrorLogPath,
            spec.MaxLogBytes,
            spec.LogBackupCount,
            spec.MaxLogLineCharacters,
            logger);
        var process = new Process
        {
            StartInfo = CreateStartInfo(spec),
            EnableRaisingEvents = false,
        };
        SafeJobHandle? job = null;
        try
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                    stdout.WriteLine(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                    stderr.WriteLine(eventArgs.Data);
            };
            if (!process.Start())
                throw new InvalidOperationException("The managed local inference process did not start.");

            var processId = process.Id;
            var startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            job = WindowsJob.CreateKillOnClose();
            if (!AssignProcessToJobObject(job, process.SafeHandle))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not assign the managed local inference process to its lifecycle job.");
            }

            var managed = new WindowsManagedProcess(
                process,
                job,
                stdout,
                stderr,
                processId,
                startedAtUtc,
                exited,
                logger);
            job = null;
            managed.EnableExitNotifications();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return Task.FromResult<ILocalAiManagedProcess>(managed);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The Job Object close below remains the authoritative cleanup.
            }

            job?.Dispose();
            process.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(LocalAiProcessStartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);
        if (spec.Arguments is null)
            throw new ArgumentException("An explicit argument list is required.", nameof(spec));
        if (spec.Environment is null)
            throw new ArgumentException("An explicit environment map is required.", nameof(spec));

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
        {
            if (argument is null)
                throw new ArgumentException("Process arguments cannot contain null values.", nameof(spec));
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var pair in spec.Environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                throw new ArgumentException("Process environment entries must have non-empty keys and non-null values.", nameof(spec));
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private sealed class WindowsManagedProcess : ILocalAiManagedProcess
    {
        private readonly Process _process;
        private readonly SafeJobHandle _job;
        private readonly BoundedRotatingLogWriter _stdout;
        private readonly BoundedRotatingLogWriter _stderr;
        private readonly Action<LocalAiManagedProcessExit> _exited;
        private readonly IOpenClawLogger _logger;
        private int _exitNotified;
        private int _disposed;

        public WindowsManagedProcess(
            Process process,
            SafeJobHandle job,
            BoundedRotatingLogWriter stdout,
            BoundedRotatingLogWriter stderr,
            int processId,
            DateTimeOffset startedAtUtc,
            Action<LocalAiManagedProcessExit> exited,
            IOpenClawLogger logger)
        {
            _process = process;
            _job = job;
            _stdout = stdout;
            _stderr = stderr;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            _exited = exited;
            _logger = logger;
        }

        public int ProcessId { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void EnableExitNotifications()
        {
            _process.Exited += (_, _) => NotifyExited();
            _process.EnableRaisingEvents = true;
        }

        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "The process stop timeout must be positive.");
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited)
                return;

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await _process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The managed local inference process did not stop within the configured timeout.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not stop the managed local inference process cleanly: {TokenSanitizer.SanitizeLogMessage(ex.Message)}");
            }
            finally
            {
                _job.Dispose();
                _process.Dispose();
                _stdout.Dispose();
                _stderr.Dispose();
            }
        }

        private void NotifyExited()
        {
            if (Interlocked.Exchange(ref _exitNotified, 1) != 0)
                return;

            int? exitCode = null;
            try
            {
                exitCode = _process.ExitCode;
            }
            catch
            {
                // The exact PID and start time remain useful even if the code raced disposal.
            }

            try
            {
                _exited(new LocalAiManagedProcessExit(ProcessId, StartedAtUtc, exitCode));
            }
            catch (Exception ex)
            {
                _logger.Warn($"The managed local inference exit callback failed: {TokenSanitizer.SanitizeLogMessage(ex.Message)}");
            }
        }
    }

    private static class WindowsJob
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        public static SafeJobHandle CreateKillOnClose()
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create the managed local inference lifecycle job.");
            }

            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformationClass,
                        pointer,
                        checked((uint)size)))
                {
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not configure the managed local inference lifecycle job.");
                }

                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        IntPtr information,
        uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class BoundedRotatingLogWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _backupCount;
    private readonly int _maxLineCharacters;
    private readonly IOpenClawLogger _logger;
    private bool _disposed;

    public BoundedRotatingLogWriter(
        string path,
        long maxBytes,
        int backupCount,
        int maxLineCharacters,
        IOpenClawLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        _path = path;
        _maxBytes = Math.Max(1024, maxBytes);
        _backupCount = Math.Clamp(backupCount, 0, 10);
        _maxLineCharacters = Math.Max(256, maxLineCharacters);
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void WriteLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                var sanitized = TokenSanitizer.SanitizeLogMessage(line);
                sanitized = ReplaceLineBreakingCharacters(sanitized);
                if (sanitized.Length > _maxLineCharacters)
                    sanitized = sanitized[.._maxLineCharacters] + " [truncated]";

                var newlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);
                var allowedBytes = checked((int)Math.Min(int.MaxValue, _maxBytes - newlineBytes));
                while (Encoding.UTF8.GetByteCount(sanitized) > allowedBytes && sanitized.Length > 1)
                    sanitized = sanitized[..Math.Max(1, sanitized.Length * 3 / 4)];

                var bytes = Encoding.UTF8.GetByteCount(sanitized) + newlineBytes;
                var currentBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                if (currentBytes + bytes > _maxBytes)
                    Rotate();
                File.AppendAllText(_path, sanitized + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"Could not write the managed local inference log: {TokenSanitizer.SanitizeLogMessage(ex.Message)}");
            }
        }
    }

    private static string ReplaceLineBreakingCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029' ? ' ' : character);
        }

        return builder.ToString();
    }

    private void Rotate()
    {
        if (_backupCount == 0)
        {
            File.Delete(_path);
            return;
        }

        File.Delete(_path + "." + _backupCount);
        for (var index = _backupCount - 1; index >= 1; index--)
        {
            var source = _path + "." + index;
            if (File.Exists(source))
                File.Move(source, _path + "." + (index + 1), overwrite: true);
        }
        if (File.Exists(_path))
            File.Move(_path, _path + ".1", overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
    }
}
