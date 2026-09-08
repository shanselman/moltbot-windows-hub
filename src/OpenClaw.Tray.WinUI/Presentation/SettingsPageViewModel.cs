using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// WinUI-free view model for the Settings page. It owns the settings surface's read/persist
/// logic that previously lived in the page code-behind: it loads the current values from
/// <see cref="ISettingsStore"/> on activation, exposes two-way-bindable properties, and persists
/// each change through the store while preserving the exact auto-save contract
/// (mutate -> save -> notify).
/// </summary>
/// <remarks>
/// Echo handling lives in the store, not here: a property change persists through
/// <see cref="ISettingsStore.Update"/>, and the resulting <see cref="ISettingsStore.Changed"/>
/// event is ignored only when it carries this instance's writer token. Any other change arrives
/// via <see cref="ISettingsStore.Changed"/> and reloads the properties under the <c>_loading</c>
/// guard so it cannot re-persist. View-only side effects (the "Saved" indicator flash and
/// refreshing the view-owned gateway section) are surfaced as events the page handles. OS-coupled
/// auto-start registration is delegated to <see cref="IAppCommands"/>, so this type stays free of
/// WinUI and OS APIs.
/// </remarks>
internal sealed class SettingsPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private const string DefaultNotificationSound = "Default";
    private const string DefaultAppTheme = "System";

    private static readonly string[] SoundTags = { "Default", "None", "Subtle" };
    private static readonly string[] ThemeTags = { "System", "Light", "Dark" };

    private readonly ISettingsStore _store;
    private readonly IAppCommands _appCommands;
    private readonly SettingsWriteOrigin _origin;

    private bool _loading;
    private bool _subscribed;
    private long _lastAppliedSettingsVersion = -1;

    private bool _autoStart;
    private bool _globalHotkeyEnabled;
    private bool _useLegacyWebChat;
    private bool _showNotifications;
    private string _notificationSound = DefaultNotificationSound;
    private string _appTheme = DefaultAppTheme;
    private bool _showDiagnostics;
    private bool _notifyHealth;
    private bool _notifyUrgent;
    private bool _notifyReminder;
    private bool _notifyEmail;
    private bool _notifyCalendar;
    private bool _notifyBuild;
    private bool _notifyStock;
    private bool _notifyInfo;
    private bool _screenRecordingConsentGiven;
    private bool _cameraRecordingConsentGiven;
    private bool _locationConsentGiven;
    private bool _showChatToolCalls;

    public SettingsPageViewModel(ISettingsStore store, IAppCommands appCommands)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _origin = _store.CreateOrigin();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised (on the caller's thread) when a change should flash the page's "Saved" indicator.</summary>
    public event EventHandler? SavedIndicated;

    /// <summary>
    /// Raised when the settings change from an external source (not this view model's own writes),
    /// so the page can refresh the view-owned sections that are not settings-bound (the gateway
    /// section). Not raised during the initial activation load.
    /// </summary>
    public event EventHandler? ExternalChanged;

    internal bool IsActive { get; private set; }
    internal bool IsDisposed { get; private set; }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (SetField(ref _autoStart, value) && !_loading)
            {
                // Auto-start owns an OS registration with a required save -> OS-write -> notify
                // order. It is applied through the app command that preserves that ordering, and
                // the saved indicator is flashed only after the OS write succeeds (matching the
                // original behavior where a failed OS write showed no confirmation).
                _ = ApplyAutoStartAsync(value);
            }
        }
    }

    private async Task ApplyAutoStartAsync(bool value)
    {
        if (await _appCommands.ApplyAutoStart(_origin, value))
        {
            RaiseSaved();
            return;
        }

        _loading = true;
        try
        {
            SetField(ref _autoStart, _store.Current.AutoStart, nameof(AutoStart));
        }
        finally
        {
            _loading = false;
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set { if (SetField(ref _globalHotkeyEnabled, value) && !_loading) Persist(e => e.GlobalHotkeyEnabled = value); }
    }

    public bool UseLegacyWebChat
    {
        get => _useLegacyWebChat;
        set { if (SetField(ref _useLegacyWebChat, value) && !_loading) Persist(e => e.UseLegacyWebChat = value); }
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set { if (SetField(ref _showNotifications, value) && !_loading) Persist(e => e.ShowNotifications = value); }
    }

    public string NotificationSound
    {
        get => _notificationSound;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? DefaultNotificationSound : value;
            if (SetField(ref _notificationSound, normalized) && !_loading) Persist(e => e.NotificationSound = normalized);
        }
    }

    public string AppTheme
    {
        get => _appTheme;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? DefaultAppTheme : value;
            if (SetField(ref _appTheme, normalized) && !_loading) Persist(e => e.AppTheme = normalized);
        }
    }

    /// <summary>Bound to the diagnostics toggle: reads the effective value, writes the override.</summary>
    public bool ShowDiagnostics
    {
        get => _showDiagnostics;
        set { if (SetField(ref _showDiagnostics, value) && !_loading) Persist(e => e.ShowDiagnosticsOverride = value); }
    }

    public bool NotifyHealth
    {
        get => _notifyHealth;
        set { if (SetField(ref _notifyHealth, value) && !_loading) Persist(e => e.NotifyHealth = value); }
    }

    public bool NotifyUrgent
    {
        get => _notifyUrgent;
        set { if (SetField(ref _notifyUrgent, value) && !_loading) Persist(e => e.NotifyUrgent = value); }
    }

    public bool NotifyReminder
    {
        get => _notifyReminder;
        set { if (SetField(ref _notifyReminder, value) && !_loading) Persist(e => e.NotifyReminder = value); }
    }

    public bool NotifyEmail
    {
        get => _notifyEmail;
        set { if (SetField(ref _notifyEmail, value) && !_loading) Persist(e => e.NotifyEmail = value); }
    }

    public bool NotifyCalendar
    {
        get => _notifyCalendar;
        set { if (SetField(ref _notifyCalendar, value) && !_loading) Persist(e => e.NotifyCalendar = value); }
    }

    public bool NotifyBuild
    {
        get => _notifyBuild;
        set { if (SetField(ref _notifyBuild, value) && !_loading) Persist(e => e.NotifyBuild = value); }
    }

    public bool NotifyStock
    {
        get => _notifyStock;
        set { if (SetField(ref _notifyStock, value) && !_loading) Persist(e => e.NotifyStock = value); }
    }

    public bool NotifyInfo
    {
        get => _notifyInfo;
        set { if (SetField(ref _notifyInfo, value) && !_loading) Persist(e => e.NotifyInfo = value); }
    }

    public bool ScreenRecordingConsentGiven
    {
        get => _screenRecordingConsentGiven;
        set { if (SetField(ref _screenRecordingConsentGiven, value) && !_loading) Persist(e => e.ScreenRecordingConsentGiven = value); }
    }

    public bool CameraRecordingConsentGiven
    {
        get => _cameraRecordingConsentGiven;
        set { if (SetField(ref _cameraRecordingConsentGiven, value) && !_loading) Persist(e => e.CameraRecordingConsentGiven = value); }
    }

    public bool LocationConsentGiven
    {
        get => _locationConsentGiven;
        set { if (SetField(ref _locationConsentGiven, value) && !_loading) Persist(e => e.LocationConsentGiven = value); }
    }

    /// <summary>
    /// "Show tool calls and usage" persists the setting. App's settings-save path
    /// applies it to every live Reactor chat host.
    /// </summary>
    public bool ShowChatToolCalls
    {
        get => _showChatToolCalls;
        set
        {
            if (SetField(ref _showChatToolCalls, value) && !_loading)
            {
                Persist(e => e.ShowChatToolCalls = value);
            }
        }
    }

    public void Activate(object? parameter)
    {
        IsActive = true;
        if (!_subscribed)
        {
            _store.Changed += OnStoreChanged;
            _subscribed = true;
        }

        LoadCurrentFromStore();
    }

    public void Deactivate()
    {
        IsActive = false;
        if (_subscribed)
        {
            _store.Changed -= OnStoreChanged;
            _subscribed = false;
        }
    }

    public void Dispose()
    {
        Deactivate();
        IsDisposed = true;
    }

    private void OnStoreChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!IsActive || !TryAdvanceSettingsVersion(e.Version))
        {
            return;
        }

        if (ApplySettingsSnapshotIfCurrent(e.Snapshot))
        {
            if (!ReferenceEquals(e.Origin, _origin))
            {
                ExternalChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Reloads all bound properties from the store snapshot without persisting.</summary>
    private void LoadCurrentFromStore()
    {
        var snapshot = _store.Current;
        TryAdvanceSettingsVersion(snapshot.Version);
        ApplySettingsSnapshotIfCurrent(snapshot);
    }

    private bool ApplySettingsSnapshotIfCurrent(SettingsSnapshot snapshot)
    {
        if (snapshot.Version != Volatile.Read(ref _lastAppliedSettingsVersion))
        {
            return false;
        }

        LoadFromStore(snapshot);
        return snapshot.Version == Volatile.Read(ref _lastAppliedSettingsVersion);
    }

    private bool TryAdvanceSettingsVersion(long version)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastAppliedSettingsVersion);
            if (version <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastAppliedSettingsVersion, version, current) == current)
            {
                return true;
            }
        }
    }

    private void LoadFromStore(SettingsSnapshot s)
    {
        _loading = true;
        try
        {
            SetField(ref _autoStart, s.AutoStart, nameof(AutoStart));
            SetField(ref _globalHotkeyEnabled, s.GlobalHotkeyEnabled, nameof(GlobalHotkeyEnabled));
            SetField(ref _useLegacyWebChat, s.UseLegacyWebChat, nameof(UseLegacyWebChat));
            SetField(ref _showNotifications, s.ShowNotifications, nameof(ShowNotifications));
            // Normalize combo values to a known tag so a legacy or odd stored value selects the
            // default item instead of rendering a blank combo (matching the old tag lookup).
            SetField(ref _notificationSound, NormalizeTag(s.NotificationSound, SoundTags, DefaultNotificationSound), nameof(NotificationSound));
            SetField(ref _appTheme, NormalizeTag(s.AppTheme, ThemeTags, DefaultAppTheme), nameof(AppTheme));
            SetField(ref _showDiagnostics, s.ShowDiagnosticsEffective, nameof(ShowDiagnostics));
            SetField(ref _notifyHealth, s.NotifyHealth, nameof(NotifyHealth));
            SetField(ref _notifyUrgent, s.NotifyUrgent, nameof(NotifyUrgent));
            SetField(ref _notifyReminder, s.NotifyReminder, nameof(NotifyReminder));
            SetField(ref _notifyEmail, s.NotifyEmail, nameof(NotifyEmail));
            SetField(ref _notifyCalendar, s.NotifyCalendar, nameof(NotifyCalendar));
            SetField(ref _notifyBuild, s.NotifyBuild, nameof(NotifyBuild));
            SetField(ref _notifyStock, s.NotifyStock, nameof(NotifyStock));
            SetField(ref _notifyInfo, s.NotifyInfo, nameof(NotifyInfo));
            SetField(ref _screenRecordingConsentGiven, s.ScreenRecordingConsentGiven, nameof(ScreenRecordingConsentGiven));
            SetField(ref _cameraRecordingConsentGiven, s.CameraRecordingConsentGiven, nameof(CameraRecordingConsentGiven));
            SetField(ref _locationConsentGiven, s.LocationConsentGiven, nameof(LocationConsentGiven));
            SetField(ref _showChatToolCalls, s.ShowChatToolCalls, nameof(ShowChatToolCalls));
        }
        finally
        {
            _loading = false;
        }
    }

    private void Persist(Action<ISettingsEditor> edit)
    {
        if (_loading)
        {
            return;
        }

        _store.Update(_origin, edit);
        _appCommands.NotifySettingsSaved();
        RaiseSaved();
    }

    private void RaiseSaved() => SavedIndicated?.Invoke(this, EventArgs.Empty);

    /// <summary>Maps a stored value to a known combo tag (case-insensitive), falling back to the default.</summary>
    private static string NormalizeTag(string? value, string[] tags, string fallback)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag, value, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        return fallback;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
