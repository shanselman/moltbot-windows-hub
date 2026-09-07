using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Windows;

namespace OpenClawTray.Services;

internal interface IWindowManager
{
    Window? ActiveHubWindow { get; }
    bool IsHubOpen { get; }
    bool IsChatVisible { get; }
    XamlRoot? DialogXamlRoot { get; }
    XamlRoot? RuntimeAnchorXamlRoot { get; }
    XamlRoot? SetupXamlRoot { get; }

    bool CanNavigateHubBack();
    void NavigateHubBack();
    void InitializeRuntimeAnchor();
    void BeginShutdown();
    void PrewarmChat(ChatWindowRequest request);
    void ShowChat(ChatWindowRequest request);
    void ResetChatForCredentialChange();
    void ShowCanvas(CanvasWindowRequest request);
    void ShowHub(string? navigateTo = null, bool activate = true);
    void ShowConnectionStatus();
    Task ShowOnboardingAsync();
    Task ShowLocalAiSetupAsync();
    Task ShowGatewayWizardAsync();
    void CloseSetup();
    void ApplyThemeToOpenWindows();
    void UpdateHubTitleBarStatus(GatewayConnectionSnapshot snapshot, ConnectionStatus status);
    void RefreshHubDiagnosticsNavigationVisibility();
    void SetPendingChatSessionKey(string? sessionKey);
    void ShowHubChatAndStartVoice();
    IntPtr GetHubWindowHandle();
    IntPtr GetOnboardingWindowHandle();
    Task CloseForShutdownAsync();
}
