using Microsoft.Win32;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace OpenClawTray.Services;

/// <summary>
/// Manages Windows auto-start registration.
/// </summary>
public static class AutoStartManager
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static readonly string AppName = AppIdentity.AutoStartRegistryName;
    // Deliberately no legacy-autostart cleanup here. The scheduled task named
    // AppIdentity.StartupTaskName is created by installer.iss and removed by the Inno
    // uninstaller, so deleting it from the packaged app would silently disable a legacy
    // install the user has not agreed to replace. Detecting a legacy install, obtaining
    // consent, and removing its registrations belong to a migration flow that asks first.

    public static bool IsAutoStartEnabled()
    {
        if (PackageHelper.IsPackaged)
            return IsPackagedAutoStartEnabled();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            if (key?.GetValue(AppName) != null)
                return true;
        }
        catch
        {
        }

        return WindowsStartupTaskRegistration.Exists(AppIdentity.StartupTaskName);
    }

    public static void SetAutoStart(bool enable)
    {
        if (PackageHelper.IsPackaged)
        {
            SetPackagedAutoStartAsync(enable).GetAwaiter().GetResult();
            return;
        }

        SetUnpackagedAutoStart(enable);
    }

    public static Task SetAutoStartAsync(bool enable) =>
        PackageHelper.IsPackaged
            ? SetPackagedAutoStartAsync(enable)
            : Task.Run(() => SetUnpackagedAutoStart(enable));

    public static Task<bool> IsAutoStartEnabledAsync() =>
        PackageHelper.IsPackaged
            ? IsPackagedAutoStartEnabledAsync()
            : Task.Run(IsAutoStartEnabled);

    /// <summary>
    /// Reconciles the persisted auto-start preference against the real Windows startup
    /// state and returns the value the app should now report and store.
    /// </summary>
    /// <remarks>
    /// Packaged builds need this at startup. The manifest installs the StartupTask
    /// disabled, and Windows (not the app) owns the state afterwards, so a preserved
    /// <c>AutoStart=true</c> setting carried over from an unpackaged install would
    /// otherwise be displayed as enabled while nothing actually launches at logon. The
    /// user can also flip the task in Settings &gt; Apps &gt; Startup at any time.
    ///
    /// Windows is treated as the source of truth: the stored intent is applied when it
    /// can be, and whatever Windows reports afterwards is what gets persisted. Enabling
    /// is a request that Windows may refuse (DisabledByUser / DisabledByPolicy), and a
    /// refusal must not be retried silently forever, so it is surfaced as false.
    /// </remarks>
    public static async Task<bool> ReconcileAutoStartAsync(bool configured)
    {
        if (!PackageHelper.IsPackaged)
            return configured;

        try
        {
            var actual = await IsPackagedAutoStartEnabledAsync();
            if (actual == configured)
                return configured;

            if (!configured)
            {
                // Windows says enabled while the app setting says off, which happens when
                // the user enables the entry in Startup Apps. Report the truth instead of
                // fighting Windows; the in-app toggle still pushes changes the other way.
                Logger.Info("Auto-start is enabled in Windows; adopting that state.");
                return true;
            }

            await SetPackagedAutoStartAsync(true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Auto-start could not be reconciled, reporting disabled: {ex.Message}");
            return false;
        }
    }

    private static void SetUnpackagedAutoStart(bool enable)
    {
        try
        {
            if (enable)
            {
                var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (WindowsStartupTaskRegistration.Register(exePath, AppIdentity.StartupTaskName))
                {
                    DeleteRunKey();
                    Logger.Info("Auto-start enabled via scheduled task");
                    return;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, true);
                if (key == null)
                {
                    Logger.Warn($"Auto-start registry key unavailable: HKCU\\{RegistryKey}");
                    return;
                }

                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Info("Auto-start enabled");
            }
            else
            {
                DeleteRunKey();
                WindowsStartupTaskRegistration.Unregister(AppIdentity.StartupTaskName);
                Logger.Info("Auto-start disabled");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to set auto-start: {ex.Message}");
        }
    }

    private static bool IsPackagedAutoStartEnabled()
    {
        try
        {
            var startupTask = StartupTask.GetAsync(AppIdentity.PackageStartupTaskId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            return startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to query packaged auto-start: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> IsPackagedAutoStartEnabledAsync()
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(AppIdentity.PackageStartupTaskId);
            return startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to query packaged auto-start: {ex.Message}");
            return false;
        }
    }

    private static async Task SetPackagedAutoStartAsync(bool enable)
    {
        var startupTask = await StartupTask.GetAsync(AppIdentity.PackageStartupTaskId);
        if (!enable)
        {
            startupTask.Disable();
            Logger.Info("Packaged auto-start disabled");
            return;
        }

        if (startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            Logger.Info("Packaged auto-start already enabled");
            return;
        }

        var state = await startupTask.RequestEnableAsync();
        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            Logger.Info("Packaged auto-start enabled");
            return;
        }

        throw new InvalidOperationException(state switch
        {
            StartupTaskState.DisabledByUser =>
                "Windows startup is disabled by the user. Re-enable OpenClaw Companion in Settings > Apps > Startup.",
            StartupTaskState.DisabledByPolicy =>
                "Windows startup is disabled by policy.",
            _ => $"Windows did not enable the packaged startup task (state: {state})."
        });
    }

    private static void DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
            key?.DeleteValue(AppName, false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to remove auto-start registry key: {ex.Message}");
        }
    }
}
