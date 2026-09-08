using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using OpenClaw.Shared.Audio;

namespace OpenClaw.Shared.Tests;

public sealed class PiperVoiceExtractionTests
{
    [Fact]
    public void IsVoiceDownloaded_RejectsIncompleteInstall_AndDeleteVoiceRemovesMarker()
    {
        using var directory = new TemporaryDirectory();
        var voice = PiperVoiceManager.AvailableVoices[0];
        var manager = new PiperVoiceManager(directory.Path, NullLogger.Instance);
        var voiceDirectory = Directory.CreateDirectory(manager.GetVoiceDirectory(voice.VoiceId));
        File.WriteAllText(manager.GetModelPath(voice.VoiceId), "partial-model");
        File.WriteAllText(manager.GetTokensPath(voice.VoiceId), "tokens");
        Directory.CreateDirectory(manager.GetEspeakDataDir(voice.VoiceId));
        var markerPath = $"{voiceDirectory.FullName}.installing";
        File.WriteAllText(markerPath, string.Empty);

        Assert.False(manager.IsVoiceDownloaded(voice.VoiceId));

        File.Delete(markerPath);
        Assert.True(manager.IsVoiceDownloaded(voice.VoiceId));

        File.WriteAllText(markerPath, string.Empty);
        Assert.True(manager.DeleteVoice(voice.VoiceId));
        Assert.False(Directory.Exists(voiceDirectory.FullName));
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ExtractTarBz2Async_ExtractsArchiveWithWindowsTar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var directory = new TemporaryDirectory();
        var packageDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "source", "package"));
        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "destination"));
        var archivePath = Path.Combine(directory.Path, "voice.tar.bz2");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory.FullName, "voice.onnx"),
            "model-bytes");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory.FullName, "voice.onnx.json"),
            "{\"audio\":{\"sample_rate\":22050}}");
        await CreateTarBz2Async(
            archivePath,
            packageDirectory.Parent!.FullName,
            packageDirectory.Name);

        await PiperVoiceManager.ExtractTarBz2Async(
            archivePath,
            destinationDirectory.FullName,
            CancellationToken.None);

        Assert.Equal(
            "model-bytes",
            await File.ReadAllTextAsync(Path.Combine(destinationDirectory.FullName, "voice.onnx")));
        Assert.True(File.Exists(Path.Combine(destinationDirectory.FullName, "voice.onnx.json")));
    }

    [Fact]
    public async Task ExtractTarBz2Async_TimeoutIsBoundedAndKillsExtractor()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var processId = 0;
        var extraction = PiperVoiceManager.ExtractTarBz2Async(
            "fixture-hold",
            directory.Path,
            cancellation.Token,
            FindTestHost(),
            TimeSpan.FromSeconds(2));

        try
        {
            processId = await ReadFixturePidAsync(directory.Path);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => extraction);

            stopwatch.Stop();
            Assert.IsType<TimeoutException>(exception.InnerException);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Piper timeout cleanup took {stopwatch.ElapsedMilliseconds} ms.");
            await AssertFixtureExitedAsync(processId);
        }
        finally
        {
            await AwaitFixtureCleanupAsync(extraction, cancellation);
            KillProcessTree(processId);
        }
    }

    [Fact]
    public async Task ExtractTarBz2Async_CancellationIsBoundedAndKillsExtractor()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var processId = 0;
        var extraction = PiperVoiceManager.ExtractTarBz2Async(
            "fixture-hold",
            directory.Path,
            cancellation.Token,
            FindTestHost(),
            BoundedProcessWait.DefaultTimeout);
        try
        {
            processId = await ReadFixturePidAsync(directory.Path);
            var stopwatch = Stopwatch.StartNew();

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => extraction);

            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"Piper cancellation cleanup took {stopwatch.ElapsedMilliseconds} ms.");
            await AssertFixtureExitedAsync(processId);
        }
        finally
        {
            await AwaitFixtureCleanupAsync(extraction, cancellation);
            KillProcessTree(processId);
        }
    }

    private static async Task CreateTarBz2Async(
        string archivePath,
        string sourceDirectory,
        string packageName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-cjf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(sourceDirectory);
        startInfo.ArgumentList.Add(packageName);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start tar archive fixture.");

        var result = await BoundedProcessWait.WaitAsync(process, TimeSpan.FromSeconds(15));

        Assert.True(
            result.ExitCode == 0,
            $"tar fixture creation failed (exit {result.ExitCode}): {result.StandardError}");
    }

    private static async Task AssertFixtureExitedAsync(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (IsProcessRunning(processId) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.False(IsProcessRunning(processId), $"Piper extractor process {processId} is still running.");
    }

    private static async Task<int> ReadFixturePidAsync(string directory)
    {
        var pidPath = Path.Combine(directory, "fixture.pid");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidPath))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(pidPath);
                    if (int.TryParse(text, out var processId))
                        return processId;
                }
                catch (IOException)
                {
                    // The fixture may have created the file but still hold its write handle.
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Piper extractor fixture did not publish its PID.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task AwaitFixtureCleanupAsync(
        Task extraction,
        CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        await Task.WhenAny(extraction, Task.Delay(TimeSpan.FromSeconds(3)));
        BoundedProcessWait.ObserveFault(extraction);
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

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("openclaw-piper-extract-").FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
