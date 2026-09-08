using System.Collections.Immutable;
using System.Globalization;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Pages;
#endif

namespace OpenClawTray.Presentation;

internal enum HubPageKind
{
    Chat,
    Connection,
    LocalAi,
    Channels,
    Instances,
    Config,
    Usage,
    Bindings,
    Permissions,
    Voice,
    Sandbox,
    Settings,
    Notifications,
    Debug,
    Sessions,
    AgentEvents,
    Extensions,
    Cron,
    Workspace
}

internal enum HubCommandActionKind
{
    Navigate,
    OpenDashboard,
    ToggleSetting
}

internal enum HubSettingToggle
{
    NodeMode,
    Camera,
    Canvas,
    ScreenCapture,
    BrowserControl
}

internal sealed record HubCommandAction(
    HubCommandActionKind Kind,
    string? Value = null,
    HubSettingToggle? Toggle = null)
{
    public static HubCommandAction Navigate(string tag) =>
        new(HubCommandActionKind.Navigate, tag);

    public static HubCommandAction OpenDashboard(string? path = null) =>
        new(HubCommandActionKind.OpenDashboard, path);

    public static HubCommandAction ToggleSetting(HubSettingToggle toggle) =>
        new(HubCommandActionKind.ToggleSetting, Toggle: toggle);
}

internal sealed record HubCommand(
    string Icon,
    string Title,
    string? Subtitle,
    HubCommandAction Action)
{
    public override string ToString() => Title;
}

internal sealed record HubCommandToggleState(
    bool NodeMode,
    bool Camera,
    bool Canvas,
    bool ScreenCapture,
    bool BrowserControl);

internal sealed record HubCommandContext(
    string CurrentAgentId,
    bool DiagnosticsVisible,
    HubCommandToggleState? Toggles,
    ImmutableArray<string> SessionKeys,
    ImmutableDictionary<string, string> Resources);

internal static class HubPageRegistry
{
    public static ImmutableArray<string> CommandResourceKeys { get; } =
    [
        "Command_GoToConnection_Title",
        "Command_GoToConnection_Subtitle",
        "Command_GoToLocalAi_Title",
        "Command_GoToLocalAi_Subtitle",
        "Command_GoToChat_Title",
        "Command_GoToChat_Subtitle",
        "Command_GoToSessions_Title",
        "Command_GoToSessions_Subtitle",
        "Command_GoToAgentEvents_Title",
        "Command_GoToAgentEvents_Subtitle",
        "Command_GoToSkills_Title",
        "Command_GoToSkills_Subtitle",
        "Command_GoToCron_Title",
        "Command_GoToCron_Subtitle",
        "Command_GoToWorkspace_Title",
        "Command_GoToWorkspace_Subtitle",
        "Command_GoToChannels_Title",
        "Command_GoToChannels_Subtitle",
        "Command_GoToInstances_Title",
        "Command_GoToInstances_Subtitle",
        "Command_GoToConfig_Title",
        "Command_GoToConfig_Subtitle",
        "Command_GoToUsage_Title",
        "Command_GoToUsage_Subtitle",
        "Command_GoToBindings_Title",
        "Command_GoToBindings_Subtitle",
        "Command_GoToPermissions_Title",
        "Command_GoToPermissions_Subtitle",
        "Command_GoToSettings_Title",
        "Command_GoToSettings_Subtitle",
        "Command_GoToNotifications_Title",
        "Command_GoToNotifications_Subtitle",
        "Command_OpenChatWindow_Title",
        "Command_OpenChatWindow_Subtitle",
        "Command_OpenDashboard_Title",
        "Command_OpenDashboard_Subtitle",
        "Command_GoToDiagnostics_Title",
        "Command_GoToDiagnostics_Subtitle",
        "Command_Subtitle_CurrentlyOn",
        "Command_Subtitle_CurrentlyOff",
        "Command_ToggleNodeMode_Title",
        "Command_ToggleCamera_Title",
        "Command_ToggleCanvas_Title",
        "Command_ToggleScreenCapture_Title",
        "Command_ToggleBrowserControl_Title"
    ];

    public static string NormalizeTag(string tag, string currentAgentId) => tag switch
    {
        "home" or "general" => "connection",
        "about" or "info" => "settings",
        "nodes" => "instances",
        "cron" => $"agent:{currentAgentId}:cron",
        "workspace" => $"agent:{currentAgentId}:workspace",
        "skills" => "extensions",
        _ => tag
    };

    public static HubPageKind? ResolvePage(string? tag) => tag switch
    {
        "chat" => HubPageKind.Chat,
        "connection" => HubPageKind.Connection,
        "local-ai" => HubPageKind.LocalAi,
        "channels" => HubPageKind.Channels,
        "nodes" or "instances" => HubPageKind.Instances,
        "config" => HubPageKind.Config,
        "usage" => HubPageKind.Usage,
        "bindings" => HubPageKind.Bindings,
        "capabilities" or "permissions" => HubPageKind.Permissions,
        "voice" => HubPageKind.Voice,
        "sandbox" => HubPageKind.Sandbox,
        "activity" => HubPageKind.Channels,
        "settings" or "info" or "about" => HubPageKind.Settings,
        "notifications" => HubPageKind.Notifications,
        "debug" => HubPageKind.Debug,
        "home" or "general" => HubPageKind.Connection,
        "conversations" or "sessions" => HubPageKind.Sessions,
        "agentevents" => HubPageKind.AgentEvents,
        "skills" or "extensions" => HubPageKind.Extensions,
        "cron" => HubPageKind.Cron,
        "workspace" => HubPageKind.Workspace,
        _ when tag?.StartsWith("agent:", StringComparison.Ordinal) == true => ResolveAgentPage(tag),
        _ => null
    };

    private static HubPageKind? ResolveAgentPage(string tag)
    {
        var parts = tag.Split(':');
        if (parts.Length == 2)
            return HubPageKind.Workspace;

        return parts[2] switch
        {
            "sessions" => HubPageKind.Sessions,
            "agentevents" => HubPageKind.AgentEvents,
            "skills" or "extensions" => HubPageKind.Extensions,
            "cron" => HubPageKind.Cron,
            "workspace" => HubPageKind.Workspace,
            _ => null
        };
    }

    public static string ParseAgentId(string? tag)
    {
        if (tag?.StartsWith("agent:", StringComparison.Ordinal) != true)
            return "main";

        var parts = tag.Split(':');
        return parts.Length >= 2 ? parts[1] : "main";
    }

    public static bool IsGatewayPageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return tag.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("skills", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("extensions", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("channels", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("instances", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("agentevents", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("bindings", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("cron", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("workspace", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldKeepCurrentPageVisibleDuringDisconnect(string? currentTag) =>
        string.Equals(currentTag, "config", StringComparison.OrdinalIgnoreCase);

#if !OPENCLAW_TRAY_TESTS
    public static Type? ResolvePageType(string? tag) => ResolvePage(tag) switch
    {
        HubPageKind.Chat => typeof(ChatPage),
        HubPageKind.Connection => typeof(ConnectionPage),
        HubPageKind.LocalAi => typeof(LocalAiPage),
        HubPageKind.Channels => typeof(ChannelsPage),
        HubPageKind.Instances => typeof(InstancesPage),
        HubPageKind.Config => typeof(ConfigPage),
        HubPageKind.Usage => typeof(UsagePage),
        HubPageKind.Bindings => typeof(BindingsPage),
        HubPageKind.Permissions => typeof(PermissionsPage),
        HubPageKind.Voice => typeof(VoiceSettingsPage),
        HubPageKind.Sandbox => typeof(SandboxPage),
        HubPageKind.Settings => typeof(SettingsPage),
        HubPageKind.Notifications => typeof(NotificationsPage),
        HubPageKind.Debug => typeof(DebugPage),
        HubPageKind.Sessions => typeof(SessionsPage),
        HubPageKind.AgentEvents => typeof(AgentEventsPage),
        HubPageKind.Extensions => typeof(ExtensionsPage),
        HubPageKind.Cron => typeof(CronPage),
        HubPageKind.Workspace => typeof(WorkspacePage),
        _ => null
    };
#endif

    public static ImmutableArray<HubCommand> BuildCommands(HubCommandContext context)
    {
        var commands = ImmutableArray.CreateBuilder<HubCommand>();

        AddNavigation(commands, context, "🔌", "Command_GoToConnection", "connection");
        AddNavigation(commands, context, "AI", "Command_GoToLocalAi", "local-ai");
        AddNavigation(commands, context, "💬", "Command_GoToChat", "chat");
        AddNavigation(commands, context, "🧠", "Command_GoToSessions", "sessions");
        AddNavigation(commands, context, "🧠", "Command_GoToAgentEvents", "agentevents");
        AddNavigation(commands, context, "🧩", "Command_GoToSkills", "extensions");
        commands.Add(new HubCommand(
            "🧠",
            Format(context, "Command_GoToCron_Title", context.CurrentAgentId),
            Get(context, "Command_GoToCron_Subtitle"),
            HubCommandAction.Navigate($"agent:{context.CurrentAgentId}:cron")));
        commands.Add(new HubCommand(
            "🧠",
            Format(context, "Command_GoToWorkspace_Title", context.CurrentAgentId),
            Get(context, "Command_GoToWorkspace_Subtitle"),
            HubCommandAction.Navigate($"agent:{context.CurrentAgentId}")));
        AddNavigation(commands, context, "📡", "Command_GoToChannels", "channels");
        AddNavigation(commands, context, "📡", "Command_GoToInstances", "instances");
        AddNavigation(commands, context, "📡", "Command_GoToConfig", "config");
        AddNavigation(commands, context, "📡", "Command_GoToUsage", "usage");
        AddNavigation(commands, context, "📡", "Command_GoToBindings", "bindings");
        AddNavigation(commands, context, "🛡️", "Command_GoToPermissions", "permissions");
        AddNavigation(commands, context, "⚙️", "Command_GoToSettings", "settings");
        AddNavigation(commands, context, "🔔", "Command_GoToNotifications", "notifications");
        AddNavigation(commands, context, "💬", "Command_OpenChatWindow", "chat");
        commands.Add(new HubCommand(
            "🌐",
            Get(context, "Command_OpenDashboard_Title"),
            Get(context, "Command_OpenDashboard_Subtitle"),
            HubCommandAction.OpenDashboard()));

        if (context.DiagnosticsVisible)
            AddNavigation(commands, context, "🐛", "Command_GoToDiagnostics", "debug");

        if (context.Toggles is { } toggles)
        {
            AddToggle(commands, context, "🔌", "Command_ToggleNodeMode_Title", HubSettingToggle.NodeMode, toggles.NodeMode);
            AddToggle(commands, context, "📷", "Command_ToggleCamera_Title", HubSettingToggle.Camera, toggles.Camera);
            AddToggle(commands, context, "🎨", "Command_ToggleCanvas_Title", HubSettingToggle.Canvas, toggles.Canvas);
            AddToggle(commands, context, "🖥️", "Command_ToggleScreenCapture_Title", HubSettingToggle.ScreenCapture, toggles.ScreenCapture);
            AddToggle(commands, context, "🌐", "Command_ToggleBrowserControl_Title", HubSettingToggle.BrowserControl, toggles.BrowserControl);
        }

        foreach (var sessionKey in context.SessionKeys)
        {
            commands.Add(new HubCommand(
                "🧠",
                $"Go to session: {sessionKey}",
                "Open in dashboard",
                HubCommandAction.OpenDashboard($"sessions/{sessionKey}")));
        }

        return commands.ToImmutable();
    }

    public static ImmutableArray<HubCommand> SearchCommands(
        IEnumerable<HubCommand> commands,
        string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(trimmed)
            ? commands.Take(8)
            : commands.Where(command =>
                command.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                (command.Subtitle?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)).Take(10);
        return filtered.ToImmutableArray();
    }

    private static void AddNavigation(
        ImmutableArray<HubCommand>.Builder commands,
        HubCommandContext context,
        string icon,
        string resourcePrefix,
        string tag)
    {
        commands.Add(new HubCommand(
            icon,
            Get(context, resourcePrefix + "_Title"),
            Get(context, resourcePrefix + "_Subtitle"),
            HubCommandAction.Navigate(tag)));
    }

    private static void AddToggle(
        ImmutableArray<HubCommand>.Builder commands,
        HubCommandContext context,
        string icon,
        string titleResourceKey,
        HubSettingToggle toggle,
        bool isOn)
    {
        commands.Add(new HubCommand(
            icon,
            Get(context, titleResourceKey),
            Get(context, isOn ? "Command_Subtitle_CurrentlyOn" : "Command_Subtitle_CurrentlyOff"),
            HubCommandAction.ToggleSetting(toggle)));
    }

    private static string Get(HubCommandContext context, string key) =>
        context.Resources.TryGetValue(key, out var value) ? value : key;

    private static string Format(HubCommandContext context, string key, params object?[] args)
    {
        var template = Get(context, key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
