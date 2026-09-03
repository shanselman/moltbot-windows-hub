using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CompletePage : Page
{
    private static readonly Regex s_urlRegex = new(@"https?://[^\s)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly WindowsRestartLauncher s_windowsRestartLauncher = new();
    private string? _logPath;
    private string? _serverLogDirectory;

    public CompletePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is CompletePageArgs args)
        {
            _logPath = args.LogPath;

            if (args.Success)
            {
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
                StartupToggle.IsOn = args.DefaultAutoStart;
                StartupRow.Visibility = args.ShowStartupPreference ? Visibility.Visible : Visibility.Collapsed;
                SetupReviewSummary review = args.ReviewSummary ?? SetupReviewSummaryBuilder.Build(new SetupConfig());
                GatewaySummaryText.Text = review.CompletionGatewaySummary;
                TitleText.Text = "All set!";
                SubtitleText.Text = "OpenClaw is ready to go";
                SubtitleText.Visibility = Visibility.Visible;
                ErrorCard.Visibility = Visibility.Collapsed;
                HelpLink.Visibility = Visibility.Collapsed;
                SummaryPanel.Visibility = Visibility.Visible;
                LocalAiSummaryCard.Visibility = review.LocalAiEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (review.LocalAiEnabled)
                {
                    LocalAiSummaryTitle.Text = review.LocalAiTitle ?? "Local AI verified";
                    LocalAiSummaryDescription.Text = review.LocalAiDescription ??
                        "The native llama-server router is ready. The model loads on the first request.";
                    SubtitleText.Text = "OpenClaw and Local AI are ready";
                    LaunchButton.Content = "Open chat";
                }
            }
            else
            {
                var errorMessage = args.ErrorMessage ?? "Unknown error";

                if (args.RequiresRestart)
                {
                    SuccessIcon.Visibility = Visibility.Collapsed;
                    FailureIcon.Visibility = Visibility.Collapsed;
                    RestartIcon.Visibility = Visibility.Visible;
                    TitleText.Text = "Restart required";
                    SubtitleText.Text = "OpenClaw needs to restart Windows to continue the installation. Would you like to restart now?";
                    SubtitleText.TextWrapping = TextWrapping.Wrap;
                    SubtitleText.TextAlignment = TextAlignment.Center;
                    SubtitleText.Visibility = Visibility.Visible;
                    NodeModeBanner.Visibility = Visibility.Collapsed;
                    StartupRow.Visibility = Visibility.Collapsed;
                    SummaryPanel.Visibility = Visibility.Collapsed;
                    LocalAiSummaryCard.Visibility = Visibility.Collapsed;
                    ErrorCard.Visibility = Visibility.Collapsed;
                    HelpLink.Visibility = Visibility.Collapsed;
                    LaunchButton.Visibility = Visibility.Collapsed;
                    StepIndicator.Visibility = Visibility.Collapsed;
                    RestartLaterButton.Visibility = Visibility.Visible;
                    RestartNowButton.Visibility = Visibility.Visible;
                    return;
                }

                // Local AI failures (identified by Detail) carry llama-server's own error text in
                // errorMessage. That text is diagnostic evidence, not a curated OpenClaw message,
                // so it must never be scanned for a URL to turn into a clickable help link.
                var helpUrl = args.Detail is null ? ExtractHelpUrl(errorMessage) : null;

                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;
                RestartIcon.Visibility = Visibility.Collapsed;
                TitleText.Text = "Setup failed";
                SubtitleText.Visibility = Visibility.Collapsed;
                NodeModeBanner.Visibility = Visibility.Collapsed;
                StartupRow.Visibility = Visibility.Collapsed;
                SummaryPanel.Visibility = Visibility.Collapsed;
                LocalAiSummaryCard.Visibility = Visibility.Collapsed;
                LaunchButton.Content = "Close";
                // Show error card with details and log link
                ErrorCard.Visibility = Visibility.Visible;
                ErrorText.Text = errorMessage;
                if (helpUrl != null)
                {
                    HelpLink.Content = errorMessage.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                        ? "Update WSL →"
                        : "Open help link →";
                    HelpLink.NavigateUri = helpUrl;
                    HelpLink.Visibility = Visibility.Visible;
                }
                else
                {
                    HelpLink.Visibility = Visibility.Collapsed;
                }
                if (args.LogPath != null)
                {
                    var displayPath = LogFileLauncher.ResolveRealPath(args.LogPath);
                    ViewLogLink.Content = $"View full log → {displayPath}";
                    ToolTipService.SetToolTip(ViewLogLink, displayPath);
                    ViewLogLink.Visibility = Visibility.Visible;
                }
                else
                    ViewLogLink.Visibility = Visibility.Collapsed;

                // llama-server reports the real cause in its own logs, which live outside the
                // setup log directory above, so surface both the lines and where to find them.
                ShowServerDiagnostics(args.Detail);
            }
        }
    }

    private void ShowServerDiagnostics(LocalAiFailureDetail? detail)
    {
        if (detail is null)
        {
            ServerDiagnosticsText.Visibility = Visibility.Collapsed;
            ViewServerLogLink.Visibility = Visibility.Collapsed;
            return;
        }

        if (detail.Diagnostics.Count > 0)
        {
            ServerDiagnosticsText.Text = string.Join(
                Environment.NewLine,
                detail.Diagnostics.Select(line => $"llama-server: {line}"));
            ServerDiagnosticsText.Visibility = Visibility.Visible;
        }
        else
            ServerDiagnosticsText.Visibility = Visibility.Collapsed;

        _serverLogDirectory = detail.LogDirectory;
        var displayDirectory = LogFileLauncher.ResolveRealPath(detail.LogDirectory);
        // The directory may not exist if setup failed before log initialization or if another
        // cleanup removed it. The link must not promise a folder that is unavailable.
        if (Directory.Exists(displayDirectory))
        {
            ViewServerLogLink.Content = $"Open Local AI logs → {displayDirectory}";
            ToolTipService.SetToolTip(ViewServerLogLink, displayDirectory);
            ViewServerLogLink.Visibility = Visibility.Visible;
        }
        else
            ViewServerLogLink.Visibility = Visibility.Collapsed;
    }

    private void ViewServerLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_serverLogDirectory);
    }

    private static Uri? ExtractHelpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = s_urlRegex.Match(text);
        if (!match.Success)
            return null;

        return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchButton.Content?.ToString() != "Close")
        {
            var enableAutoStart = StartupRow.Visibility == Visibility.Visible && StartupToggle.IsOn;
            if (SetupWindow.Active?.RequestSetupCompleted(enableAutoStart) == true)
                return;
        }

        SetupWindow.Active?.Close();
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_logPath);
    }

    private void RestartLaterButton_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.Close();
    }

    private void RestartNowButton_Click(object sender, RoutedEventArgs e)
    {
        AsyncEventHandlerGuard.Run(
            RestartWindowsAsync,
            NullLogger.Instance,
            nameof(RestartNowButton_Click),
            ShowRestartError);
    }

    private async Task RestartWindowsAsync()
    {
        RestartNowButton.IsEnabled = false;
        RestartLaterButton.IsEnabled = false;
        SubtitleText.Text = "Windows is restarting...";

        await s_windowsRestartLauncher.RestartAsync();
    }

    private void ShowRestartError(Exception ex)
    {
        ErrorText.Text = $"Windows could not be restarted: {ex.Message}";
        ErrorCard.Visibility = Visibility.Visible;
        ViewLogLink.Visibility = Visibility.Collapsed;
        RestartNowButton.IsEnabled = true;
        RestartLaterButton.IsEnabled = true;
        SubtitleText.Text = "OpenClaw needs to restart Windows to continue the installation. Would you like to restart now?";
    }

}
