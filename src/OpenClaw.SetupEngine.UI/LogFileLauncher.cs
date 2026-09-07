using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI;

internal static class LogFileLauncher
{
    // When running under MSIX with package identity, writes to
    // %APPDATA% / %LOCALAPPDATA% are silently redirected to a per-package
    // container under %LOCALAPPDATA%\Packages\<PFN>\LocalCache. The app sees
    // and uses the unredirected path, but external tools like Explorer.exe
    // do not honor that redirect, so /select with the unredirected path
    // fails and Explorer opens the default folder. Translate the path so
    // both the displayed text and the Explorer launch reference the real
    // on-disk location.
    public static string ResolveRealPath(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return logPath;

        var pfn = TryGetPackageFamilyName();
        if (pfn == null)
            return logPath;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageRoot = Path.Combine(localAppData, "Packages", pfn);

        // Already inside the package container — nothing to translate.
        if (logPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            return logPath;

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (TryStrip(logPath, roaming, out var roamingRest))
            return Path.Combine(packageRoot, "LocalCache", "Roaming", roamingRest);

        if (TryStrip(logPath, localAppData, out var localRest))
            return Path.Combine(packageRoot, "LocalCache", "Local", localRest);

        return logPath;
    }

    public static void RevealInExplorer(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        var realPath = ResolveRealPath(logPath);

        try
        {
            if (File.Exists(realPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{realPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            // Callers may pass a directory (the Local AI log folder) rather than a single file;
            // opening its parent would show the wrong place.
            if (Directory.Exists(realPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{realPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var dir = Path.GetDirectoryName(realPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            // best effort — the link is informational; if shell open fails
            // the user can navigate to the log path manually.
            System.Diagnostics.Trace.WriteLine($"LogFileLauncher.OpenContainingFolder: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryStrip(string path, string prefix, out string rest)
    {
        rest = string.Empty;
        if (string.IsNullOrEmpty(prefix))
            return false;

        var sep = Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix + sep, StringComparison.OrdinalIgnoreCase))
            return false;

        rest = path.Substring(prefix.Length + 1);
        return true;
    }

    private static string? TryGetPackageFamilyName()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FamilyName;
        }
        catch
        {
            // Unpackaged process — no virtualization in play.
            return null;
        }
    }
}
