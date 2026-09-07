using System.Diagnostics;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.Tray.Tests;

public sealed class WindowsRestartLauncherTests
{
    [Fact]
    public async Task RestartAsync_NonzeroExitCodeIsRejected()
    {
        ProcessStartInfo? observedStartInfo = null;
        var launcher = new WindowsRestartLauncher(startInfo =>
        {
            observedStartInfo = startInfo;
            return Task.FromResult(5);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(launcher.RestartAsync);

        var startInfo = Assert.IsType<ProcessStartInfo>(observedStartInfo);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), startInfo.FileName);
        Assert.Equal(["/r", "/t", "0"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Contains("code 5", exception.Message);
    }
}
