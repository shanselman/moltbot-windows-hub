namespace OpenClaw.Shared.IO;

/// <summary>
/// Generic Windows path-safety primitives shared by every component that validates
/// app-managed or cache-managed filesystem paths (segment safety, device-name
/// rejection, and containment checks). Callers layer their own containment and
/// reparse-point policy on top of these primitives -- this class only judges
/// individual paths and segments in isolation.
/// </summary>
public static class WindowsPathSafety
{
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Fully resolves <paramref name="path"/> and trims any trailing separator.</summary>
    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Ordinal, case-insensitive path equality.</summary>
    public static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, PathComparison);

    /// <summary>True if <paramref name="candidate"/> equals <paramref name="root"/> or is nested under it.</summary>
    public static bool IsSameOrDescendant(string candidate, string root) =>
        PathEquals(candidate, root) || IsStrictDescendant(candidate, root);

    /// <summary>True if <paramref name="candidate"/> is strictly nested under <paramref name="root"/>.</summary>
    public static bool IsStrictDescendant(string candidate, string root) =>
        candidate.StartsWith(EnsureTrailingDirectorySeparator(root), PathComparison);

    public static string EnsureTrailingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>
    /// True if <paramref name="value"/> is safe to use as a single Windows path segment:
    /// non-empty, untrimmed-equal, not <c>.</c>/<c>..</c>, no trailing dot, not rooted, no
    /// invalid filename characters or separators, and not a reserved device name.
    /// </summary>
    public static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value is not "." and not ".." &&
        !value.EndsWith('.') &&
        !Path.IsPathRooted(value) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !value.Contains(Path.DirectorySeparatorChar) &&
        !value.Contains(Path.AltDirectorySeparatorChar) &&
        !IsWindowsDeviceName(value);

    /// <summary>True if <paramref name="segment"/> names a reserved Windows device (CON, PRN, COM1, LPT1, ...).</summary>
    public static bool IsWindowsDeviceName(string segment)
    {
        string baseName = segment.Split('.')[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               IsNumberedDevice(baseName, "COM") ||
               IsNumberedDevice(baseName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix) =>
        value.Length == 4 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[3] is >= '1' and <= '9';
}
