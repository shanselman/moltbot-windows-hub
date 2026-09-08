using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class WindowManagerTests
{
    [Theory]
    [InlineData((int)CanvasSurfaceDestination.Capabilities, "capabilities")]
    [InlineData((int)CanvasSurfaceDestination.Connection, "connection")]
    public void CanvasRequest_PreservesCapabilityAndPairingFallbacks(
        int destinationValue,
        string expectedRoute)
    {
        var destination = (CanvasSurfaceDestination)destinationValue;
        var routes = new List<string>();
        var canvasShows = 0;
        var request = new CanvasWindowRequest(destination, () => canvasShows++);

        request.Dispatch(routes.Add);

        Assert.Equal([expectedRoute], routes);
        Assert.Equal(0, canvasShows);
    }

    [Fact]
    public void CanvasRequest_PairedDestinationDelegatesExactlyOnce()
    {
        var routes = new List<string>();
        var canvasShows = 0;
        var request = new CanvasWindowRequest(
            CanvasSurfaceDestination.Canvas,
            () => canvasShows++);

        request.Dispatch(routes.Add);

        Assert.Empty(routes);
        Assert.Equal(1, canvasShows);
    }

    [Fact]
    public void ChatRequest_RejectsMissingCredentialsAndRetainsValues()
    {
        var request = new ChatWindowRequest("ws://127.0.0.1:18789", "token");

        Assert.Equal("ws://127.0.0.1:18789", request.GatewayUrl);
        Assert.Equal("token", request.GatewayToken);
        Assert.Throws<ArgumentException>(() => new ChatWindowRequest("", "token"));
        Assert.Throws<ArgumentException>(() => new ChatWindowRequest("ws://127.0.0.1:18789", ""));
    }

    [Fact]
    public void LocalAiSetup_ChoosesRecoveryOnlyAfterManagedGatewayProof()
    {
        var manager = ReadManager();

        Assert.Contains("public async Task ShowLocalAiSetupAsync()", manager);
        Assert.Contains("LocalAiGatewayDistroResolver.FindOwners(", manager);
        Assert.Contains("ExistingConfigDetector.Detect(", manager);
        Assert.Contains("LocalAiSetupRoutePolicy.Decide(", manager);
        AssertInOrder(
            manager,
            "if (resolution.Route == LocalAiSetupRoute.Provision)",
            "await ShowOnboardingAsync();",
            "if (resolution.Route == LocalAiSetupRoute.Blocked",
            "await ShowLocalAiSetupRecoveryAsync(");
    }

    [Fact]
    public void LocalAiRecoveryMode_IsNotAppliedToAnExistingSetupWindow()
    {
        var manager = ReadManager();
        var setupWindow = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine.UI",
            "SetupWindow.xaml.cs"));

        Assert.DoesNotContain("TryNavigateToOnboardingStart", manager);
        Assert.DoesNotContain("TryNavigateToLocalAiRecoveryReview", manager);
        Assert.Contains("private void ResetLocalAiRecoveryMode()", setupWindow);
        AssertInOrder(
            setupWindow,
            "public void NavigateToWelcome(bool back = false)",
            "ResetLocalAiRecoveryMode();",
            "NavigateTo(typeof(WelcomePage), _config, back);");
        Assert.Contains("_config.LocalAi.Enabled = true;", setupWindow);
        Assert.Contains("_config.SkipWizard = true;", setupWindow);

        var capabilities = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.SetupEngine.UI",
            "Pages",
            "CapabilitiesPage.xaml.cs"));
        AssertInOrder(
            capabilities,
            "private void Back_Click(object sender, RoutedEventArgs e)",
            "if (_localAiRecoveryOnly)",
            "SetupWindow.Active?.NavigateToWelcome(back: true);",
            "return;");
    }

    [Fact]
    public void CloseForShutdown_GatesCreationAndClosesOwnedWindowsOnce()
    {
        var manager = ReadManager();

        Assert.Contains("public void BeginShutdown() => _isShuttingDown = true;", manager);
        Assert.Contains("return _closeForShutdownTask ??= CloseOwnedWindowsAsync();", manager);
        Assert.Contains("if (_isShuttingDown)", manager);
        Assert.Equal(2, Count(manager, "ResetNavigationScope();"));
        AssertInOrder(
            manager,
            "hub.Closed -= OnHubClosed;",
            "TryClose(\"Hub window\", hub.Close, ref failures);",
            "_hubWindow = null;",
            "ResetNavigationScope();");
        AssertInOrder(
            manager,
            "setupWindow.Closed -= OnSetupClosed;",
            "if (!setupWindow.IsClosed)",
            "setupWindow.Close();",
            "if (setupWindow.IsClosed)",
            "await setupWindow.CleanupCompleted;",
            "_setupWindow = null;");
    }

    [Fact]
    public void WindowLifetimes_PreserveReuseNoFocusThemeHandlesAndCleanup()
    {
        var manager = ReadManager();

        Assert.Contains("if (_hubWindow is null || _hubWindow.IsClosed)", manager);
        Assert.Contains("Show(activateWindow: false)", manager);
        Assert.Contains("DispatcherQueuePriority.Low", manager);
        Assert.Contains("_chatWindow.HideNearTray()", manager);
        Assert.Contains("window.ShowNearTrayAnimated()", manager);
        Assert.Contains("window.Closed -= OnConnectionStatusClosed", manager);
        Assert.Contains("await existingSetupWindow.CleanupCompleted", manager);
        Assert.Contains("_callbacks.ApplyTheme(_keepAliveWindow)", manager);
        Assert.Contains("_callbacks.ApplyTheme(_setupWindow)", manager);
        Assert.Contains("_hubWindow is { IsClosed: false } hub", manager);
        Assert.Contains("(hub.Content as FrameworkElement)?.XamlRoot", manager);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(_hubWindow)", manager);
        Assert.Contains("WinRT.Interop.WindowNative.GetWindowHandle(_setupWindow)", manager);
    }

    private static string ReadManager() => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(),
        "src",
        "OpenClaw.Tray.WinUI",
        "Services",
        "WindowManager.cs"));

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previous = -1;
        foreach (var fragment in fragments)
        {
            var current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected to find '{fragment}'.");
            previous = current;
        }
    }
}
