using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Manages downloads and on-disk lifecycle for Piper TTS voices.
///
/// Each "voice" is a sherpa-onnx pre-packaged tarball that contains
/// everything needed for offline synthesis — the .onnx model, the
/// tokens.txt phoneme map, and the language-specific espeak-ng-data.
/// We use the sherpa-onnx repackaged distribution rather than the raw
/// HuggingFace Piper voices because the latter requires the user (or
/// us) to ship espeak-ng-data separately (~80 MB shared across voices).
///
/// Storage layout under the tray's data directory:
///   models/piper/&lt;voice-id&gt;/
///       &lt;voice-id&gt;.onnx
///       tokens.txt
///       espeak-ng-data/...
///
/// Each voice is ~50 MB compressed, ~80 MB extracted (with espeak data).
///
/// **Integrity:** downloaded tarballs are SHA-256-verified against the pinned
/// hash in <see cref="AvailableVoices"/> before extraction; a mismatch is a
/// hard failure and the partial file is deleted. See
/// <c>docs/AUDIO_MODEL_ASSETS.md</c>.
/// </summary>
public sealed class PiperVoiceManager
{
    private const string InstallMarkerSuffix = ".installing";
    private readonly string _voicesDirectory;
    private readonly IOpenClawLogger _logger;
    // Per-voice single-flight gate: prevents racing the same voice download
    // from two callers (e.g. UI and a programmatic caller). Static so two
    // PiperVoiceManager instances over the same data directory still
    // coalesce against the same in-flight task.
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Curated catalog of Piper voices we offer in the UI. Each entry is
    /// a sherpa-onnx pre-packaged tarball from the project's GitHub
    /// releases. To add a voice: pick its key from
    /// https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models,
    /// download the tarball, compute its SHA-256, and pin it below.
    /// Sizes shown in the UI are approximate compressed sizes.
    ///
    /// SECURITY — pinned SHA-256 hashes (lowercase hex) verified against
    /// the sherpa-onnx GitHub release on 2026-05-05. Downloads with a
    /// different hash are rejected and the partial tarball is deleted.
    /// Before every public release: re-verify each hash from an independent
    /// source and document provenance. See
    /// <c>docs/AUDIO_MODEL_ASSETS.md</c>.
    /// </summary>
    public static readonly PiperVoiceInfo[] AvailableVoices =
    [
        new("en_US-amy-low",     "English (US): Amy (low quality, fast)",   "en-US",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-amy-low.tar.bz2",
            "c70f5284a09a7fd4ed203b39b2ff51cac1432b422b852eb647b481dade3cf639"),
        new("en_US-libritts-high","English (US): LibriTTS (high quality)",  "en-US",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-libritts-high.tar.bz2",
            "d9d35056703fd38ed38e95c202a50f603fefdc8a92a7b6332c4f1a41616eac72"),
        new("en_GB-alan-low",    "English (GB): Alan (low quality, fast)",  "en-GB",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_GB-alan-low.tar.bz2",
            "1308e730b7a12c3b64b669d65daa0138fcb83b1a086edee92fa9fa68cb0290dd"),
        new("fr_FR-siwis-low",   "Français (FR): Siwis (low quality, fast)","fr-FR",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-fr_FR-siwis-low.tar.bz2",
            "3d69170c160c8375c4123901a72a3845222b39456d39ab74f5bbd7310952b5af"),
        new("de_DE-thorsten-low","Deutsch (DE): Thorsten (low quality)",    "de-DE",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-thorsten-low.tar.bz2",
            "41fab35910fdcec4696b031951d8fd6c262e594cf77b35e1068fadbeb5a091a6"),
        new("zh_CN-huayan-medium","中文 (CN): Huayan (medium quality)",      "zh-CN",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-zh_CN-huayan-medium.tar.bz2",
            "dbdfec42b91d9cee31cce9ff4b3e9c305eb6fbf60546d071f7e46273554cce6b"),
    ];

    public PiperVoiceManager(string dataDirectory, IOpenClawLogger logger)
    {
        _voicesDirectory = Path.Combine(dataDirectory, "models", "piper");
        _logger = logger;
        Directory.CreateDirectory(_voicesDirectory);
    }

    /// <summary>Root directory where this voice's files live (created lazily).</summary>
    public string GetVoiceDirectory(string voiceId)
    {
        var info = FindVoice(voiceId);
        return Path.Combine(_voicesDirectory, info.VoiceId);
    }

    /// <summary>Path to the .onnx model file for a downloaded voice.</summary>
    public string GetModelPath(string voiceId)
    {
        var dir = GetVoiceDirectory(voiceId);
        // sherpa-onnx tarballs put files at the root of the voice dir; the
        // model file is named after the voice id.
        return Path.Combine(dir, $"{voiceId}.onnx");
    }

    /// <summary>Path to tokens.txt (phoneme map).</summary>
    public string GetTokensPath(string voiceId) => Path.Combine(GetVoiceDirectory(voiceId), "tokens.txt");

    /// <summary>Path to the espeak-ng-data directory bundled with this voice.</summary>
    public string GetEspeakDataDir(string voiceId) => Path.Combine(GetVoiceDirectory(voiceId), "espeak-ng-data");

    /// <summary>True when all three files are present on disk.</summary>
    public bool IsVoiceDownloaded(string voiceId)
    {
        try
        {
            return !File.Exists(GetInstallMarkerPath(voiceId))
                && HasExpectedVoiceLayout(voiceId);
        }
        catch (Exception ex)
        {
            // FindVoice throws on unknown voiceId — treat as not-downloaded.
            _logger.Debug($"PiperVoiceManager.IsVoiceDownloaded('{voiceId}'): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Download and extract a Piper voice from the sherpa-onnx release.
    /// Reports progress as bytes downloaded / total bytes (extraction
    /// progress is not reported separately).
    /// Per-voice single-flight: concurrent calls for the same voice await
    /// the in-flight download instead of racing on the same temp tarball.
    /// </summary>
    public Task DownloadVoiceAsync(
        string voiceId,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = FindVoice(voiceId);
        if (IsVoiceDownloaded(info.VoiceId))
        {
            _logger.Info($"Piper voice '{info.VoiceId}' already downloaded");
            return Task.CompletedTask;
        }

        // Preflight: bail out before downloading 50-150 MB if the OS isn't
        // capable of extracting the .tar.bz2 we'd produce. tar.exe ships with
        // Windows 10 1803+; older systems would fail at the extract step
        // after a long, wasted download.
        EnsureExtractorAvailable();

        var key = info.VoiceId;
        return SingleFlightDownload.RunAsync(
            InFlightDownloads,
            key,
            token => DownloadVoiceCoreAsync(info, progress, token),
            cancellationToken);
    }

    private async Task DownloadVoiceCoreAsync(
        PiperVoiceInfo info,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        // SECURITY: refuse to install any voice that doesn't have a pinned
        // hash. See docs/AUDIO_MODEL_ASSETS.md.
        if (string.IsNullOrWhiteSpace(info.Sha256))
        {
            throw new InvalidOperationException(
                $"Piper voice '{info.VoiceId}' has no pinned SHA-256; refusing to download. " +
                "Add a verified hash to AvailableVoices before enabling this voice.");
        }

        var voiceDir = Path.Combine(_voicesDirectory, info.VoiceId);
        var installMarkerPath = GetInstallMarkerPath(info.VoiceId);
        var tarballPath = Path.Combine(voiceDir, $"{info.VoiceId}.tar.bz2.tmp");
        var hadExpectedLayout = HasExpectedVoiceLayout(info.VoiceId);
        var extractionVerified = false;
        _logger.Info($"Downloading Piper voice '{info.VoiceId}' from {info.DownloadUrl}");

        try
        {
            Directory.CreateDirectory(voiceDir);
            File.WriteAllText(installMarkerPath, string.Empty);
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);
            using var response = await httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var fileStream = new FileStream(tarballPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    downloaded += bytesRead;
                    progress?.Report((downloaded, totalBytes));
                }
            }

            // SECURITY: verify SHA-256 of the downloaded tarball BEFORE we
            // hand it to the extractor. tar reads file contents to disk; an
            // attacker-controlled tarball could plant arbitrary files (path
            // traversal aside, the .onnx model itself is loaded into the
            // process). Fail closed on mismatch — partial dir cleanup runs
            // in the catch block below.
            await VerifyHashAsync(tarballPath, info.Sha256, info.VoiceId, cancellationToken);

            _logger.Info($"Extracting Piper voice '{info.VoiceId}'");
            await ExtractTarBz2Async(tarballPath, voiceDir, cancellationToken).ConfigureAwait(false);

            // Verify the extraction produced the files we expect; if not,
            // tear the half-extracted dir down so a retry starts clean.
            if (!HasExpectedVoiceLayout(info.VoiceId))
            {
                throw new InvalidOperationException(
                    $"Extraction of Piper voice '{info.VoiceId}' did not produce the expected layout.");
            }

            extractionVerified = true;
            File.Delete(installMarkerPath);
            _logger.Info($"Piper voice '{info.VoiceId}' verified and ready at {voiceDir}");
        }
        catch
        {
            // Best-effort cleanup — leaves the user able to retry without
            // leftover partial files.
            try { if (File.Exists(tarballPath)) File.Delete(tarballPath); }
            catch (Exception cleanupEx) { _logger.Debug($"PiperVoiceManager: post-failure tarball cleanup failed: {cleanupEx.Message}"); }
            try
            {
                if (!extractionVerified && !hadExpectedLayout && Directory.Exists(voiceDir))
                    Directory.Delete(voiceDir, recursive: true);
                if (!Directory.Exists(voiceDir) && File.Exists(installMarkerPath))
                    File.Delete(installMarkerPath);
            }
            catch (Exception cleanupEx) { _logger.Debug($"PiperVoiceManager: post-failure voiceDir cleanup failed: {cleanupEx.Message}"); }
            throw;
        }
        finally
        {
            try { if (File.Exists(tarballPath)) File.Delete(tarballPath); }
            catch (Exception cleanupEx) { _logger.Debug($"PiperVoiceManager: finally tarball cleanup failed: {cleanupEx.Message}"); }
        }
    }

    private string GetInstallMarkerPath(string voiceId) =>
        $"{GetVoiceDirectory(voiceId)}{InstallMarkerSuffix}";

    private bool HasExpectedVoiceLayout(string voiceId) =>
        File.Exists(GetModelPath(voiceId))
        && File.Exists(GetTokensPath(voiceId))
        && Directory.Exists(GetEspeakDataDir(voiceId));

    /// <summary>
    /// Compute SHA-256 of <paramref name="filePath"/> and compare to
    /// <paramref name="expectedHex"/>. Throws on mismatch (caller is
    /// expected to delete the file). Does not echo the actual hash to
    /// avoid handing attackers a confirmation oracle.
    /// </summary>
    private static async Task VerifyHashAsync(string filePath, string expectedHex, string assetName, CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = await sha.ComputeHashAsync(stream, cancellationToken);
        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException(
                $"Piper voice '{assetName}' failed integrity check. The downloaded tarball does not match the pinned SHA-256.");
        }
    }

    /// <summary>Delete a downloaded voice directory.</summary>
    public bool DeleteVoice(string voiceId)
    {
        var info = FindVoice(voiceId);
        var dir = Path.Combine(_voicesDirectory, info.VoiceId);
        var markerPath = GetInstallMarkerPath(info.VoiceId);
        var deleted = false;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            deleted = true;
        }
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            deleted = true;
        }
        if (!deleted) return false;
        _logger.Info($"Deleted Piper voice '{info.VoiceId}'");
        return true;
    }

    /// <summary>Total disk usage of a downloaded voice, or 0 if not downloaded.</summary>
    public long GetVoiceSize(string voiceId)
    {
        var info = FindVoice(voiceId);
        var dir = Path.Combine(_voicesDirectory, info.VoiceId);
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch (Exception ex) { _logger.Debug($"PiperVoiceManager.GetVoiceSize: skip file '{f}': {ex.Message}"); }
        }
        return total;
    }

    /// <summary>
    /// Probe the bundled OS tar.exe used by <see cref="ExtractTarBz2Async"/>.
    /// Throws a clear error before any network I/O happens so users on
    /// downlevel Windows aren't left with a half-downloaded tarball.
    /// </summary>
    private static void EnsureExtractorAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("tar.exe not found on PATH.");
            }
            proc.WaitForExit(2000);
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception killEx)
                {
                    // Static context (no instance logger). Best-effort kill of unresponsive
                    // tar probe; the InvalidOperationException thrown below carries the user signal.
                    System.Diagnostics.Trace.WriteLine($"PiperVoiceManager: tar probe kill failed: {killEx.GetType().Name}: {killEx.Message}");
                }
                throw new InvalidOperationException("tar.exe didn't respond to --version.");
            }
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"tar.exe --version returned exit code {proc.ExitCode}.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Piper voices need bundled tar (Windows 10 1803+). " +
                "Your system doesn't have tar on PATH; please update Windows or install a tar utility.", ex);
        }
    }

    /// <summary>
    /// Extract a .tar.bz2 archive in-place. We use SharpCompress (already a
    /// transitive dependency via PiperSharp's ecosystem, but explicit here)
    /// so we don't need to shell out to tar.exe.
    /// </summary>
    internal static async Task ExtractTarBz2Async(
        string archivePath,
        string destinationDir,
        CancellationToken cancellationToken,
        string extractorFileName = "tar",
        TimeSpan? timeout = null)
    {
        // SharpCompress isn't a direct dep of OpenClaw.Shared today; we
        // intentionally use the BCL .tar reader on top of a bzip2 stream
        // from a small inline implementation. Keeping the dep surface small
        // matters in this assembly because everything here is also referenced
        // from OpenClaw.Cli.
        //
        // .NET 7+ ships System.Formats.Tar; bzip2 is not in the BCL, so we
        // bring it in via a thin wrapper. For now the simplest-correct path
        // is to call out to the OS-bundled `tar` (Win10 1803+ ships it),
        // which transparently handles bz2.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = extractorFileName,
            ArgumentList = { "-xjf", archivePath, "-C", destinationDir, "--strip-components=1" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start tar to extract Piper voice");

        BoundedProcessResult result;
        try
        {
            result = await BoundedProcessWait.WaitAsync(
                proc,
                timeout ?? BoundedProcessWait.DefaultTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                "tar extraction timed out while unpacking the Piper voice.", ex);
        }

        if (result.ExitCode != 0)
        {
            var output = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"tar extraction failed (exit {result.ExitCode}): {output}");
        }
    }

    private static PiperVoiceInfo FindVoice(string voiceId)
    {
        foreach (var v in AvailableVoices)
        {
            if (string.Equals(v.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        var available = string.Join(", ", AvailableVoicesIds());
        throw new ArgumentException($"Unknown Piper voice: '{voiceId}'. Available: {available}");
    }

    private static IEnumerable<string> AvailableVoicesIds()
    {
        foreach (var v in AvailableVoices) yield return v.VoiceId;
    }
}

/// <summary>Metadata about a Piper voice variant.</summary>
/// <param name="VoiceId">Short id, e.g. "en_US-amy-low".</param>
/// <param name="DisplayName">Human-readable label for UI.</param>
/// <param name="LanguageTag">BCP-47 tag.</param>
/// <param name="DownloadUrl">HTTPS URL of the .tar.bz2.</param>
/// <param name="Sha256">Pinned lowercase hex SHA-256 of the downloaded
/// tarball. MUST be set; downloads are refused when null. See the catalog
/// for the "verified on" date. Re-verify all shipped assets before every public
/// release; see <c>docs/AUDIO_MODEL_ASSETS.md</c>.</param>
public sealed record PiperVoiceInfo(
    string VoiceId,
    string DisplayName,
    string LanguageTag,
    string DownloadUrl,
    string? Sha256);
