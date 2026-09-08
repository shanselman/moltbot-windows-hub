using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Characterization of the Settings page's save behavior, now owned by
/// <see cref="SettingsPageViewModel"/>: each bound property writes the correct settings field,
/// preserves the mutate -> save -> notify contract, flashes the saved indicator, keeps the two
/// chat side effects, and does not echo/re-persist on external changes. Toggle tests flip relative
/// to the loaded value so they hold regardless of a field's default.
/// </summary>
public sealed class SettingsPageViewModelTests
{
    private static SettingsPageViewModel NewVm(
        out SettingsManager settings,
        out FakeAppCommands appCommands,
        out RecordingUiDispatcher dispatcher,
        out TempDir temp)
    {
        temp = new TempDir();
        settings = new SettingsManager(temp.Path);
        appCommands = new FakeAppCommands();
        dispatcher = new RecordingUiDispatcher();
        var store = new SettingsStore(settings, dispatcher);
        return new SettingsPageViewModel(store, appCommands);
    }

    [Fact]
    public void Activate_LoadsCurrentSettings()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            settings.GlobalHotkeyEnabled = true;
            settings.AppTheme = "Dark";
            settings.NotifyBuild = false;

            vm.Activate(null);

            Assert.True(vm.GlobalHotkeyEnabled);
            Assert.Equal("Dark", vm.AppTheme);
            Assert.False(vm.NotifyBuild);
        }
    }

    [Fact]
    public void TogglingBool_PersistsField_AndNotifiesOnce_AndFlashesSaved()
    {
        var vm = NewVm(out var settings, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);
            var savedFlashes = 0;
            vm.SavedIndicated += (_, _) => savedFlashes++;

            var target = !vm.GlobalHotkeyEnabled;
            vm.GlobalHotkeyEnabled = target;

            Assert.Equal(target, settings.GlobalHotkeyEnabled);    // persisted the right field
            Assert.Equal(1, appCommands.NotifySettingsSavedCount); // notified exactly once (no echo)
            Assert.Equal(1, savedFlashes);
        }
    }

    [Fact]
    public void LocationConsent_PersistsField()
    {
        var vm = NewVm(out var settings, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);

            var target = !vm.LocationConsentGiven;
            vm.LocationConsentGiven = target;

            Assert.Equal(target, settings.LocationConsentGiven);
            Assert.Equal(1, appCommands.NotifySettingsSavedCount);
        }
    }

    [Fact]
    public void ShowDiagnostics_WritesOverride_NotEffectiveOnly()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);

            var target = !vm.ShowDiagnostics;
            vm.ShowDiagnostics = target;

            Assert.Equal((bool?)target, settings.ShowDiagnosticsOverride);
        }
    }

    [Fact]
    public void AppTheme_And_NotificationSound_PersistStringTag()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);

            vm.AppTheme = "Light";
            vm.NotificationSound = "Subtle";

            Assert.Equal("Light", settings.AppTheme);
            Assert.Equal("Subtle", settings.NotificationSound);
        }
    }

    [Fact]
    public void AutoStart_AppliedThroughAppCommand_FlashesSavedOnlyOnSuccess()
    {
        var vm = NewVm(out _, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);
            var savedFlashes = 0;
            vm.SavedIndicated += (_, _) => savedFlashes++;

            var target = !vm.AutoStart;
            vm.AutoStart = target;

            // Routed through the app command that owns the save -> OS-write -> notify ordering.
            // With a completed (successful) task, the saved flash runs after the awaited apply.
            Assert.Equal((bool?)target, appCommands.AutoStartApplied);
            Assert.Equal(1, appCommands.AutoStartApplyCount);
            Assert.NotNull(appCommands.LastAutoStartOrigin);
            Assert.Equal(0, appCommands.NotifySettingsSavedCount);
            Assert.Equal(1, savedFlashes);
        }
    }

    [Fact]
    public void AutoStart_OsWriteFailure_DoesNotFlashSaved()
    {
        var vm = NewVm(out var settings, out var appCommands, out _, out var temp);
        using (temp)
        {
            appCommands.AutoStartResult = false; // simulate the OS registration failing
            vm.Activate(null);
            var savedFlashes = 0;
            vm.SavedIndicated += (_, _) => savedFlashes++;

            var effectiveValue = settings.AutoStart;
            vm.AutoStart = !vm.AutoStart;

            Assert.Equal(1, appCommands.AutoStartApplyCount);
            Assert.Equal(0, savedFlashes); // no confirmation when the apply reports failure
            Assert.Equal(effectiveValue, vm.AutoStart);
        }
    }

    [Fact]
    public void SelfWrite_AutoStartOrMute_DoesNotTriggerExternalReload()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        var dispatcher = new RecordingUiDispatcher();
        var store = new SettingsStore(settings, dispatcher);
        var appCommands = new SelfWritingAppCommands(store);
        var vm = new SettingsPageViewModel(store, appCommands);

        vm.Activate(null);
        var externalChanges = 0;
        vm.ExternalChanged += (_, _) => externalChanges++;

        // This persists directly to settings.Save() but marks a store self-write, so the store must
        // suppress Changed and the view model must not treat it as an external change.
        vm.AutoStart = !vm.AutoStart;

        Assert.Equal(0, externalChanges);

        // Control: a genuine external save (no self-write scope) still reloads.
        settings.NotifyStock = !settings.NotifyStock;
        settings.Save();
        Assert.Equal(1, externalChanges);
    }

    [Fact]
    public void NotificationSound_LegacyOrOddValue_NormalizesToKnownTag()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            settings.NotificationSound = "subtle"; // lower-case legacy value
            vm.Activate(null);
            Assert.Equal("Subtle", vm.NotificationSound);

            settings.NotificationSound = "bogus";
            settings.Save(); // external change -> reload
            Assert.Equal("Default", vm.NotificationSound); // unknown -> default, never blank
        }
    }

    [Fact]
    public void ExternalChange_RaisesExternalChanged_ForViewOwnedRefresh()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);
            var externalChanges = 0;
            vm.ExternalChanged += (_, _) => externalChanges++;

            settings.NotifyStock = !settings.NotifyStock;
            settings.Save();

            Assert.Equal(1, externalChanges);
        }
    }

    [Fact]
    public void ShowChatToolCalls_Persists_AndNotifies()
    {
        var vm = NewVm(out var settings, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);

            var target = !vm.ShowChatToolCalls;
            vm.ShowChatToolCalls = target;

            Assert.Equal(target, settings.ShowChatToolCalls);
            Assert.Equal(1, appCommands.NotifySettingsSavedCount);
        }
    }

    [Fact]
    public void ExternalChange_ReloadsWithoutRePersisting()
    {
        var vm = NewVm(out var settings, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);
            var target = !vm.NotifyStock;

            // Simulate another surface saving a change.
            settings.NotifyStock = target;
            settings.Save();

            Assert.Equal(target, vm.NotifyStock);                  // VM reflected the external change
            Assert.Equal(0, appCommands.NotifySettingsSavedCount); // and did NOT re-persist (no echo/save-storm)
        }
    }

    [Fact]
    public void SelfChange_DoesNotDoublePersist()
    {
        var vm = NewVm(out _, out var appCommands, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);

            vm.ShowNotifications = !vm.ShowNotifications;

            Assert.Equal(1, appCommands.NotifySettingsSavedCount); // exactly one, not two
        }
    }

    [Fact]
    public void AfterDeactivate_ExternalChangeIsIgnored()
    {
        var vm = NewVm(out var settings, out _, out _, out var temp);
        using (temp)
        {
            vm.Activate(null);
            var before = vm.NotifyStock;
            vm.Deactivate();

            settings.NotifyStock = !before;
            settings.Save();

            Assert.Equal(before, vm.NotifyStock); // unsubscribed, so no reload
        }
    }
}
