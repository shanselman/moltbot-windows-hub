using OpenClaw.Shared.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace OpenClaw.SetupEngine;

internal sealed record LocalAiPinnedArchive(
    string FileName,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256);

internal sealed record LocalAiVerifiedArchive(
    string FileName,
    long SizeBytes,
    string Sha256);

internal enum LocalAiArtifactInstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Promoting,
    Complete,
}

internal enum LocalAiArtifactProgressUnit
{
    None,
    Bytes,
    Entries,
}

internal sealed record LocalAiArtifactInstallProgress(
    LocalAiArtifactInstallPhase Phase,
    string? ArchiveFileName,
    int ArchiveNumber,
    int ArchiveCount,
    long Completed,
    long? Total,
    LocalAiArtifactProgressUnit Unit)
{
    public double? Fraction => Total is > 0
        ? Math.Clamp((double)Completed / Total.Value, 0, 1)
        : null;
}

/// <summary>
/// Describes the one directory a setup transaction owns after promotion.
/// Callers must revalidate this path with <see cref="LocalAiPathPolicy"/>
/// before recursively removing it during rollback.
/// </summary>
internal sealed record LocalAiArtifactRollbackMetadata(string CreatedDirectory);

internal sealed record LocalAiArtifactInstallResult(
    LocalAiComponentIdentity Component,
    string InstallDirectory,
    IReadOnlyList<LocalAiVerifiedArchive> VerifiedArchives,
    LocalAiArtifactRollbackMetadata Rollback);

internal sealed class LocalAiArtifactInstallException : Exception
{
    public LocalAiArtifactInstallException(string message)
        : base(message)
    {
    }

    public LocalAiArtifactInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Downloads one or more pinned native archives, verifies each byte stream,
/// safely extracts them into one disposable staging directory, then atomically
/// promotes the complete directory without replacing an existing install.
/// Component-specific release, executable, and version validation belong to
/// later policy layers.
/// </summary>
internal sealed class LocalAiArtifactInstaller
{
    private const int DownloadBufferSize = 128 * 1024;
    private const int DownloadProgressIntervalBytes = 4 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymbolicLink = 0xA000;

    private readonly HttpClient _httpClient;

    public LocalAiArtifactInstaller(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public event EventHandler<LocalAiArtifactInstallProgress>? ProgressChanged;

    public async Task<LocalAiArtifactInstallResult> InstallAsync(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        IReadOnlyList<LocalAiPinnedArchive> archives,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(archives);

        var pinnedArchives = archives.ToArray();
        ValidateArchiveSet(pinnedArchives);

        if (!LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out var paths,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        var resolvedArchives = ResolveArchivePaths(paths, pinnedArchives);
        var runId = Guid.NewGuid().ToString("N");
        if (!LocalAiPathPolicy.TryGetStagingDirectory(
                paths,
                runId,
                out var stagingDirectory,
                out pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        var stagingCreated = false;
        var promoted = false;
        var verifiedArchives = new List<LocalAiVerifiedArchive>(pinnedArchives.Length);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePromotionTargetDoesNotExist(paths.InstallDirectory);

            Directory.CreateDirectory(paths.DownloadsDirectory);
            Directory.CreateDirectory(paths.StagingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.InstallDirectory)!);

            RevalidatePaths(
                localDataDirectory,
                component,
                paths,
                resolvedArchives,
                stagingDirectory);

            RemoveStaleStagingEntries(localDataDirectory, paths.StagingDirectory);

            foreach (var resolved in resolvedArchives)
                RemoveStalePartial(localDataDirectory, resolved.PartialArchivePath);

            if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            {
                throw new LocalAiArtifactInstallException(
                    "The Local AI staging run directory already exists.");
            }

            Directory.CreateDirectory(stagingDirectory);
            stagingCreated = true;

            for (var index = 0; index < resolvedArchives.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = resolvedArchives[index];
                var archiveNumber = index + 1;
                var verifiedHash = await DownloadAndVerifyAsync(
                    resolved.Archive,
                    resolved.PartialArchivePath,
                    archiveNumber,
                    resolvedArchives.Length,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                verifiedArchives.Add(new LocalAiVerifiedArchive(
                    resolved.Archive.FileName,
                    resolved.Archive.SizeBytes,
                    verifiedHash));

                await ExtractArchiveAsync(
                    resolved.Archive,
                    resolved.PartialArchivePath,
                    stagingDirectory,
                    archiveNumber,
                    resolvedArchives.Length,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                TryDeleteManagedFile(localDataDirectory, resolved.PartialArchivePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RevalidatePaths(
                localDataDirectory,
                component,
                paths,
                resolvedArchives,
                stagingDirectory);
            EnsurePromotionTargetDoesNotExist(paths.InstallDirectory);

            Report(progress, new(
                LocalAiArtifactInstallPhase.Promoting,
                ArchiveFileName: null,
                ArchiveNumber: resolvedArchives.Length,
                ArchiveCount: resolvedArchives.Length,
                Completed: 0,
                Total: 1,
                LocalAiArtifactProgressUnit.None));

            Directory.Move(stagingDirectory, paths.InstallDirectory);
            promoted = true;

            var result = new LocalAiArtifactInstallResult(
                component,
                paths.InstallDirectory,
                verifiedArchives.AsReadOnly(),
                new LocalAiArtifactRollbackMetadata(paths.InstallDirectory));

            Report(progress, new(
                LocalAiArtifactInstallPhase.Complete,
                ArchiveFileName: null,
                ArchiveNumber: resolvedArchives.Length,
                ArchiveCount: resolvedArchives.Length,
                Completed: 1,
                Total: 1,
                LocalAiArtifactProgressUnit.None));
            return result;
        }
        finally
        {
            foreach (var resolved in resolvedArchives)
                TryDeleteManagedFile(localDataDirectory, resolved.PartialArchivePath);
            if (stagingCreated && !promoted)
                TryDeleteManagedDirectory(localDataDirectory, stagingDirectory);
        }
    }

    private async Task<string> DownloadAndVerifyAsync(
        LocalAiPinnedArchive archive,
        string partialArchivePath,
        int archiveNumber,
        int archiveCount,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithValidatedRedirectsAsync(
                archive.DownloadUri,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive '{archive.FileName}' download failed with HTTP status " +
                $"{(int)response.StatusCode} ({response.StatusCode}).");
        }

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != archive.SizeBytes)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive '{archive.FileName}' declared {contentLength} bytes; " +
                $"expected {archive.SizeBytes} bytes.");
        }

        Report(progress, new(
            LocalAiArtifactInstallPhase.Downloading,
            archive.FileName,
            archiveNumber,
            archiveCount,
            Completed: 0,
            Total: archive.SizeBytes,
            LocalAiArtifactProgressUnit.Bytes));

        long downloaded = 0;
        long lastReportedDownloadBytes = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content
                         .ReadAsStreamAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var destination = new FileStream(
            partialArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[DownloadBufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                downloaded = checked(downloaded + read);
                if (downloaded > archive.SizeBytes)
                {
                    throw new LocalAiArtifactInstallException(
                        $"Local AI archive '{archive.FileName}' exceeded its expected size of " +
                        $"{archive.SizeBytes} bytes.");
                }

                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                hasher.AppendData(buffer, 0, read);

                if (downloaded == archive.SizeBytes ||
                    downloaded - lastReportedDownloadBytes >= DownloadProgressIntervalBytes)
                {
                    Report(progress, new(
                        LocalAiArtifactInstallPhase.Downloading,
                        archive.FileName,
                        archiveNumber,
                        archiveCount,
                        downloaded,
                        archive.SizeBytes,
                        LocalAiArtifactProgressUnit.Bytes));
                    lastReportedDownloadBytes = downloaded;
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (downloaded != archive.SizeBytes)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive '{archive.FileName}' contained {downloaded} bytes; " +
                $"expected {archive.SizeBytes} bytes.");
        }

        Report(progress, new(
            LocalAiArtifactInstallPhase.Verifying,
            archive.FileName,
            archiveNumber,
            archiveCount,
            downloaded,
            archive.SizeBytes,
            LocalAiArtifactProgressUnit.Bytes));

        var actualHashBytes = hasher.GetHashAndReset();
        var expectedHashBytes = Convert.FromHexString(archive.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes))
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive '{archive.FileName}' failed SHA-256 verification.");
        }

        return Convert.ToHexStringLower(actualHashBytes);
    }

    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        ValidateDownloadUri(initialUri, initialRequest: true);
        Uri current = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            Uri observed = response.RequestMessage?.RequestUri ?? current;
            try
            {
                ValidateDownloadUri(observed, initialRequest: false);
                if (!IsRedirect(response.StatusCode))
                    return response;

                if (redirect == MaximumRedirects || response.Headers.Location is null)
                {
                    throw new LocalAiArtifactInstallException(
                        "The Local AI runtime download exceeded the redirect limit.");
                }

                Uri next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(observed, response.Headers.Location);
                ValidateDownloadUri(next, initialRequest: false);
                current = next;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }

        throw new LocalAiArtifactInstallException(
            "The Local AI runtime download exceeded the redirect limit.");
    }

    private static void ValidateDownloadUri(Uri uri, bool initialRequest)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new LocalAiArtifactInstallException(
                "The Local AI runtime download URI must be credential-free HTTPS.");
        }

        bool allowed = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            (!initialRequest &&
             (string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
        {
            throw new LocalAiArtifactInstallException(
                "The Local AI runtime download redirected to an untrusted host.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private async Task ExtractArchiveAsync(
        LocalAiPinnedArchive pinnedArchive,
        string archivePath,
        string stagingDirectory,
        int archiveNumber,
        int archiveCount,
        IProgress<LocalAiArtifactInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            var totalEntries = archive.Entries.Count;
            long completedEntries = 0;

            Report(progress, new(
                LocalAiArtifactInstallPhase.Extracting,
                pinnedArchive.FileName,
                archiveNumber,
                archiveCount,
                completedEntries,
                totalEntries,
                LocalAiArtifactProgressUnit.Entries));

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateArchiveEntryName(entry.FullName);
                var isDirectory = ValidateArchiveEntryType(entry);

                if (!LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                        stagingDirectory,
                        entry.FullName,
                        out var destinationPath,
                        out var pathError))
                {
                    throw new LocalAiArtifactInstallException(pathError);
                }

                if (isDirectory)
                {
                    if (File.Exists(destinationPath))
                    {
                        throw new LocalAiArtifactInstallException(
                            $"Local AI archive entry '{entry.FullName}' would replace an existing file.");
                    }

                    Directory.CreateDirectory(destinationPath);
                    RevalidateArchiveDestination(stagingDirectory, entry.FullName, destinationPath);
                }
                else
                {
                    if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                    {
                        throw new LocalAiArtifactInstallException(
                            $"Local AI archive entry '{entry.FullName}' would overwrite an existing path.");
                    }

                    var parentDirectory = Path.GetDirectoryName(destinationPath)
                        ?? throw new LocalAiArtifactInstallException(
                            "Local AI archive entry has no parent directory.");
                    Directory.CreateDirectory(parentDirectory);
                    RevalidateArchiveDestination(stagingDirectory, entry.FullName, destinationPath);

                    await using var source = entry.Open();
                    await using var destination = new FileStream(
                        destinationPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        DownloadBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source
                        .CopyToAsync(destination, DownloadBufferSize, cancellationToken)
                        .ConfigureAwait(false);
                }

                completedEntries++;
                Report(progress, new(
                    LocalAiArtifactInstallPhase.Extracting,
                    pinnedArchive.FileName,
                    archiveNumber,
                    archiveCount,
                    completedEntries,
                    totalEntries,
                    LocalAiArtifactProgressUnit.Entries));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive '{pinnedArchive.FileName}' is not a valid ZIP archive.",
                ex);
        }
    }

    private static bool ValidateArchiveEntryType(ZipArchiveEntry entry)
    {
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive entry '{entry.FullName}' is a reparse point.");
        }

        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var unixFileType = unixMode & UnixFileTypeMask;
        if (unixFileType == UnixSymbolicLink)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive entry '{entry.FullName}' is a symbolic link.");
        }

        if (unixFileType is not 0 and not UnixRegularFile and not UnixDirectory)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive entry '{entry.FullName}' has an unsupported file type.");
        }

        var hasDirectoryMarker = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
        var declaresDirectory = windowsAttributes.HasFlag(FileAttributes.Directory) ||
                                unixFileType == UnixDirectory;
        var declaresRegularFile = unixFileType == UnixRegularFile;

        if (declaresDirectory && !hasDirectoryMarker)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive entry '{entry.FullName}' has inconsistent directory metadata.");
        }

        if (declaresRegularFile && hasDirectoryMarker)
        {
            throw new LocalAiArtifactInstallException(
                $"Local AI archive entry '{entry.FullName}' has inconsistent file metadata.");
        }

        return hasDirectoryMarker;
    }

    private static void ValidateArchiveEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
            throw new LocalAiArtifactInstallException("Local AI archive contains an empty or invalid entry name.");

        var normalized = entryName.Replace('\\', '/');
        var segments = normalized.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var isTrailingDirectoryMarker = index == segments.Length - 1 && segment.Length == 0;
            if (isTrailingDirectoryMarker)
                continue;

            if (string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                !string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
                segment.EndsWith('.') ||
                segment.Contains(':') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                WindowsPathSafety.IsWindowsDeviceName(segment))
            {
                throw new LocalAiArtifactInstallException(
                    $"Local AI archive entry '{entryName}' contains an unsafe path segment.");
            }
        }
    }

    private static void RevalidateArchiveDestination(
        string stagingDirectory,
        string entryName,
        string expectedDestination)
    {
        if (!LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                stagingDirectory,
                entryName,
                out var currentDestination,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        if (!string.Equals(
                currentDestination,
                expectedDestination,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalAiArtifactInstallException(
                "The Local AI archive destination changed during extraction.");
        }
    }

    private static void ValidateArchiveSet(LocalAiPinnedArchive[] archives)
    {
        if (archives.Length == 0)
        {
            throw new ArgumentException(
                "At least one pinned Local AI archive is required.",
                nameof(archives));
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            if (archive is null)
                throw new ArgumentException("Pinned Local AI archives cannot contain null entries.", nameof(archives));
            ValidateArchive(archive);
            if (!fileNames.Add(archive.FileName))
            {
                throw new ArgumentException(
                    $"Pinned Local AI archive file name '{archive.FileName}' appears more than once.",
                    nameof(archives));
            }
        }
    }

    private static void ValidateArchive(LocalAiPinnedArchive archive)
    {
        if (archive.SizeBytes <= 0)
            throw new ArgumentException("Local AI archive expected size must be positive.", nameof(archive));
        if (!archive.DownloadUri.IsAbsoluteUri ||
            !string.Equals(
                archive.DownloadUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Local AI archive download URI must use HTTPS.", nameof(archive));
        }

        try
        {
            if (Convert.FromHexString(archive.Sha256).Length != 32 ||
                !string.Equals(
                    archive.Sha256,
                    archive.Sha256.ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Local AI archive SHA-256 must be 64 lowercase hexadecimal characters.",
                    nameof(archive));
            }
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "Local AI archive SHA-256 must be 64 lowercase hexadecimal characters.",
                nameof(archive),
                ex);
        }
    }

    private static ResolvedArchive[] ResolveArchivePaths(
        LocalAiSetupPaths paths,
        LocalAiPinnedArchive[] archives)
    {
        var resolved = new ResolvedArchive[archives.Length];
        for (var index = 0; index < archives.Length; index++)
        {
            var archive = archives[index];
            if (!LocalAiPathPolicy.TryGetDownloadPath(
                    paths,
                    archive.FileName,
                    out var archivePath,
                    out var pathError))
            {
                throw new LocalAiArtifactInstallException(pathError);
            }

            if (!LocalAiPathPolicy.TryGetDownloadPath(
                    paths,
                    archive.FileName + ".partial",
                    out var partialArchivePath,
                    out pathError))
            {
                throw new LocalAiArtifactInstallException(pathError);
            }

            resolved[index] = new ResolvedArchive(archive, archivePath, partialArchivePath);
        }

        return resolved;
    }

    private static void RevalidatePaths(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalAiSetupPaths expectedPaths,
        ResolvedArchive[] expectedArchives,
        string expectedStagingDirectory)
    {
        if (!LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out var currentPaths,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        if (currentPaths != expectedPaths)
            throw new LocalAiArtifactInstallException("The Local AI install path changed during installation.");

        foreach (var expected in expectedArchives)
        {
            if (!LocalAiPathPolicy.TryGetDownloadPath(
                    currentPaths,
                    expected.Archive.FileName,
                    out var currentArchivePath,
                    out pathError) ||
                !string.Equals(currentArchivePath, expected.ArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalAiArtifactInstallException(
                    pathError.Length == 0
                        ? "A Local AI archive path changed during installation."
                        : pathError);
            }

            if (!LocalAiPathPolicy.TryGetDownloadPath(
                    currentPaths,
                    expected.Archive.FileName + ".partial",
                    out var currentPartialPath,
                    out pathError) ||
                !string.Equals(currentPartialPath, expected.PartialArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalAiArtifactInstallException(
                    pathError.Length == 0
                        ? "A Local AI partial archive path changed during installation."
                        : pathError);
            }
        }

        var runId = Path.GetFileName(expectedStagingDirectory);
        if (!LocalAiPathPolicy.TryGetStagingDirectory(
                currentPaths,
                runId,
                out var currentStagingDirectory,
                out pathError) ||
            !string.Equals(
                currentStagingDirectory,
                expectedStagingDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalAiArtifactInstallException(
                pathError.Length == 0
                    ? "The Local AI staging path changed during installation."
                    : pathError);
        }
    }

    private static void EnsurePromotionTargetDoesNotExist(string installDirectory)
    {
        if (Directory.Exists(installDirectory) || File.Exists(installDirectory))
        {
            throw new LocalAiArtifactInstallException(
                $"Refusing to replace existing Local AI install path '{installDirectory}'.");
        }
    }

    private static void RemoveStalePartial(string localDataDirectory, string partialArchivePath)
    {
        if (Directory.Exists(partialArchivePath))
        {
            throw new LocalAiArtifactInstallException(
                "A Local AI partial download path is an existing directory.");
        }

        if (!File.Exists(partialArchivePath))
            return;
        if (!LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                localDataDirectory,
                partialArchivePath,
                out var deletePath,
                out var pathError))
        {
            throw new LocalAiArtifactInstallException(pathError);
        }

        File.Delete(deletePath);
    }

    private static void TryDeleteManagedFile(string localDataDirectory, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            if (LocalAiPathPolicy.TryValidateManagedDeleteTarget(
                    localDataDirectory,
                    path,
                    out var deletePath,
                    out _))
            {
                File.Delete(deletePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not clean Local AI partial download '{0}': {1}",
                path,
                ex.Message);
        }
    }

    private static void TryDeleteManagedDirectory(string localDataDirectory, string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if (LocalAiPathPolicy.TryDeleteManagedTree(
                    localDataDirectory,
                    path,
                    allowRoot: false,
                    out _))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Could not clean Local AI staging directory '{0}': {1}",
                path,
                ex.Message);
        }
    }

    private static void RemoveStaleStagingEntries(
        string localDataDirectory,
        string stagingDirectory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(stagingDirectory))
        {
            if (!LocalAiPathPolicy.TryDeleteManagedTree(
                    localDataDirectory,
                    entry,
                    allowRoot: false,
                    out string error))
            {
                throw new LocalAiArtifactInstallException(
                    $"A stale Local AI staging entry could not be removed safely: {error}");
            }
        }
    }

    private void Report(
        IProgress<LocalAiArtifactInstallProgress>? progress,
        LocalAiArtifactInstallProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Local AI progress observer failed: {0}",
                ex.Message);
        }

        try
        {
            ProgressChanged?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Local AI progress event observer failed: {0}",
                ex.Message);
        }
    }

    private sealed record ResolvedArchive(
        LocalAiPinnedArchive Archive,
        string ArchivePath,
        string PartialArchivePath);
}
