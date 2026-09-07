using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI;

internal sealed class WindowsRestartLauncher
{
    private readonly Func<ProcessStartInfo, Task<int>> _runProcessAsync;

    public WindowsRestartLauncher()
        : this(RunProcessAsync)
    {
    }

    internal WindowsRestartLauncher(Func<ProcessStartInfo, Task<int>> runProcessAsync) =>
        _runProcessAsync = runProcessAsync ?? throw new ArgumentNullException(nameof(runProcessAsync));

    public async Task RestartAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/r", "/t", "0" },
        };

        int exitCode = await _runProcessAsync(startInfo);
        if (exitCode != 0)
            throw new InvalidOperationException($"shutdown.exe exited with code {exitCode}.");
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Process.Start returned null.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
