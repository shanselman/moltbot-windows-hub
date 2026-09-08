using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Dialogs;
using OpenClawTray.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Updatum;

namespace OpenClawTray.Services;

/// <summary>
/// Coordinates update checks, user prompts, download, and installation.
/// All public methods must be called from the UI thread.
/// </summary>
internal sealed class UpdateCoordinator(
    UpdatumManager updater,
    AppState appState,
    SettingsManager? settings,
    Func<XamlRoot?> getXamlRoot,
    Action refreshStatus,
    Action exit,
    IUpdateCheckBoundary? updateCheckBoundary = null)
{
    private readonly SettingsManager? _settings = settings;
    private readonly IUpdateCheckBoundary _updateCheckBoundary =
        updateCheckBoundary ?? new UpdatumUpdateCheckBoundary(updater);

    // Cross-path concurrency for update checks, split into two phases:
    //  - _updateCheckGate: held only during the metadata/network check.
    //    Short timeout so contended callers don't block on user thinking.
    //  - _updateInstallInProgress: Interlocked flag covering the user-facing
    //    UpdateDialog + download + install. Prevents two parallel installs
    //    without holding a lock across user interaction.
    private readonly SemaphoreSlim _updateCheckGate = new(1, 1);
#if !DEBUG
    private int _updateInstallInProgress;
#endif
    private int _manualUpdateCheckInFlight;

    public static UpdateCommandCenterInfo BuildInitialInfo() => new()
    {
        Status = "Not checked",
        CurrentVersion = AppVersionInfo.Version
    };

    public async Task<bool> CheckForUpdatesAsync(bool userInitiated = false)
    {
        // === Stage 1: metadata check (gate-protected) ===
        if (!await _updateCheckGate.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Logger.Warn("Update check gate timed out: another check is in progress");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "another update check is already in progress; try again in a moment"
            };
            return true; // Don't block launch
        }

        if (PackageHelper.IsPackaged)
        {
            // A Store-installed package is updated by Windows, not by Updatum.
            // Self-updating from GitHub releases would bypass the Store, and the
            // packaged app must not claim update ownership from a legacy install.
            Logger.Info("Skipping update check in packaged build; updates are managed by the Microsoft Store");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "managed by the Microsoft Store"
            };
            _updateCheckGate.Release();
            return true;
        }

        if (AppIdentity.IsDev)
        {
            Logger.Info("Skipping release-channel update check in development build");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "development build"
            };
            _updateCheckGate.Release();
            return true;
        }

#if DEBUG
        try
        {
            Logger.Info("Skipping update check in debug build");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Skipped",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "debug build"
            };
            return true;
        }
        finally
        {
            _updateCheckGate.Release();
        }
#else
        string releaseTag;
        string changelog;
        try
        {
            Logger.Info("Checking for updates...");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Checking",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow
            };
            var checkOutcome = await UpdateCheckPipeline.CheckAsync(
                _updateCheckBoundary,
                AppVersionInfo.Version);
            var updateFound = checkOutcome.UpdateFound;
            if (checkOutcome.ActivatedFallbackTag is not null)
            {
                Logger.Info(
                    $"Using OpenClaw stable correction ordering for update {checkOutcome.ActivatedFallbackTag}");
            }

            if (!updateFound)
            {
                Logger.Info("No updates available");
                appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Current",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "no updates available"
                };
                return true;
            }

            var release = updater.LatestRelease!;
            if (string.IsNullOrEmpty(release.TagName))
            {
                // Defensive: AppUpdater says an update is available but the
                // release has no tag. Don't silently claim "up to date" —
                // surface as Failed so the user sees something is off.
                Logger.Warn("Update reported available but release has no TagName");
                appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Failed",
                    CurrentVersion = AppVersionInfo.Version,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "update metadata incomplete (missing version tag)"
                };
                return true;
            }

            releaseTag = release.TagName;
            changelog = updater.GetChangelog(true) ?? "No release notes available.";
            Logger.Info($"Update available: {releaseTag}");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Available",
                CurrentVersion = AppVersionInfo.Version,
                LatestVersion = releaseTag,
                CheckedAt = DateTime.UtcNow,
                Detail = "prompted"
            };

            if (!string.IsNullOrWhiteSpace(_settings?.SkippedUpdateTag) &&
                string.Equals(_settings.SkippedUpdateTag, releaseTag, StringComparison.OrdinalIgnoreCase) &&
                !userInitiated)
            {
                Logger.Info($"Skipping update prompt for remembered version {releaseTag}");
                // Replace the whole object rather than mutating Detail in place:
                // AppState.UpdateInfo only fires PropertyChanged on assignment, not on mutation.
                appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Available",
                    CurrentVersion = AppVersionInfo.Version,
                    LatestVersion = releaseTag,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "skipped by user"
                };
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Update check cancelled");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "update check cancelled"
            };
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = ex.Message
            };
            return true;
        }
        finally
        {
            // Release the gate BEFORE user interaction & download/install.
            // Holding it across these long phases would silently time-out
            // any concurrent manual click.
            _updateCheckGate.Release();
        }

        // === Stage 2: user-interactive prompt + download/install ===
        // Gate is released. Use Interlocked flag so concurrent callers can't
        // start a second parallel install while we're prompting/downloading.
        if (Interlocked.CompareExchange(ref _updateInstallInProgress, 1, 0) != 0)
        {
            Logger.Info("Update prompt/install already in progress; skipping");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = "an update is already being downloaded or installed"
            };
            return true;
        }

        try
        {
            var dialog = new UpdateDialog(releaseTag, changelog);
            UpdateDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (COMException ex)
            {
                // Visual tree torn down mid-await (e.g. window closed).
                // Treat as "remind me later" rather than tainting Status with
                // "Failed" — the network check itself succeeded.
                Logger.Warn($"[Update] Prompt dialog dismissed before completion: 0x{ex.HResult:X8}");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                // Another ContentDialog is already open on this XamlRoot.
                Logger.Warn($"[Update] Prompt dialog could not be shown: {ex.Message}");
                return true;
            }

            if (result == UpdateDialogResult.Download)
            {
                // Assign a fresh object rather than mutating .Detail in place:
                // a concurrent loser of the install-flag CAS may have just
                // overwritten appState.UpdateInfo with a "Failed" object,
                // and mutating its Detail would leave Status="Failed" with
                // our "download requested" detail — briefly inconsistent.
                appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Available",
                    CurrentVersion = AppVersionInfo.Version,
                    LatestVersion = releaseTag,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "download requested"
                };
                if (_settings != null)
                {
                    _settings.SkippedUpdateTag = string.Empty;
                    _settings.Save();
                }
                var installed = await DownloadAndInstallUpdateAsync();
                if (!installed)
                {
                    appState.UpdateInfo = new UpdateCommandCenterInfo
                    {
                        Status = "Failed",
                        CurrentVersion = AppVersionInfo.Version,
                        CheckedAt = DateTime.UtcNow,
                        Detail = "download or install failed"
                    };
                }
                return !installed; // Don't launch if update succeeded
            }

            if (result == UpdateDialogResult.Skip && _settings != null)
            {
                _settings.SkippedUpdateTag = releaseTag;
                _settings.Save();
                appState.UpdateInfo = new UpdateCommandCenterInfo
                {
                    Status = "Available",
                    CurrentVersion = AppVersionInfo.Version,
                    LatestVersion = releaseTag,
                    CheckedAt = DateTime.UtcNow,
                    Detail = "skipped by user"
                };
            }
            else if (userInitiated && _settings != null
                && string.Equals(_settings.SkippedUpdateTag, releaseTag,
                                 StringComparison.OrdinalIgnoreCase))
            {
                // User explicitly bypassed the remembered skip for THIS
                // release and picked RemindLater — clear the stale tag.
                _settings.SkippedUpdateTag = string.Empty;
                _settings.Save();
            }

            return true; // RemindLater or Skip - continue launch
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update prompt/install failed: {ex.Message}");
            appState.UpdateInfo = new UpdateCommandCenterInfo
            {
                Status = "Failed",
                CurrentVersion = AppVersionInfo.Version,
                CheckedAt = DateTime.UtcNow,
                Detail = ex.Message
            };
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _updateInstallInProgress, 0);
        }
#endif
    }

    // Re-entrancy guard: the button/menu/deep-link are all fire-and-forget
    // (`_ = CheckForUpdatesUserInitiatedAsync()`), so a double-click would
    // otherwise open two ContentDialogs on the same XamlRoot which throws
    // COMException. One in-flight manual check at a time is enough.
    public async Task CheckForUpdatesUserInitiatedAsync()
    {
        if (Interlocked.CompareExchange(ref _manualUpdateCheckInFlight, 1, 0) != 0)
        {
            Logger.Info("Manual update check ignored: another check is already in progress");
            return;
        }

        try
        {
            Logger.Info("Manual update check requested");
            // Pass userInitiated=true so an explicit click bypasses the
            // "remind me later" SkippedUpdateTag — the user is asking *now*.
            var shouldContinue = await CheckForUpdatesAsync(userInitiated: true);
            refreshStatus();

            // The "Available" path already prompts via UpdateDialog. For the
            // other terminal states a manual click would otherwise produce no
            // UI at all, leaving users wondering whether the click registered.
            // Surface each explicitly with a small OK dialog.
            var info = appState.UpdateInfo;
            if (info != null)
            {
                switch (info.Status)
                {
                    case "Current":
                        await ShowUpdateInfoDialogAsync(
                            "UpToDate",
                            LocalizationHelper.GetString("Update_Title_UpToDate"),
                            LocalizationHelper.Format("Update_Message_UpToDate", info.CurrentVersion));
                        break;
                    case "Failed":
                        // Format string ends with "\n\n{0}"; an empty Detail
                        // would leave a dangling blank line. Trim only the
                        // newline characters we added, never arbitrary
                        // whitespace from the localized string.
                        var failedMessage = LocalizationHelper
                            .Format("Update_Message_Failed", info.Detail ?? "")
                            .TrimEnd('\r', '\n');
                        await ShowUpdateInfoDialogAsync(
                            "Failed",
                            LocalizationHelper.GetString("Update_Title_Failed"),
                            failedMessage);
                        break;
                    // User-skipped versions keep Status="Available". This state
                    // only represents an app build whose update channel is
                    // intentionally disabled.
                    case "Skipped":
                        await ShowUpdateInfoDialogAsync(
                            "Skipped",
                            LocalizationHelper.GetString("Update_Title_Skipped"),
                            LocalizationHelper.GetString(
                                PackageHelper.IsPackaged
                                    ? "Update_Message_Skipped_Store"
                                    : AppIdentity.IsDev
                                        ? "Update_Message_Skipped_Dev"
                                        : "Update_Message_Skipped_Debug"));
                        break;
                }
            }

            if (!shouldContinue)
                exit();
        }
        finally
        {
            Interlocked.Exchange(ref _manualUpdateCheckInFlight, 0);
        }
    }

    private async Task ShowUpdateInfoDialogAsync(string logKey, string title, string message)
    {
        // Prefer the Hub window when open so the dialog appears modal to what
        // the user is actually looking at; fall back to the hidden keep-alive
        // window so the dialog still renders if the Hub has been dismissed.
        var xamlRoot = getXamlRoot();
        if (xamlRoot == null)
        {
            Logger.Warn($"[Update] No XAML root available to show dialog: {logKey}");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = LocalizationHelper.GetString("Update_OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (COMException ex)
        {
            // ContentDialog.ShowAsync throws COMException if its XamlRoot's
            // visual tree is torn down mid-await (e.g. Hub window closed).
            Logger.Warn($"[Update] Dialog dismissed before completion ({logKey}): 0x{ex.HResult:X8}");
        }
        catch (InvalidOperationException ex)
        {
            // WinUI throws InvalidOperationException when another ContentDialog
            // is already open on the same thread/XamlRoot. The re-entrancy
            // guard only blocks duplicate *update* dialogs; collisions with
            // other features' dialogs (onboarding, connection, etc.) must be
            // tolerated here so the fire-and-forget call sites don't crash.
            Logger.Warn($"[Update] Dialog could not be shown ({logKey}): {ex.Message}");
        }
    }

    private async Task<bool> DownloadAndInstallUpdateAsync()
    {
        DownloadProgressDialog? progressDialog = null;
        try
        {
            progressDialog = new DownloadProgressDialog(updater);
            progressDialog.ShowAsync(); // Fire and forget

            var downloadedAsset = await updater.DownloadUpdateAsync();

            TryCloseProgressDialog(progressDialog);

            if (downloadedAsset == null || !File.Exists(downloadedAsset.FilePath))
            {
                Logger.Error("Update download failed or file missing");
                return false;
            }

            Logger.Info("Installing update and restarting...");
            await updater.InstallUpdateAsync(downloadedAsset);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed: {ex.Message}");
            TryCloseProgressDialog(progressDialog);
            return false;
        }
    }

    private static void TryCloseProgressDialog(DownloadProgressDialog? dialog)
    {
        if (dialog == null) return;
        try
        {
            dialog.Close();
        }
        // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
        catch (COMException)
        {
            // Window already closed — closing a closed WinUI window throws
            // COMException 0x80070578. Swallow so a real exception in the
            // outer catch isn't masked by this cleanup failure.
        }
        // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
        catch (InvalidOperationException)
        {
            // Same as above for other "already-disposed" race variants.
        }
    }
}

internal sealed class UpdatumUpdateCheckBoundary(UpdatumManager updater) : IUpdateCheckBoundary
{
    public Task<bool> CheckForUpdatesAsync() => updater.CheckForUpdatesAsync();

    public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates()
    {
        foreach (var release in updater.Releases)
        {
            yield return new UpdateReleaseCandidate(
                release.TagName,
                release.Draft,
                release.Prerelease,
                release.PublishedAt is not null,
                () => updater.GetCompatibleReleaseAsset(release) is not null,
                () => updater.ForceTriggerUpdateFromRelease(release));
        }
    }
}
