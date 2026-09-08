using System.Collections.Immutable;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubPageRegistryTests
{
    [Theory]
    [InlineData("home", "connection")]
    [InlineData("general", "connection")]
    [InlineData("about", "settings")]
    [InlineData("info", "settings")]
    [InlineData("nodes", "instances")]
    [InlineData("cron", "agent:alpha:cron")]
    [InlineData("workspace", "agent:alpha:workspace")]
    [InlineData("skills", "extensions")]
    [InlineData("HOME", "HOME")]
    [InlineData("Cron", "Cron")]
    public void NormalizeTag_PreservesExactCaseSensitiveAliases(string tag, string expected)
    {
        Assert.Equal(expected, HubPageRegistry.NormalizeTag(tag, "alpha"));
    }

    [Theory]
    [InlineData("chat", (int)HubPageKind.Chat)]
    [InlineData("local-ai", (int)HubPageKind.LocalAi)]
    [InlineData("nodes", (int)HubPageKind.Instances)]
    [InlineData("capabilities", (int)HubPageKind.Permissions)]
    [InlineData("permissions", (int)HubPageKind.Permissions)]
    [InlineData("activity", (int)HubPageKind.Channels)]
    [InlineData("conversations", (int)HubPageKind.Sessions)]
    [InlineData("info", (int)HubPageKind.Settings)]
    [InlineData("agent:main", (int)HubPageKind.Workspace)]
    [InlineData("agent:main:sessions", (int)HubPageKind.Sessions)]
    [InlineData("agent:main:agentevents", (int)HubPageKind.AgentEvents)]
    [InlineData("skills", (int)HubPageKind.Extensions)]
    [InlineData("extensions", (int)HubPageKind.Extensions)]
    [InlineData("agent:main:skills", (int)HubPageKind.Extensions)]
    [InlineData("agent:main:extensions", (int)HubPageKind.Extensions)]
    [InlineData("agent:main:cron", (int)HubPageKind.Cron)]
    [InlineData("agent:main:workspace", (int)HubPageKind.Workspace)]
    public void ResolvePage_OwnsDirectLegacyAndAgentMappings(string tag, int expected)
    {
        Assert.Equal((HubPageKind)expected, HubPageRegistry.ResolvePage(tag));
    }

    [Theory]
    [InlineData("PERMISSIONS")]
    [InlineData("Agent:main")]
    [InlineData("agent:main:unknown")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolvePage_RejectsUnknownOrWrongCaseTags(string? tag)
    {
        Assert.Null(HubPageRegistry.ResolvePage(tag));
    }

    [Theory]
    [InlineData("agent:main")]
    [InlineData("AGENT:MAIN")]
    [InlineData("chat")]
    [InlineData("SESSIONS")]
    [InlineData("skills")]
    [InlineData("extensions")]
    [InlineData("channels")]
    [InlineData("instances")]
    [InlineData("agentevents")]
    [InlineData("bindings")]
    [InlineData("config")]
    [InlineData("usage")]
    [InlineData("cron")]
    [InlineData("workspace")]
    public void GatewayClassification_PreservesCaseInsensitiveCurrentSet(string tag)
    {
        Assert.True(HubPageRegistry.IsGatewayPageTag(tag));
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("permissions")]
    [InlineData("local-ai")]
    [InlineData("debug")]
    [InlineData(null)]
    public void GatewayClassification_RejectsNonGatewayTags(string? tag)
    {
        Assert.False(HubPageRegistry.IsGatewayPageTag(tag));
    }

    [Theory]
    [InlineData("config", true)]
    [InlineData("CONFIG", true)]
    [InlineData("channels", false)]
    [InlineData(null, false)]
    public void KeepVisibleDuringDisconnect_OnlyAppliesToConfig(string? tag, bool expected)
    {
        Assert.Equal(expected, HubPageRegistry.ShouldKeepCurrentPageVisibleDuringDisconnect(tag));
    }

    [Fact]
    public void BuildCommands_PreservesBaseOrderActionsIconsAndResourceKeys()
    {
        var commands = HubPageRegistry.BuildCommands(Context());

        Assert.Equal(
            new string?[]
            {
                "connection", "local-ai", "chat", "sessions", "agentevents", "extensions",
                "agent:alpha:cron", "agent:alpha", "channels", "instances", "config",
                "usage", "bindings", "permissions", "settings", "notifications",
                "chat", null
            },
            commands.Select(CommandValue).ToArray());
        Assert.Equal(
            ["🔌", "AI", "💬", "🧠", "🧠", "🧩", "🧠", "🧠", "📡", "📡", "📡", "📡", "📡", "🛡️", "⚙️", "🔔", "💬", "🌐"],
            commands.Select(command => command.Icon).ToArray());
        Assert.Equal("Command_GoToConnection_Title", commands[0].Title);
        Assert.Equal("Command_GoToConnection_Subtitle", commands[0].Subtitle);
        Assert.Equal("Cron alpha", commands[6].Title);
        Assert.Equal(HubCommandActionKind.OpenDashboard, commands[17].Action.Kind);
        Assert.Null(commands[17].Action.Value);
    }

    [Fact]
    public void BuildCommands_DiagnosticToggleAndSessionsPreserveInsertionOrderAndSemantics()
    {
        var commands = HubPageRegistry.BuildCommands(Context(
            diagnosticsVisible: true,
            toggles: new HubCommandToggleState(true, false, true, false, true),
            sessions: ["session/z", "session/a"]));

        Assert.Equal("debug", CommandValue(commands[18]));
        Assert.Equal(
            [
                HubSettingToggle.NodeMode,
                HubSettingToggle.Camera,
                HubSettingToggle.Canvas,
                HubSettingToggle.ScreenCapture,
                HubSettingToggle.BrowserControl
            ],
            commands.Skip(19).Take(5).Select(command => command.Action.Toggle).ToArray());
        Assert.Equal(
            new string?[] { "Command_Subtitle_CurrentlyOn", "Command_Subtitle_CurrentlyOff", "Command_Subtitle_CurrentlyOn", "Command_Subtitle_CurrentlyOff", "Command_Subtitle_CurrentlyOn" },
            commands.Skip(19).Take(5).Select(command => command.Subtitle).ToArray());
        Assert.Equal(["Go to session: session/z", "Go to session: session/a"], commands.TakeLast(2).Select(command => command.Title).ToArray());
        Assert.Equal(new string?[] { "sessions/session/z", "sessions/session/a" }, commands.TakeLast(2).Select(command => command.Action.Value).ToArray());
        Assert.All(commands.TakeLast(2), command => Assert.Equal("Open in dashboard", command.Subtitle));
    }

    [Fact]
    public void Search_EmptyReturnsFirstEightAndQueryMatchesTitleOrSubtitleInInsertionOrder()
    {
        var commands = Enumerable.Range(0, 14)
            .Select(index => new HubCommand(
                index.ToString(),
                index is 2 or 11 ? $"Target {index}" : $"Title {index}",
                index is 4 or 12 ? $"TARGET subtitle {index}" : $"Subtitle {index}",
                HubCommandAction.Navigate(index.ToString())))
            .ToImmutableArray();

        Assert.Equal(Enumerable.Range(0, 8).Select(i => i.ToString()), HubPageRegistry.SearchCommands(commands, "   ").Select(c => c.Icon));
        Assert.Equal(["2", "4", "11", "12"], HubPageRegistry.SearchCommands(commands, "target").Select(c => c.Icon));
    }

    [Fact]
    public void Search_QueryIsLimitedToFirstTenMatches()
    {
        var commands = Enumerable.Range(0, 12)
            .Select(index => new HubCommand("", $"Match {index}", null, HubCommandAction.Navigate(index.ToString())))
            .ToImmutableArray();

        Assert.Equal(10, HubPageRegistry.SearchCommands(commands, "MATCH").Length);
    }

    private static HubCommandContext Context(
        bool diagnosticsVisible = false,
        HubCommandToggleState? toggles = null,
        IReadOnlyList<string>? sessions = null)
    {
        var resources = HubPageRegistry.CommandResourceKeys.ToImmutableDictionary(
            key => key,
            key => key switch
            {
                "Command_GoToCron_Title" => "Cron {0}",
                "Command_GoToWorkspace_Title" => "Workspace {0}",
                _ => key
            },
            StringComparer.Ordinal);
        return new HubCommandContext(
            "alpha",
            diagnosticsVisible,
            toggles,
            (sessions ?? []).ToImmutableArray(),
            resources);
    }

    private static string? CommandValue(HubCommand command) => command.Action.Value;
}
