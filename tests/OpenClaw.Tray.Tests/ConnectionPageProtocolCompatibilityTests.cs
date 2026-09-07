using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Pages;
using OpenClawTray.Services;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class ConnectionPageProtocolCompatibilityTests
{
    [Theory]
    [InlineData(
        GatewayProtocolCompatibilityState.GatewayTooOld,
        2,
        "ConnectionPage_ProtocolGatewayUpdateRequired",
        "ConnectionPage_ProtocolGatewayUpdateDetail")]
    [InlineData(
        GatewayProtocolCompatibilityState.GatewayTooNew,
        5,
        "ConnectionPage_ProtocolWindowsUpdateRequired",
        "ConnectionPage_ProtocolWindowsUpdateDetail")]
    [InlineData(
        GatewayProtocolCompatibilityState.Mismatch,
        null,
        "ConnectionPage_ProtocolUnknownMismatch",
        null)]
    public void ProtocolMismatch_ProjectsDirectionalLocalizedRecoveryWithManualActions(
        GatewayProtocolCompatibilityState state,
        int? expectedProtocol,
        string expectedHeaderKey,
        string? expectedDetailKey)
    {
        var compatibility = new GatewayProtocolCompatibility
        {
            State = state,
            GatewayExpectedProtocol = expectedProtocol,
            Retryable = false
        };
        var snapshot = new GatewayConnectionSnapshot
        {
            OverallState = OverallConnectionState.Error,
            OperatorState = RoleConnectionState.Error,
            OperatorErrorKind = GatewayErrorKind.ProtocolMismatch,
            OperatorProtocolCompatibility = compatibility,
            ProtocolCompatibility = compatibility,
            ProtocolCompatibilityRole = GatewayProtocolCompatibilityRole.Operator,
            GatewayId = "gw-1",
            GatewayUrl = "wss://gateway.example"
        };

        var plan = ConnectionPagePlan.Build(
            snapshot,
            new GatewayRecord { Id = "gw-1", Url = "wss://gateway.example" },
            self: null,
            settings: null,
            savedGatewayCount: 1);

        Assert.Equal(ConnectionPageMode.Recovery, plan.Mode);
        Assert.Equal(RecoveryCategory.ProtocolMismatch, plan.Recovery);
        Assert.Equal(expectedHeaderKey, plan.StripHeadlineResourceKey);
        Assert.Equal(expectedDetailKey, plan.StripSubResourceKey);
        Assert.Equal(expectedHeaderKey, plan.RecoveryHeaderResourceKey);
        Assert.Equal(
            expectedDetailKey is null ? [] : [expectedDetailKey],
            plan.RecoveryBulletResourceKeys);
        Assert.Equal(expectedProtocol, plan.ProtocolExpectedVersion);
        Assert.Equal(3, plan.ProtocolMinimumVersion);
        Assert.Equal(4, plan.ProtocolMaximumVersion);
        Assert.Equal(4, plan.ProtocolCurrentVersion);
        Assert.Equal(ConnectionPrimaryAction.None, plan.StripPrimaryAction);
        Assert.Null(plan.StripPrimaryLabel);
        Assert.True(plan.AllowConnectionToggle);
    }

    [Fact]
    public void NodeOnlyProtocolMismatch_PreservesConnectedOperatorWithoutRetry()
    {
        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "OpenClawTrayTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsManager(settingsDirectory)
            {
                EnableNodeMode = true
            };
            var compatibility = new GatewayProtocolCompatibility
            {
                State = GatewayProtocolCompatibilityState.GatewayTooNew,
                GatewayExpectedProtocol = 5,
                Retryable = false
            };
            var snapshot = new GatewayConnectionSnapshot
            {
                OverallState = OverallConnectionState.Degraded,
                OperatorState = RoleConnectionState.Connected,
                NodeConnectionIntended = true,
                NodeState = RoleConnectionState.Error,
                NodeErrorKind = GatewayErrorKind.ProtocolMismatch,
                OperatorProtocolCompatibility = GatewayProtocolCompatibility.Compatible(4),
                NodeProtocolCompatibility = compatibility,
                ProtocolCompatibility = compatibility,
                ProtocolCompatibilityRole = GatewayProtocolCompatibilityRole.Node,
                GatewayId = "gw-1",
                GatewayUrl = "wss://gateway.example"
            };

            var plan = ConnectionPagePlan.Build(
                snapshot,
                new GatewayRecord { Id = "gw-1", Url = "wss://gateway.example" },
                self: null,
                settings,
                savedGatewayCount: 1);

            Assert.Equal(ConnectionPageMode.Cockpit, plan.Mode);
            Assert.Equal(OperatorCardState.Active, plan.OperatorCard);
            Assert.Equal(NodeCardState.OnNodeError, plan.NodeCard);
            Assert.Equal(
                "ConnectionPage_ProtocolWindowsUpdateRequired",
                plan.StripHeadlineResourceKey);
            Assert.Equal(
                "ConnectionPage_ProtocolWindowsUpdateDetail",
                plan.StripSubResourceKey);
            Assert.Equal(
                "ConnectionPage_ProtocolWindowsUpdateDetail",
                plan.NodeErrorDetailResourceKey);
            Assert.Equal(5, plan.ProtocolExpectedVersion);
            Assert.Equal(ConnectionPrimaryAction.None, plan.StripPrimaryAction);
            Assert.Null(plan.StripPrimaryLabel);
            Assert.True(plan.AllowConnectionToggle);
        }
        finally
        {
            if (Directory.Exists(settingsDirectory))
                Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConnectionPageApplicator_UsesPlanToShowManualRecoveryActions()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Pages",
            "ConnectionPage.xaml.cs"));

        Assert.Contains("x:Name=\"RecoveryConnectionActions\"", xaml);
        Assert.Contains(
            "RecoveryConnectionActions.Visibility = plan.AllowConnectionToggle",
            codeBehind);
        Assert.Contains(
            "_currentPlan?.AllowConnectionToggle ?? true",
            codeBehind);
        Assert.Contains("plan.ProtocolMinimumVersion", codeBehind);
        Assert.Contains("plan.ProtocolMaximumVersion", codeBehind);
        Assert.Contains("plan.ProtocolCurrentVersion", codeBehind);
    }

    [Fact]
    public void LocalizedProtocolCopy_UsesExpectedRangeAndCurrentPlaceholders()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        foreach (var locale in new[] { "en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw", "pt-br" })
        {
            var document = XDocument.Load(Path.Combine(
                root,
                "src",
                "OpenClaw.Tray.WinUI",
                "Strings",
                locale,
                "Resources.resw"));
            var gatewayUpdate = GetResourceValue(
                document,
                "ConnectionPage_ProtocolGatewayUpdateDetail");
            var windowsUpdate = GetResourceValue(
                document,
                "ConnectionPage_ProtocolWindowsUpdateDetail");

            Assert.Contains("{0}", gatewayUpdate);
            Assert.Contains("{1}", gatewayUpdate);
            Assert.Contains("{2}", gatewayUpdate);
            Assert.Contains("{3}", gatewayUpdate);
            Assert.DoesNotContain("v4", gatewayUpdate, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{0}", windowsUpdate);
            Assert.Contains("{1}", windowsUpdate);
            Assert.Contains("{2}", windowsUpdate);
            Assert.Contains("{3}", windowsUpdate);
            Assert.DoesNotContain("v4", windowsUpdate, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string GetResourceValue(XDocument document, string key) =>
        document.Root!
            .Elements("data")
            .Single(element => string.Equals(
                (string?)element.Attribute("name"),
                key,
                StringComparison.Ordinal))
            .Element("value")!
            .Value;
}
