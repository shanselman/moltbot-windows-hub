using static OpenClaw.Shared.IO.WindowsPathSafety;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Identifies one versioned native component without prescribing its vendor,
/// download source, archive contents, or executable layout.
/// </summary>
internal sealed record LocalAiComponentIdentity(
    string Name,
    string Version,
    string RuntimeIdentifier);

internal sealed record LocalAiSetupPaths(
    string RootDirectory,
    string DownloadsDirectory,
    string EnginesDirectory,
    string StagingDirectory,
    string InstallDirectory,
    string LogsDirectory);

/// <summary>
/// Resolves setup-owned Local AI paths and guards every recursive-operation
/// target against traversal and reparse-point redirection.
/// </summary>
internal static class LocalAiPathPolicy
{
    internal const string RootDirectoryName = "LocalAI";
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    public static bool TryResolve(
        string localDataDirectory,
        LocalAiComponentIdentity identity,
        out LocalAiSetupPaths paths,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(identity);
        paths = null!;

        if (string.IsNullOrWhiteSpace(localDataDirectory))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        if (!IsSafeSegment(identity.Name) ||
            !IsSafeSegment(identity.Version) ||
            !IsSafeSegment(identity.RuntimeIdentifier))
        {
            error = "Local AI component identity contains an invalid path segment.";
            return false;
        }

        string localDataRoot;
        string root;
        string downloads;
        string engines;
        string staging;
        string installDirectory;
        string logs;
        try
        {
            localDataRoot = NormalizePath(localDataDirectory);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            downloads = NormalizePath(Path.Combine(root, "downloads"));
            engines = NormalizePath(Path.Combine(root, "engines"));
            staging = NormalizePath(Path.Combine(root, "staging"));
            installDirectory = NormalizePath(Path.Combine(
                engines,
                identity.Name,
                identity.Version,
                identity.RuntimeIdentifier));
            logs = NormalizePath(Path.Combine(root, "logs"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI data path: {ex.Message}";
            return false;
        }

        if (!PathEquals(Path.GetDirectoryName(root), localDataRoot))
        {
            error = $"Local AI root '{root}' must be an immediate child of '{localDataRoot}'.";
            return false;
        }

        foreach (var candidate in new[]
                 {
                     root,
                     downloads,
                     engines,
                     staging,
                     installDirectory,
                     logs,
                 })
        {
            if (!IsSameOrDescendant(candidate, root))
            {
                error = $"Local AI path '{candidate}' escaped the app-owned Local AI root.";
                return false;
            }

            if (!TryValidateExistingPathChain(localDataRoot, candidate, out error))
                return false;
        }

        paths = new LocalAiSetupPaths(
            root,
            downloads,
            engines,
            staging,
            installDirectory,
            logs);
        error = "";
        return true;
    }

    public static bool TryGetDownloadPath(
        LocalAiSetupPaths paths,
        string archiveFileName,
        out string downloadPath,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        downloadPath = "";

        if (!IsSafeSegment(archiveFileName))
        {
            error = "Local AI archive file name contains an invalid path segment.";
            return false;
        }

        try
        {
            downloadPath = NormalizePath(Path.Combine(paths.DownloadsDirectory, archiveFileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI download path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(downloadPath, paths.DownloadsDirectory) ||
            !IsStrictDescendant(downloadPath, paths.RootDirectory))
        {
            downloadPath = "";
            error = "Local AI download path escaped the app-owned Local AI root.";
            return false;
        }

        if (!TryValidateExistingPathChain(paths.RootDirectory, downloadPath, out error))
        {
            downloadPath = "";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryGetStagingDirectory(
        LocalAiSetupPaths paths,
        string runId,
        out string stagingDirectory,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(paths);
        stagingDirectory = "";
        if (string.IsNullOrWhiteSpace(runId) ||
            runId.Length is < 8 or > 64 ||
            !runId.All(char.IsAsciiHexDigit))
        {
            error = "Local AI staging run ID must contain 8 to 64 ASCII hexadecimal characters.";
            return false;
        }

        try
        {
            stagingDirectory = NormalizePath(Path.Combine(paths.StagingDirectory, runId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI staging path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(stagingDirectory, paths.StagingDirectory) ||
            !IsStrictDescendant(stagingDirectory, paths.RootDirectory))
        {
            stagingDirectory = "";
            error = "Local AI staging directory escaped the app-owned Local AI root.";
            return false;
        }

        if (!TryValidateExistingPathChain(paths.RootDirectory, stagingDirectory, out error))
        {
            stagingDirectory = "";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryValidateManagedDeleteTarget(
        string localDataDirectory,
        string candidatePath,
        out string deletePath,
        out string error)
    {
        deletePath = "";
        if (string.IsNullOrWhiteSpace(localDataDirectory))
        {
            error = "Local AI data directory is required.";
            return false;
        }

        string localDataRoot;
        string root;
        try
        {
            localDataRoot = NormalizePath(localDataDirectory);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            deletePath = NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI deletion path: {ex.Message}";
            return false;
        }

        if (!IsStrictDescendant(deletePath, root))
        {
            error = $"Refusing to delete Local AI path '{deletePath}'; it is not below the app-owned root '{root}'.";
            deletePath = "";
            return false;
        }

        if (!TryValidateExistingPathChain(localDataRoot, deletePath, out error))
        {
            deletePath = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deletes one app-owned Local AI tree without following filesystem links.
    /// The entire tree is validated before the first delete so a reparse point
    /// or inaccessible entry leaves the tree untouched.
    /// </summary>
    public static bool TryDeleteManagedTree(
        string localDataDirectory,
        string candidatePath,
        bool allowRoot,
        out string error)
    {
        error = "";
        string localDataRoot;
        string root;
        string deletePath;
        try
        {
            localDataRoot = NormalizePath(localDataDirectory);
            root = NormalizePath(Path.Combine(localDataRoot, RootDirectoryName));
            deletePath = NormalizePath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid Local AI deletion path: {ex.Message}";
            return false;
        }

        bool isRoot = PathEquals(deletePath, root);
        if ((!allowRoot || !isRoot) && !IsStrictDescendant(deletePath, root))
        {
            error = $"Refusing to delete Local AI path '{deletePath}'; it is not below the app-owned root '{root}'.";
            return false;
        }
        if (allowRoot && !isRoot && !IsStrictDescendant(deletePath, root))
        {
            error = $"Refusing to delete Local AI path '{deletePath}'; it is not the app-owned root or one of its descendants.";
            return false;
        }
        if (isRoot && !PathEquals(Path.GetDirectoryName(root), localDataRoot))
        {
            error = $"Refusing to delete Local AI root '{root}'; it is not an immediate child of '{localDataRoot}'.";
            return false;
        }
        if (!TryValidateExistingPathChain(localDataRoot, deletePath, out error))
            return false;

        try
        {
            if (!File.Exists(deletePath) && !Directory.Exists(deletePath))
                return true;

            FileAttributes rootAttributes = File.GetAttributes(deletePath);
            if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"Refusing to delete Local AI path '{deletePath}' because it is a reparse point.";
                return false;
            }
            if (!rootAttributes.HasFlag(FileAttributes.Directory))
            {
                File.Delete(deletePath);
                return true;
            }

            var files = new List<string>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(deletePath);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                directories.Add(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        error = $"Refusing to delete Local AI tree because '{entry}' is a reparse point.";
                        return false;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                        pending.Push(entry);
                    else
                        files.Add(entry);
                }
            }

            foreach (string file in files)
            {
                if (!TryValidateExistingPathChain(localDataRoot, file, out error))
                    return false;
                File.Delete(file);
            }
            for (int index = directories.Count - 1; index >= 0; index--)
            {
                string directory = directories[index];
                if (!TryValidateExistingPathChain(localDataRoot, directory, out error))
                    return false;
                Directory.Delete(directory, recursive: false);
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Could not safely delete Local AI path '{deletePath}': {ex.Message}";
            return false;
        }
    }

    public static bool TryResolveArchiveEntryDestination(
        string stagingDirectory,
        string entryName,
        out string destinationPath,
        out string error)
    {
        destinationPath = "";
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
        {
            error = "Local AI archive contains an empty or invalid entry name.";
            return false;
        }

        string root;
        string candidatePath;
        try
        {
            root = NormalizePath(stagingDirectory);
            candidatePath = NormalizePath(Path.Combine(
                root,
                entryName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Local AI archive entry has an invalid path: {ex.Message}";
            return false;
        }

        // Keep the untrusted candidate local until containment and reparse checks establish ownership.
        if (!candidatePath.StartsWith(EnsureTrailingDirectorySeparator(root), PathComparison))
        {
            error = $"Local AI archive entry '{entryName}' escapes its staging directory.";
            return false;
        }

        if (!TryValidateExistingPathChain(root, candidatePath, out error))
            return false;

        destinationPath = candidatePath;
        error = "";
        return true;
    }

    private static bool TryValidateExistingPathChain(
        string containmentRoot,
        string candidatePath,
        out string error)
    {
        if (!IsSameOrDescendant(candidatePath, containmentRoot))
        {
            error = $"Local AI path '{candidatePath}' is not contained within '{containmentRoot}'.";
            return false;
        }

        string? current = candidatePath;
        while (current is not null)
        {
            if (!TryGetExistingAttributes(current, out var exists, out var attributes, out error))
                return false;
            if (exists && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"Refusing to operate under '{current}' because it is a reparse point.";
                return false;
            }

            if (PathEquals(current, containmentRoot))
            {
                error = "";
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        error = $"Local AI path '{candidatePath}' is not contained within '{containmentRoot}'.";
        return false;
    }

    private static bool TryGetExistingAttributes(
        string path,
        out bool exists,
        out FileAttributes attributes,
        out string error)
    {
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            error = "";
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            exists = false;
            error = "";
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            exists = false;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            attributes = default;
            exists = false;
            error = $"Cannot verify Local AI path '{path}': {ex.Message}";
            return false;
        }
    }

}
