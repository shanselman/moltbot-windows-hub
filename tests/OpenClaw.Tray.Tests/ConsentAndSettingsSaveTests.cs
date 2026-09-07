using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ConsentAndSettingsSaveTests
{
    [Fact]
    public async Task Save_IsThreadSafe_ConcurrentCallsDoNotCorruptFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            settings.GatewayUrl = "ws://localhost:9999";

            // Fire many concurrent saves — none should throw or corrupt
            var tasks = Enumerable.Range(0, 20).Select(i =>
            {
                return Task.Run(() =>
                {
                    settings.ScreenRecordingConsentGiven = (i % 2 == 0);
                    settings.Save();
                });
            }).ToArray();

            await Task.WhenAll(tasks);

            // Verify file is still valid JSON and loadable
            var reloaded = new SettingsManager(tempDir);
            Assert.Equal("ws://localhost:9999", reloaded.GatewayUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Save_RaisesSavedEvent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            var eventRaised = false;
            settings.Saved += (s, e) => eventRaised = true;

            settings.ScreenRecordingConsentGiven = true;
            settings.Save();

            Assert.True(eventRaised);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConsentFlags_PersistAcrossReload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            Assert.False(settings.ScreenRecordingConsentGiven);
            Assert.False(settings.CameraRecordingConsentGiven);
            Assert.False(settings.LocationConsentGiven);

            settings.ScreenRecordingConsentGiven = true;
            settings.CameraRecordingConsentGiven = true;
            settings.LocationConsentGiven = true;
            settings.Save();

            var reloaded = new SettingsManager(tempDir);
            Assert.True(reloaded.ScreenRecordingConsentGiven);
            Assert.True(reloaded.CameraRecordingConsentGiven);
            Assert.True(reloaded.LocationConsentGiven);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConsentFlags_CanBeRevoked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            settings.ScreenRecordingConsentGiven = true;
            settings.Save();

            // Revoke
            settings.ScreenRecordingConsentGiven = false;
            settings.Save();

            var reloaded = new SettingsManager(tempDir);
            Assert.False(reloaded.ScreenRecordingConsentGiven);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CaptureConsentTimeoutMs_DefaultsToTwoMinutes_AndPersistsAcrossReload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsManager(tempDir);
            // Generous enough for a real interactive user to notice and answer
            // the prompt, but still bounded - see NodeService.EnsureCaptureConsentAsync.
            Assert.Equal(120_000, settings.CaptureConsentTimeoutMs);

            settings.CaptureConsentTimeoutMs = 3000;
            settings.Save();

            var reloaded = new SettingsManager(tempDir);
            Assert.Equal(3000, reloaded.CaptureConsentTimeoutMs);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
