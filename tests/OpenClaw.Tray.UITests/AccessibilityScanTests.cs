using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Automation;
using Axe.Windows.Core.Enums;
using Xunit;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AccessibilityCollection : ICollectionFixture<AccessibilityAppFixture>
{
    public const string Name = "Accessibility app";
}

/// <summary>
/// Scans every native Hub page in the real OpenClaw process. The test process
/// remains separate because Axe.Windows drives the target through UI Automation.
/// </summary>
[Collection(AccessibilityCollection.Name)]
public sealed class AccessibilityScanTests
{
    private readonly AccessibilityAppFixture _app;

    public AccessibilityScanTests(AccessibilityAppFixture app)
    {
        _app = app;
    }

    private static readonly IReadOnlyDictionary<string, RuleId[]> PageRuleExclusions =
        new Dictionary<string, RuleId[]>
        {
            // The public GridSplitter has an app-supplied localized name/type. Its
            // CommunityToolkit SizerBase child peer still reports both types as
            // "custom" and cannot be configured by the consuming XAML page.
            ["ConfigPage"] = [RuleId.LocalizedControlTypeNotCustom],
        };

    public static IEnumerable<object[]> PageTestData()
    {
        yield return ["AgentEventsPage", "agentevents", "AgentEventsPageMarker"];
        yield return ["BindingsPage", "bindings", "BindingsPageMarker"];
        yield return ["ChannelsPage", "channels", "ChannelsPageMarker"];
        yield return ["ChatPage", "chat", "ChatComposerInput"];
        yield return ["ConfigPage", "config", "ConfigPageMarker"];
        yield return ["ConnectionPage", "connection", "ConnectionPageMarker"];
        yield return ["CronPage", "cron", "CronPageMarker"];
        yield return ["DebugPage", "debug", "DebugPageMarker"];
        yield return ["InstancesPage", "instances", "InstancesPageMarker"];
        yield return ["NotificationsPage", "notifications", "NotificationsPageMarker"];
        yield return ["PermissionsPage", "permissions", "PermissionsPageMarker"];
        yield return ["SandboxPage", "sandbox", "SandboxPageMarker"];
        yield return ["SessionsPage", "sessions", "SessionsPageMarker"];
        yield return ["SettingsPage", "settings", "SettingsPageMarker"];
        yield return ["ExtensionsPage", "extensions", "ExtensionsPageMarker"];
        yield return ["UsagePage", "usage", "UsagePageMarker"];
        yield return ["VoiceSettingsPage", "voice", "VoiceSettingsPageMarker"];
        yield return ["WorkspacePage", "workspace", "WorkspacePageMarker"];
    }

    [Theory]
    [Trait("Category", "Accessibility")]
    [MemberData(nameof(PageTestData))]
    public async Task Page_PassesAccessibilityScan(
        string pageName,
        string pageTag,
        string pageMarkerAutomationId)
    {
        await _app.NavigateAsync(
            pageTag,
            pageName,
            pageMarkerAutomationId);
        PageRuleExclusions.TryGetValue(pageName, out var exclusions);
        AxeHelper.AssertNoAccessibilityErrors(
            _app.HubWindowHandle,
            exclusions,
            context: pageName);
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task ChatComposerControls_ExposeOnscreenLayoutThroughUia()
    {
        await _app.NavigateAsync(
            "chat",
            "ChatPage",
            "ChatComposerInput");
        var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
        foreach (var automationId in new[]
        {
            "ChatComposerInput",
            "ChatComposerAttach",
            "ChatComposerSpeakerToggle",
        })
        {
            var element = await WaitForOnscreenLayoutAsync(hub, automationId);
            Assert.False(element.Current.IsOffscreen);
            Assert.True(element.Current.BoundingRectangle.Width > 0);
            Assert.True(element.Current.BoundingRectangle.Height > 0);
        }
    }

    private static async Task<AutomationElement> WaitForOnscreenLayoutAsync(
        AutomationElement hub,
        string automationId)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            var element = hub.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
            if (element is not null)
            {
                var bounds = element.Current.BoundingRectangle;
                if (!element.Current.IsOffscreen
                    && bounds.Width > 0
                    && bounds.Height > 0)
                {
                    return element;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Composer control '{automationId}' did not expose onscreen nonzero UIA bounds.");
    }
}
