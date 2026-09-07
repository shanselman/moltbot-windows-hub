using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SessionTitleFormatterTests
{
    [Fact]
    public void Format_DistinguishesForkFromCanonicalSessionWithSameDisplayName()
    {
        var canonical = new SessionInfo
        {
            Key = "agent:main:main",
            IsMain = true,
            DisplayName = "OpenClaw Windows Tray",
        };
        var fork = new SessionInfo
        {
            Key = "agent:main:fork",
            DisplayName = "OpenClaw Windows Tray",
        };

        var titles = SessionTitleFormatter.FormatUnique([canonical, fork]);
        var canonicalTitle = titles[0];
        var forkTitle = titles[1];

        Assert.Equal("OpenClaw Windows Tray", canonicalTitle);
        Assert.Equal("OpenClaw Windows Tray (2)", forkTitle);
        Assert.NotEqual(canonicalTitle, forkTitle);
        Assert.Equal("agent:main:main", canonical.Key);
        Assert.Equal("agent:main:fork", fork.Key);
    }

    [Theory]
    [InlineData("agent:assistant:assistant", "Research", "Research")]
    [InlineData("agent:assistant:review", "Research", "Research")]
    [InlineData("legacy-session", "Research", "Research")]
    public void Format_UsesStableKeyQualifier(string key, string displayName, string expected)
    {
        var session = new SessionInfo { Key = key, DisplayName = displayName };

        Assert.Equal(expected, SessionTitleFormatter.Format(session));
    }

    [Fact]
    public void Format_PreservesExistingMissingDisplayNameFallbacks()
    {
        var main = new SessionInfo { Key = "agent:main:main", IsMain = true };
        var worker = new SessionInfo { Key = "agent:assistant:review" };

        Assert.Equal("Main session", SessionTitleFormatter.Format(main));
        Assert.Equal("review", SessionTitleFormatter.Format(worker));
    }

    [Fact]
    public void FormatUnique_DistinguishesMultiSegmentKeysWithSamePrefix()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "agent:main:subagent:uuid-b", DisplayName = "Research" },
            new SessionInfo { Key = "agent:main:subagent:uuid-a", DisplayName = "Research" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal("Research (2)", titles[0]);
        Assert.Equal("Research", titles[1]);
        Assert.Equal(2, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FormatUnique_DistinguishesLegacyKeysWithoutExposingThem()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-b", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-a", DisplayName = "Research" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal("Research (2)", titles[0]);
        Assert.Equal("Research", titles[1]);
        Assert.DoesNotContain("legacy", string.Join(" ", titles), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatUnique_DoesNotCollideWithNaturalNumberedTitle()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-a", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-b", DisplayName = "Research" },
            new SessionInfo { Key = "legacy-c", DisplayName = "Research (2)" },
        };

        var titles = SessionTitleFormatter.FormatUnique(sessions);

        Assert.Equal(new[] { "Research", "Research (3)", "Research (2)" }, titles);
        Assert.Equal(3, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FormatUnique_AssignmentRemainsStableWhenCallerFiltersSubset()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "legacy-a", DisplayName = "Research", Channel = "Slack" },
            new SessionInfo { Key = "legacy-b", DisplayName = "Research", Channel = "WhatsApp" },
        };
        var titles = SessionTitleFormatter.FormatUnique(sessions);
        var titledSessions = sessions
            .Select((session, index) => (Session: session, Title: titles[index]));

        var whatsAppTitle = titledSessions
            .Single(item => item.Session.Channel == "WhatsApp")
            .Title;

        Assert.Equal("Research (2)", whatsAppTitle);
    }

    [Theory]
    [InlineData("agent:main:telegram:main:direct:491234567890", "Telegram direct message")]
    [InlineData("agent:main:tui-01234567-89ab-cdef-0123-456789abcdef", "Terminal session")]
    [InlineData("agent:main:tui-01234567-89ab-cdef-0123-456789abcdef:heartbeat", "Heartbeat")]
    [InlineData("agent:main:subagent:01234567-89ab-cdef-0123-456789abcdef", "Subagent")]
    public void Format_DoesNotExposeOpaqueSessionKeys(string key, string expected)
    {
        Assert.Equal(expected, SessionTitleFormatter.Format(new SessionInfo
        {
            Key = key,
            DisplayName = key,
        }));
    }

    [Fact]
    public void Format_PreservesExplicitLabelsAndLocalizesGeneratedClassifications()
    {
        var explicitTitle = new SessionInfo
        {
            Key = "agent:main:telegram:main:direct:491234567890",
            Label = "Family chat",
            Classification = "direct",
            Channel = "telegram",
        };

        Assert.Equal("Family chat", SessionTitleFormatter.Format(explicitTitle));
    }

    [Fact]
    public void Format_HandlesCaseInsensitiveGeneratedRouteFamilies()
    {
        var session = new SessionInfo
        {
            Key = "route",
            Classification = "Direct",
            Channel = "telegram",
        };

        Assert.Equal("Telegram direct message", SessionTitleFormatter.Format(session));
    }

    [Fact]
    public void FormatSubtitle_UsesLocalFallbackWithoutStructuredParts()
    {
        var session = new SessionInfo
        {
            Key = "custom",
            DerivedTitle = "Custom",
        };

        Assert.Null(SessionTitleFormatter.FormatSubtitle(session));
    }

    [Fact]
    public void FormatSubtitle_MasksPhoneLikeAccountIds()
    {
        var session = new SessionInfo
        {
            Key = "custom",
            Classification = "direct",
            AccountId = "491234567890",
        };

        var subtitle = SessionTitleFormatter.FormatSubtitle(session);

        Assert.Equal("account …7890", subtitle);
        Assert.DoesNotContain("491234567890", subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionLocalizationResources_CoverGeneratedTitlesAndSubtitleLabels()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            var resources = File.ReadAllText(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Strings",
                locale,
                "Resources.resw"));
            Assert.Contains("SessionTitle_Heartbeat", resources, StringComparison.Ordinal);
            Assert.Contains("SessionTitle_Direct", resources, StringComparison.Ordinal);
            Assert.Contains("SessionSubtitle_Account", resources, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SessionsPage_UsesSharedSessionTitleFormatter()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml.cs"));

        var formatIndex = source.IndexOf("SessionTitleFormatter.FormatUnique(visibleSessions)", StringComparison.Ordinal);
        var channelFilterIndex = source.IndexOf("if (_activeChannel != \"all\")", StringComparison.Ordinal);

        Assert.True(formatIndex >= 0);
        Assert.True(channelFilterIndex > formatIndex);
        Assert.Contains("ToViewModel(item.Session, item.Title)", source, StringComparison.Ordinal);
        Assert.Contains("DisplayName = displayName", source, StringComparison.Ordinal);
        Assert.Contains("Key = s.Key", source, StringComparison.Ordinal);
        Assert.Contains("SessionsForCurrentBackgroundScope()", source, StringComparison.Ordinal);
        Assert.Contains("SessionDisplayResolver.IsVisible(session, _showBackgroundSessions)", source, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml"));
        Assert.Contains("Tag=\"{Binding Key}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SessionsPageShowBackground", xaml, StringComparison.Ordinal);

        var chatRoot = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawReactorChatRoot.cs"));
        var chatComposer = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "ReactorChatComposer.cs"));
        Assert.Contains("SessionVisibilityFilter.VisibleChatPickerThreads(threads, effectiveThread.Id)", chatRoot, StringComparison.Ordinal);
        Assert.Contains("thread.IsVisibleInSessionPicker(effectiveThread.Id)", chatRoot, StringComparison.Ordinal);
        Assert.Contains("inputs.CurrentThread.Title", chatComposer, StringComparison.Ordinal);
        Assert.Contains("thread.Title", chatComposer, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatThreadVisibility_HidesBackgroundButRetainsExplicitSelection()
    {
        var thread = new OpenClaw.Chat.ChatThread
        {
            Id = "agent:main:subagent:child",
            Title = "Research",
            IsBackground = true,
        };

        Assert.False(thread.IsVisibleInSessionPicker("agent:main:main"));
        Assert.True(thread.IsVisibleInSessionPicker(thread.Id));
    }

    [Fact]
    public void SessionsPage_UsesCanonicalCompactSessionPresentation()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml.cs"));

        Assert.Contains("SessionRunState.GetDisplaySortOrder(item.Session)", source, StringComparison.Ordinal);
        Assert.Contains("StatusText = ResolveStatusText(s)", source, StringComparison.Ordinal);
        Assert.Contains("SessionRunState.HasStoppedLastRun(s)", source, StringComparison.Ordinal);

        var xaml = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "SessionsPage.xaml"));
        Assert.Contains("Text=\"{Binding StatusText}\"", xaml, StringComparison.Ordinal);
    }
}
