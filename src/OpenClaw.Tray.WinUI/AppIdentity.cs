namespace OpenClawTray;

/// <summary>
/// Compile-time app identity constants that vary between Dev and Release builds,
/// enabling side-by-side installation of both variants (similar to WinUI Gallery).
/// </summary>
internal static class AppIdentity
{
#if DEV_BUILD
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion (Dev)";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray (Dev)";

    /// <summary>
    /// Win32 AppUserModelID used for notifications and shell grouping. This applies to
    /// unpackaged (Inno Setup) installs only; packaged builds take their AUMID from the
    /// MSIX manifest instead. It must keep matching installer.iss MyAppAumid, and it is
    /// deliberately independent of Identity/@Name in Package.appxmanifest -- resyncing it
    /// to the MSIX identity would orphan the AUMID already written into existing users'
    /// Start menu shortcuts and break their notifications.
    /// </summary>
    public const string AppUserModelId = "OpenClaw.Companion.Dev";

    /// <summary>Windows Registry auto-start value name (must differ so both can auto-start).</summary>
    public const string AutoStartRegistryName = "OpenClawTray-Dev";

    /// <summary>Windows scheduled task name (must differ so both can auto-start).</summary>
    public const string StartupTaskName = "OpenClaw Companion (Dev)";

    /// <summary>MSIX manifest startup task identifier.</summary>
    public const string PackageStartupTaskId = "OpenClawStartup";

    /// <summary>Leaf directory for local and roaming app-owned data.</summary>
    public const string DataDirectoryName = "OpenClawTray-Dev";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray-Dev";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw-dev";

    /// <summary>App-owned WSL distro used by embedded setup.</summary>
    public const string SetupDistroName = "OpenClawGateway-Dev";

    /// <summary>Loopback gateway port used by embedded setup.</summary>
    public const int SetupGatewayPort = 18790;

    /// <summary>Explicit IPv4 loopback gateway URL used by embedded setup and post-setup startup.</summary>
    public const string SetupGatewayUrl = "ws://127.0.0.1:18790";

    /// <summary>Whether this is a development build.</summary>
    public static bool IsDev => true;
#else
    /// <summary>Human-visible app name shown in tray tooltips, window titles, and notifications.</summary>
    public const string DisplayName = "OpenClaw Companion";

    /// <summary>Short name used in tray tooltip prefix.</summary>
    public const string TrayName = "OpenClaw Tray";

    /// <summary>
    /// Win32 AppUserModelID used for notifications and shell grouping. This applies to
    /// unpackaged (Inno Setup) installs only; packaged builds take their AUMID from the
    /// MSIX manifest instead. It must keep matching installer.iss MyAppAumid, and it is
    /// deliberately independent of Identity/@Name in Package.appxmanifest -- resyncing it
    /// to the MSIX identity would orphan the AUMID already written into existing users'
    /// Start menu shortcuts and break their notifications.
    /// </summary>
    public const string AppUserModelId = "OpenClaw.Companion";

    /// <summary>Windows Registry auto-start value name.</summary>
    public const string AutoStartRegistryName = "OpenClawTray";

    /// <summary>Windows scheduled task name.</summary>
    public const string StartupTaskName = "OpenClaw Companion";

    /// <summary>MSIX manifest startup task identifier.</summary>
    public const string PackageStartupTaskId = "OpenClawStartup";

    /// <summary>Leaf directory for local and roaming app-owned data.</summary>
    public const string DataDirectoryName = "OpenClawTray";

    /// <summary>Single-instance mutex base name.</summary>
    public const string MutexBaseName = "OpenClawTray";

    /// <summary>Protocol scheme for deep links.</summary>
    public const string ProtocolScheme = "openclaw";

    /// <summary>App-owned WSL distro used by embedded setup.</summary>
    public const string SetupDistroName = "OpenClawGateway";

    /// <summary>Loopback gateway port used by embedded setup.</summary>
    public const int SetupGatewayPort = 18789;

    /// <summary>Explicit IPv4 loopback gateway URL used by embedded setup and post-setup startup.</summary>
    public const string SetupGatewayUrl = "ws://127.0.0.1:18789";

    /// <summary>Whether this is a development build.</summary>
    public static bool IsDev => false;
#endif

    public static string ResolveLocalDataDirectory()
        => Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DataDirectoryName);

    /// <summary>
    /// Resolves setup-owned local state while preserving SetupEngine's dedicated
    /// local-data override contract.
    /// </summary>
    public static string ResolveSetupLocalDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR") is { Length: > 0 } localAppDataRoot)
            return Path.Combine(localAppDataRoot, DataDirectoryName);

        if (Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR") is { Length: > 0 } localDataDir)
            return localDataDir;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DataDirectoryName);
    }

    public static string ResolveRoamingDataDirectory()
        => Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                DataDirectoryName);

    public static string DecorateWindowTitle(string title)
        => IsDev ? $"{title} (Dev)" : title;
}
