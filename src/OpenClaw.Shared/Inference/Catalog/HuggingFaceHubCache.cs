using Microsoft.Win32.SafeHandles;
using OpenClaw.Shared.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Shared.Inference.Catalog;

/// <summary>
/// Resolves paths in the standard Hugging Face hub cache layout
/// (<c>&lt;cache root&gt;\models--&lt;org&gt;--&lt;repo&gt;\snapshots\&lt;revision&gt;\&lt;file&gt;</c>), the
/// same layout <c>huggingface_hub</c>, <c>huggingface-cli</c>, and llama.cpp's own
/// <c>--hf-repo</c> downloader use. Matching it lets a model already downloaded by any
/// of those tools be recognized and reused here, and vice versa.
/// </summary>
/// <remarks>
/// Files downloaded by OpenClaw are written directly at their snapshot paths, without
/// the content-addressed <c>blobs/</c> store or symlinks that <c>huggingface_hub</c>
/// creates when the host supports them. This sacrifices disk dedup between two
/// revisions of the same file, but needs no elevated privileges or Developer Mode on
/// Windows. Existing standard snapshot symlinks remain reusable when the opened file
/// handle resolves into the same repository's <c>blobs/</c> directory.
///
/// Unlike the app-owned Local AI directories under <c>LocalAiPathPolicy</c>, this cache
/// root is not exclusively owned by this app -- other tools may legitimately create
/// their own files, and even symlinks, inside sibling <c>models--*</c> folders.
/// Read validation and mutation validation are intentionally separate. Reads may
/// follow the one standard snapshot-to-blob symlink described above. Writes and
/// deletes reject every existing reparse point below the explicitly selected cache
/// root so a cache junction cannot redirect an app-owned mutation.
/// </remarks>
public static class HuggingFaceHubCache
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const string HubCacheEnvironmentVariable = "HF_HUB_CACHE";
    private const string LegacyHubCacheEnvironmentVariable = "HUGGINGFACE_HUB_CACHE";
    private const string HubHomeEnvironmentVariable = "HF_HOME";

    /// <summary>
    /// Resolves the Hugging Face hub cache root using the same precedence
    /// <c>huggingface_hub</c> uses: <c>HF_HUB_CACHE</c>, then the legacy
    /// <c>HUGGINGFACE_HUB_CACHE</c>, then <c>&lt;HF_HOME&gt;\hub</c>, then the default
    /// <c>%USERPROFILE%\.cache\huggingface\hub</c>.
    /// </summary>
    public static string ResolveCacheRoot() => ResolveCacheRoot(Environment.GetEnvironmentVariable);

    internal static string ResolveCacheRoot(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        string? explicitCache = NullIfWhiteSpace(getEnvironmentVariable(HubCacheEnvironmentVariable))
            ?? NullIfWhiteSpace(getEnvironmentVariable(LegacyHubCacheEnvironmentVariable));
        if (explicitCache is not null)
            return WindowsPathSafety.NormalizePath(explicitCache);

        string? home = NullIfWhiteSpace(getEnvironmentVariable(HubHomeEnvironmentVariable));
        if (home is not null)
            return WindowsPathSafety.NormalizePath(Path.Combine(home, "hub"));

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return WindowsPathSafety.NormalizePath(Path.Combine(userProfile, ".cache", "huggingface", "hub"));
    }

    /// <summary>
    /// Computes the standard hub-cache snapshot path for one pinned Hugging Face model
    /// file, plus a same-directory <c>.partial</c> sibling used while downloading.
    /// </summary>
    public static bool TryGetSnapshotPaths(
        string cacheRoot,
        string repositoryId,
        string revision,
        string fileName,
        out string modelPath,
        out string partialPath,
        out string error)
    {
        modelPath = "";
        partialPath = "";

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            error = "The Hugging Face hub cache root is required.";
            return false;
        }

        string[] repositorySegments = repositoryId?.Split('/') ?? [];
        if (repositorySegments.Length != 2 ||
            repositorySegments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            revision is null || revision.Length != 40 || !PinnedArtifactValidation.IsLowerHex(revision, 40) ||
            !WindowsPathSafety.IsSafeSegment(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "The Hugging Face model identity contains an invalid path segment.";
            return false;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            string repositoryFolder = $"models--{repositorySegments[0]}--{repositorySegments[1]}";
            string snapshotDirectory = WindowsPathSafety.NormalizePath(
                Path.Combine(normalizedRoot, repositoryFolder, "snapshots", revision));
            modelPath = WindowsPathSafety.NormalizePath(Path.Combine(snapshotDirectory, fileName));
            partialPath = WindowsPathSafety.NormalizePath(Path.Combine(snapshotDirectory, fileName + ".partial"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            modelPath = "";
            partialPath = "";
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(modelPath, normalizedRoot) ||
            !WindowsPathSafety.IsStrictDescendant(partialPath, normalizedRoot))
        {
            modelPath = "";
            partialPath = "";
            error = "The Hugging Face model path escaped the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Enumerates existing files that may already hold the pinned model content:
    /// the content-addressed blob named by the pinned SHA-256 digest plus any
    /// same-named snapshot entry across all revisions of the repository. Paths
    /// are returned unresolved (snapshot entries may be standard blob symlinks)
    /// and must pass <see cref="TryValidateSnapshotReadPath"/> plus the pinned
    /// size and digest check before reuse.
    /// </summary>
    public static bool TryGetReuseCandidates(
        string cacheRoot,
        string repositoryId,
        string fileName,
        Sha256Digest pinnedSha256,
        out IReadOnlyList<string> candidates,
        out string error)
    {
        candidates = [];
        error = "";

        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            error = "The Hugging Face hub cache root is required.";
            return false;
        }

        string[] repositorySegments = repositoryId?.Split('/') ?? [];
        if (repositorySegments.Length != 2 ||
            repositorySegments.Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            !WindowsPathSafety.IsSafeSegment(fileName) ||
            !string.Equals(Path.GetExtension(fileName), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "The Hugging Face model identity contains an invalid path segment.";
            return false;
        }

        string normalizedRoot;
        string repositoryFolder;
        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            repositoryFolder = WindowsPathSafety.NormalizePath(Path.Combine(
                normalizedRoot,
                $"models--{repositorySegments[0]}--{repositorySegments[1]}"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        string pinnedDigest = pinnedSha256.Value;
        var found = new List<string>();
        try
        {
            string blobsDirectory = Path.Combine(repositoryFolder, "blobs");
            if (Directory.Exists(blobsDirectory))
            {
                string blobPath = Path.Combine(blobsDirectory, pinnedDigest);
                if (File.Exists(blobPath) &&
                    WindowsPathSafety.IsStrictDescendant(blobPath, normalizedRoot))
                {
                    found.Add(WindowsPathSafety.NormalizePath(blobPath));
                }
            }

            string snapshotsDirectory = Path.Combine(repositoryFolder, "snapshots");
            if (Directory.Exists(snapshotsDirectory))
            {
                foreach (string revision in Directory.EnumerateDirectories(snapshotsDirectory))
                {
                    string candidate = Path.Combine(revision, fileName);
                    if (File.Exists(candidate) &&
                        WindowsPathSafety.IsStrictDescendant(candidate, normalizedRoot))
                    {
                        found.Add(WindowsPathSafety.NormalizePath(candidate));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A unreadable sibling revision must not block reuse of another candidate.
        }

        candidates = found;
        return true;
    }

    /// <summary>
    /// Validates a persisted or reusable snapshot path for reading. A regular file must
    /// resolve beneath the selected cache root. A symbolic-link snapshot is accepted
    /// only when its opened file handle resolves beneath the same repository's
    /// <c>blobs</c> directory. Other reparse-point targets and reparse-point ancestors
    /// below the configured cache root are rejected.
    /// </summary>
    public static bool TryValidateSnapshotReadPath(
        string cacheRoot,
        string candidatePath,
        out string validatedPath,
        out string error)
    {
        if (!TryNormalizeContainedPath(
                cacheRoot,
                candidatePath,
                out string normalizedRoot,
                out string normalizedPath,
                out error))
        {
            validatedPath = "";
            return false;
        }

        if (!File.Exists(normalizedPath))
            return TryValidateManagedPath(normalizedRoot, normalizedPath, out validatedPath, out error);

        if (!TryOpenSnapshotReadPath(
                normalizedRoot,
                normalizedPath,
                out FileStream? stream,
                out validatedPath,
                out error))
        {
            return false;
        }

        stream!.Dispose();
        return true;
    }

    /// <summary>
    /// Validates a fully qualified path before OpenClaw writes or deletes it. The path
    /// must be contained within <paramref name="cacheRoot"/>, and every existing path
    /// component below that explicitly selected root must be a non-reparse entry.
    /// </summary>
    public static bool TryValidateManagedPath(
        string cacheRoot,
        string candidatePath,
        out string validatedPath,
        out string error)
    {
        if (!TryNormalizeContainedPath(
                cacheRoot,
                candidatePath,
                out string normalizedRoot,
                out string normalizedPath,
                out error))
        {
            validatedPath = "";
            return false;
        }

        if (!TryRejectReparsePointDescendants(normalizedRoot, normalizedPath, includeTarget: true, out error))
        {
            validatedPath = "";
            return false;
        }

        validatedPath = normalizedPath;
        error = "";
        return true;
    }

    internal static bool TryOpenSnapshotReadPath(
        string cacheRoot,
        string candidatePath,
        out FileStream? stream,
        out string validatedPath,
        out string error)
    {
        stream = null;
        validatedPath = "";
        if (!TryNormalizeContainedPath(
                cacheRoot,
                candidatePath,
                out string normalizedRoot,
                out string normalizedPath,
                out error) ||
            !TryRejectReparsePointDescendants(normalizedRoot, normalizedPath, includeTarget: false, out error))
        {
            return false;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(normalizedPath);
            stream = new FileStream(
                normalizedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            string finalPath = GetFinalPathFromHandle(stream.SafeFileHandle, normalizedPath);
            string finalRoot = GetFinalDirectoryPath(normalizedRoot);
            if (!WindowsPathSafety.IsStrictDescendant(finalPath, finalRoot))
            {
                error = $"Hugging Face model path '{normalizedPath}' resolves outside the hub cache root.";
                stream.Dispose();
                stream = null;
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                string? linkTarget = new FileInfo(normalizedPath).LinkTarget;
                if (linkTarget is null)
                {
                    error = $"Refusing to read '{normalizedPath}' because it is an unsupported reparse point.";
                    stream.Dispose();
                    stream = null;
                    return false;
                }

                if (!TryGetRepositoryBlobsDirectory(
                        normalizedRoot,
                        normalizedPath,
                        out string blobsDirectory,
                        out error) ||
                    !Directory.Exists(blobsDirectory) ||
                    !TryRejectReparsePointDescendants(
                        normalizedRoot,
                        blobsDirectory,
                        includeTarget: true,
                        out error))
                {
                    stream.Dispose();
                    stream = null;
                    if (string.IsNullOrWhiteSpace(error))
                        error = "The Hugging Face snapshot link has no repository blobs directory.";
                    return false;
                }

                string finalBlobsDirectory = GetFinalDirectoryPath(blobsDirectory);
                if (!WindowsPathSafety.IsStrictDescendant(finalBlobsDirectory, finalRoot) ||
                    !WindowsPathSafety.IsStrictDescendant(finalPath, finalBlobsDirectory))
                {
                    error = $"Hugging Face snapshot link '{normalizedPath}' does not resolve into its repository blobs directory.";
                    stream.Dispose();
                    stream = null;
                    return false;
                }
            }

            validatedPath = normalizedPath;
            error = "";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            stream?.Dispose();
            stream = null;
            error = $"Cannot safely open Hugging Face hub cache path '{normalizedPath}': {ex.Message}";
            return false;
        }
    }

    private static bool TryNormalizeContainedPath(
        string cacheRoot,
        string candidatePath,
        out string normalizedRoot,
        out string normalizedPath,
        out string error)
    {
        normalizedRoot = "";
        normalizedPath = "";
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            error = "The Hugging Face hub cache root is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath))
        {
            error = "The Hugging Face model path must be a fully qualified path.";
            return false;
        }

        try
        {
            normalizedRoot = WindowsPathSafety.NormalizePath(cacheRoot);
            normalizedPath = WindowsPathSafety.NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Hugging Face hub cache path: {ex.Message}";
            return false;
        }

        if (!WindowsPathSafety.IsStrictDescendant(normalizedPath, normalizedRoot))
        {
            error = $"Hugging Face model path '{normalizedPath}' is not contained within the hub cache root.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryRejectReparsePointDescendants(
        string normalizedRoot,
        string normalizedPath,
        bool includeTarget,
        out string error)
    {
        string relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int count = includeTarget ? segments.Length : Math.Max(0, segments.Length - 1);
        string current = normalizedRoot;
        for (int index = 0; index < count; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!TryRejectReparsePoint(current, out error))
                return false;
        }

        error = "";
        return true;
    }

    private static bool TryRejectReparsePoint(string path, out string error)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                error = $"Refusing to operate on '{path}' because it is a reparse point.";
                return false;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Cannot verify Hugging Face hub cache path '{path}': {ex.Message}";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Creates a hard link so the pinned snapshot path points at already-present
    /// verified content (a hub blob or another revision's snapshot). Hard links
    /// need no extra privilege, keep the content at its existing location, and
    /// deleting the link later never removes the underlying content.
    /// </summary>
    public static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return CreateHardLinkW(linkPath, targetPath, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static bool TryGetRepositoryBlobsDirectory(
        string normalizedRoot,
        string normalizedPath,
        out string blobsDirectory,
        out string error)
    {
        blobsDirectory = "";
        string[] segments = Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 ||
            !segments[0].StartsWith("models--", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "snapshots", StringComparison.Ordinal) ||
            !PinnedArtifactValidation.IsLowerHex(segments[2], 40) ||
            segments.Skip(3).Any(segment => !WindowsPathSafety.IsSafeSegment(segment)) ||
            !string.Equals(Path.GetExtension(segments[^1]), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            error = "The Hugging Face symbolic link is not a pinned GGUF snapshot path.";
            return false;
        }

        blobsDirectory = WindowsPathSafety.NormalizePath(
            Path.Combine(normalizedRoot, segments[0], "blobs"));
        error = "";
        return true;
    }

    private static string GetFinalDirectoryPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? resolved = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return WindowsPathSafety.NormalizePath(resolved?.FullName ?? path);
        }

        using SafeFileHandle handle = CreateFileW(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Cannot open directory '{path}' (Win32 error {Marshal.GetLastWin32Error()}).");
        return GetFinalPathFromHandle(handle, path);
    }

    private static string GetFinalPathFromHandle(SafeFileHandle handle, string fallbackPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo? resolved = new FileInfo(fallbackPath).ResolveLinkTarget(returnFinalTarget: true);
            return WindowsPathSafety.NormalizePath(resolved?.FullName ?? fallbackPath);
        }

        int capacity = 512;
        while (capacity <= 32_768)
        {
            var builder = new StringBuilder(capacity);
            uint length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0)
                throw new IOException($"Cannot resolve a Hugging Face cache handle (Win32 error {Marshal.GetLastWin32Error()}).");
            if (length < builder.Capacity)
                return WindowsPathSafety.NormalizePath(NormalizeFinalPath(builder.ToString()));
            capacity = checked((int)length + 1);
        }

        throw new IOException("A resolved Hugging Face cache path exceeded the supported length.");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[extendedUncPrefix.Length..];
        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
