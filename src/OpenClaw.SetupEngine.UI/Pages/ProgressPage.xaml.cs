using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using OpenClaw.Shared;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

internal sealed record ProgressPageArgs(
    SetupConfig Config,
    bool ShowMilestoneOnly,
    bool LocalAiRecoveryOnly,
    string DataDir,
    string LocalDataDir);

public sealed partial class ProgressPage : Page
{
    private SetupConfig? _config;
    private SetupPipeline? _pipeline;
    private SetupLogger? _logger;
    private CancellationTokenSource? _runCts;
    private readonly Dictionary<string, StepRow> _rows = new();
    private int _logLineCount;
    private bool _pipelineFinished;
    private string _dataDir = null!;
    private string _localDataDir = null!;
    private Uri? _tailscaleAuthorizationUri;
    private HashSet<string> _activeStepIds = [];
    private bool _localAiRecoveryOnly;
    private const int MaxLogLines = 200;

    internal bool IsPipelineRunning => _runCts != null && !_pipelineFinished;

    // Map pipeline step IDs to display groups (N:1)
    private static readonly (string GroupId, string DisplayName, string[] StepIds)[] StepGroups =
    [
        ("preflight", "Check compatibility", ["validate-distro-path", "preflight-os", "preflight-local-ai-hardware", "preflight-wsl", "preflight-windows-tailscale"]),
        ("wsl-platform", "Prepare WSL", ["ensure-wsl-platform"]),
        ("local-ai-engine", "Install Local AI", ["acquire-local-ai-runtime"]),
        ("local-ai-model", "Download AI model", ["acquire-local-ai-model"]),
        ("local-ai-verify", "Verify Local AI", ["persist-local-ai-manifest", "start-local-ai-runtime", "capture-local-ai-gpu-baseline", "verify-local-ai-inference", "verify-local-ai-gpu-load"]),
        ("wsl-networking", "Connect WSL to Local AI", ["configure-local-ai-wsl-networking"]),
        ("cleanup", "Remove existing gateway", ["cleanup-distro", "cleanup-gateway"]),
        ("port", "Check gateway port", ["preflight-port"]),
        ("wsl-create", "Install WSL gateway", ["wsl-create"]),
        ("wsl-configure", "Configure WSL", ["wsl-configure", "validate-wsl-lockdown"]),
        ("install-cli", "Install OpenClaw", ["install-cli"]),
        ("local-ai-wsl", "Verify Local AI access", ["verify-local-ai-wsl"]),
        ("tailscale-auth", "Connect Tailscale", ["install-tailscale", "authorize-tailscale"]),
        ("configure", "Configure gateway", ["configure-gateway", "configure-local-ai-gateway", "install-service"]),
        ("start", "Start gateway", ["start-gateway", "restart-gateway", "mint-token"]),
        ("tailscale-serve", "Publish with Tailscale", ["finalize-tailscale-serve"]),
        ("pairing", "Pair device", ["pair-operator", "pair-node", "verify-e2e"]),
        ("finish", "Finish setup", ["run-wizard", "start-keepalive"]),
    ];

    public ProgressPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => CancelPipeline();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as ProgressPageArgs;
        _config = args?.Config ?? e.Parameter as SetupConfig ?? new SetupConfig();
        _dataDir = args?.DataDir ?? SetupContext.ResolveDataDir();
        _localDataDir = args?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        _localAiRecoveryOnly = args?.LocalAiRecoveryOnly == true;
        _activeStepIds = BuildSteps(_config, _localAiRecoveryOnly)
            .Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        TitleText.Text = _config.LocalAi.Enabled ? "Setting up OpenClaw and Local AI" : "Setting up OpenClaw";
        SubtitleText.Text = _config.LocalAi.Enabled
            ? "Preparing the gateway and Local AI"
            : $"Creating {_config.DistroName} WSL instance";

        BuildStepRows();
        if (args?.ShowMilestoneOnly == true)
        {
            foreach (var (groupId, _, _) in StepGroups)
                if (_rows.TryGetValue(groupId, out var row))
                    row.SetStatus(StepStatus.Done);
            ShowGatewayInstalledMilestone();
            return;
        }

        if (SetupPreview.IsActive)
        {
            if (SetupPreview.RequestedPage == "milestone")
            {
                foreach (var (groupId, _, _) in StepGroups)
                    if (_rows.TryGetValue(groupId, out var row))
                        row.SetStatus(StepStatus.Done);
                ShowGatewayInstalledMilestone();
                return;
            }
            RenderProgressPreview();
            return;
        }
        StartPipeline();
    }

    private void RenderProgressPreview()
    {
        bool localAiPreview =
            _config?.LocalAi.Enabled == true ||
            SetupPreview.RequestedPage == "progress-local-ai";
        TitleText.Text = localAiPreview ? "Setting up OpenClaw and Local AI" : "Setting up OpenClaw";
        SubtitleText.Text = localAiPreview
            ? "Downloading the AI model. About 18 minutes left."
            : "Creating the WSL gateway. About 4 minutes left.";
        var ids = StepGroups.Select(g => g.GroupId).ToArray();
        int previewRunningIndex = Array.IndexOf(
            ids,
            localAiPreview ? "local-ai-model" : "wsl-create");
        for (int i = 0; i < ids.Length; i++)
        {
            var status = i < previewRunningIndex
                ? StepStatus.Done
                : i == previewRunningIndex ? StepStatus.Running : StepStatus.Idle;
            if (_rows.TryGetValue(ids[i], out var row))
                row.SetStatus(status);
        }
        if (localAiPreview && _rows.TryGetValue("local-ai-model", out var modelRow))
            modelRow.SetDetail("Downloading Qwen3.8-27B-UD-Q4_K_M.gguf", 6_322_405_376, 16_464_440_224, SetupDetailProgressUnit.Bytes);
        LogText.Text =
            "[12:04:01] [info] Windows 11 26100 · WSL 2 present\n" +
            "[12:04:03] [info] port 127.0.0.1:18789 available\n" +
            "[12:04:05] [info] wsl --install -d Ubuntu-24.04 --name OpenClawGateway --no-launch\n" +
            "[12:04:38] [info] downloading distro image (disk use varies)\n" +
            "[12:04:38] [changed] created %LOCALAPPDATA%\\OpenClawTray\\wsl\\OpenClawGateway\\\n" +
            "[12:04:38] [info] next: install CLI via HTTPS, configure loopback gateway\n";
    }

    private void BuildStepRows()
    {
        foreach (var (groupId, displayName, stepIds) in StepGroups)
        {
            var row = new StepRow(
                displayName,
                showDetailProgress: groupId is "local-ai-engine" or "local-ai-model");
            _rows[groupId] = row;
            StepsPanel.Children.Add(row.Element);
            if (!_activeStepIds.Overlaps(stepIds) ||
                (_config?.LocalAi.Enabled != true && IsLocalAiOnlyGroup(stepIds)))
                row.Element.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsLocalAiOnlyGroup(string[] stepIds) =>
        stepIds.All(stepId => stepId.Contains("local-ai", StringComparison.Ordinal));

    private void StartPipeline() =>
        AsyncEventHandlerGuard.Run(
            StartPipelineAsync,
            NullLogger.Instance,
            nameof(StartPipeline));

    private async Task StartPipelineAsync()
    {
        var config = _config!;
        if (_runCts != null)
            return;

        config.LogPath ??= Path.Combine(
            _dataDir, "Logs", "Setup", $"setup-engine-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        _runCts = cts;

        try
        {
            _logger = new SetupLogger(config.LogPath,
                Enum.TryParse<LogLevel>(config.LogLevel, true, out var lvl) ? lvl : LogLevel.Trace);

            _logger.LogEmitted += OnLogEmitted;

            var journalPath = Path.ChangeExtension(config.LogPath, ".journal.jsonl");
            using var journal = new TransactionJournal(journalPath);
            var commands = new CommandRunner(_logger);
            var ctx = new SetupContext(
                config,
                _logger,
                journal,
                commands,
                cts.Token,
                _dataDir,
                _localDataDir);
            ctx.ExternalAuthorizationPresenter = new ProgressAuthorizationPresenter(DispatcherQueue, ShowTailscaleAuthorization);
            ctx.DetailProgress = new DirectProgress<SetupDetailProgressEvent>(OnDetailProgress);

            var steps = BuildSteps(config, _localAiRecoveryOnly);
            _pipeline = new SetupPipeline(steps);
            _pipeline.StepProgress += OnStepProgress;

            var result = await Task.Run(() => _pipeline.RunAsync(ctx), cts.Token);
            sw.Stop();
            _pipelineFinished = true;

            var success = result.Outcome == PipelineOutcome.Success;
            if (success)
            {
                if (!config.SkipWizard)
                {
                    if (_rows.TryGetValue("finish", out var finishRow))
                        finishRow.SetStatus(StepStatus.Done);
                    // Pause on a "Gateway installed" milestone so the user knowingly steps
                    // from install (gateway provisioning) into onboarding (the OpenClaw wizard),
                    // instead of being thrown straight into the questions.
                    ShowGatewayInstalledMilestone();
                }
                else
                    // Permissions are now surfaced inline on the capabilities screen, so
                    // the standalone permissions step is skipped — go straight to done.
                    SetupWindow.Active?.NavigateToComplete(true, sw.Elapsed, config.LogPath);
            }
            else
            {
                var errorMsg = result.Outcome == PipelineOutcome.Cancelled
                    ? "Setup was cancelled."
                    : result.FailedStepId != null
                        ? $"Step '{result.FailedStepId}' failed: {result.Message}"
                        : result.Message;
                SetupWindow.Active?.NavigateToComplete(
                    false,
                    sw.Elapsed,
                    config.LogPath,
                    errorMsg,
                    result.CompatibilityFailure,
                    result.Detail,
                    restartRequired: result.RequiresRestart);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            _pipelineFinished = true;
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, "Setup was cancelled.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _pipelineFinished = true;
            _logger?.Error($"Setup UI pipeline failed: {ex.Message}");
            SetupWindow.Active?.NavigateToComplete(false, sw.Elapsed, config.LogPath, $"Setup crashed: {ex.Message}");
        }
        finally
        {
            if (_logger != null)
                _logger.LogEmitted -= OnLogEmitted;
            if (_pipeline != null)
                _pipeline.StepProgress -= OnStepProgress;
            _logger?.Dispose();
            _logger = null;
            _pipeline = null;
            if (ReferenceEquals(_runCts, cts))
                _runCts = null;
        }
    }

    private void CancelPipeline()
    {
        if (!_pipelineFinished)
            _runCts?.Cancel();
    }

    private void OnStepProgress(object? sender, StepProgressEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Find which group this step belongs to
            var groupIndex = Array.FindIndex(StepGroups, g => g.StepIds.Contains(e.StepId));
            if (groupIndex < 0) return;

            var group = StepGroups[groupIndex];
            var row = _rows[group.GroupId];

            if (e.Outcome == null)
            {
                // Step started — mark all previous groups as done if still running
                for (int i = 0; i < groupIndex; i++)
                {
                    var prevRow = _rows[StepGroups[i].GroupId];
                    if (prevRow.Status == StepStatus.Running)
                        prevRow.SetStatus(StepStatus.Done);
                }

                // Mark this group as running
                if (row.Status != StepStatus.Done)
                    row.SetStatus(StepStatus.Running);
            }
            else if (e.Outcome == StepOutcome.Failed || e.Outcome == StepOutcome.FailedTerminal)
            {
                row.SetStatus(StepStatus.Failed);
            }
            else
            {
                // Step succeeded/skipped — track it
                _completedSteps.Add(e.StepId);

                // If all steps in this group are done, mark group done
                if (group.StepIds.Where(_activeStepIds.Contains).All(id => _completedSteps.Contains(id)))
                    row.SetStatus(StepStatus.Done);
            }
        });
    }

    private readonly HashSet<string> _completedSteps = new();

    private void OnDetailProgress(SetupDetailProgressEvent progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var group = StepGroups.FirstOrDefault(candidate => candidate.StepIds.Contains(progress.StepId));
            if (string.IsNullOrWhiteSpace(group.GroupId) || !_rows.TryGetValue(group.GroupId, out var row))
                return;
            row.SetDetail(progress.Detail, progress.Completed, progress.Total, progress.Unit);
        });
    }

    private void OnLogEmitted(object? sender, LogEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}\n";
            _logLineCount++;
            if (_logLineCount > MaxLogLines)
            {
                // Trim old lines (simple: just keep appending; reset periodically)
                if (_logLineCount % MaxLogLines == 0)
                    LogText.Text = line;
                else
                    LogText.Text += line;
            }
            else
            {
                LogText.Text += line;
            }

            // Auto-scroll
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        LogFileLauncher.RevealInExplorer(_config?.LogPath);
    }

    private void ShowTailscaleAuthorization(ExternalAuthorizationRequest request)
    {
        _tailscaleAuthorizationUri = request.AuthorizationUri;
        TailscaleAuthorizationText.Text = request.Message;
        TailscaleAuthorizationPanel.Visibility = Visibility.Visible;
        _ = global::Windows.System.Launcher.LaunchUriAsync(request.AuthorizationUri);
    }

    private void TailscaleAuthorization_Click(object sender, RoutedEventArgs e)
    {
        if (_tailscaleAuthorizationUri is not null)
            _ = global::Windows.System.Launcher.LaunchUriAsync(_tailscaleAuthorizationUri);
    }

    // Swap the install UI for a "Gateway installed" milestone with an explicit
    // onboard CTA. The gateway keeps running (WSL keepalive), so the wizard
    // connects when the user chooses to continue.
    private void ShowGatewayInstalledMilestone()
    {
        InstallHeader.Visibility = Visibility.Collapsed;
        InstallContent.Visibility = Visibility.Collapsed;
        MilestonePanel.Visibility = Visibility.Visible;
        OnboardButton.Visibility = Visibility.Visible;
    }

    private void Onboard_Click(object sender, RoutedEventArgs e)
    {
        if (SetupWindow.Active?.TryNavigateToWizard() == true)
            return;

        MilestoneStatusText.Text = "Another setup task is still active. Wait for it to finish, then start OpenClaw onboard.";
    }

    private static List<SetupStep> BuildSteps(SetupConfig config, bool localAiRecoveryOnly = false)
        => (localAiRecoveryOnly
                ? SetupStepFactory.BuildLocalAiRecoverySteps()
                : SetupStepFactory.BuildDefaultSteps())
            .Where(step => step is not RunGatewayWizardStep)
            .Where(step => config.SkipWizard || step is not WindowsNodeBootstrapContextStep)
            .ToList();
}

internal sealed class ProgressAuthorizationPresenter(
    DispatcherQueue dispatcherQueue,
    Action<ExternalAuthorizationRequest> present) : IExternalAuthorizationPresenter
{
    public Task PresentAsync(ExternalAuthorizationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcherQueue.TryEnqueue(() => present(request)))
            throw new InvalidOperationException("Setup UI closed before the Tailscale authorization link could be shown.");
        return Task.CompletedTask;
    }
}

internal sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(T value) => _report(value);
}

// ─── Step Row UI Element ───

internal enum StepStatus { Idle, Running, Done, Failed }

internal sealed class StepRow
{
    public FrameworkElement Element { get; }
    public StepStatus Status { get; private set; }

    private readonly TextBlock _label;
    private readonly TextBlock _detail;
    private readonly ProgressBar _detailProgress;
    private readonly ProgressRing _spinner;
    private readonly Border _idleBadge;
    private readonly Border _checkBadge;
    private readonly Border _errorBadge;
    private readonly Border _rowBorder;

    public StepRow(string displayName, bool showDetailProgress = false)
    {
        _label = new TextBlock
        {
            Text = displayName,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _detail = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        _detailProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 4,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // Bare Windows spinner (no filled disc) — theme-neutral so it reads white
        // on the dark active row and dark on light, like a standard ProgressRing.
        _spinner = new ProgressRing
        {
            Width = 20, Height = 20,
            MinWidth = 20, MinHeight = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var spinnerFg) && spinnerFg is Brush spinnerBrush)
            _spinner.Foreground = spinnerBrush;

        _idleBadge = CreateEmptyBadge();

        _checkBadge = CreateIconBadge("\uE73E", ResolveColor("SystemFillColorSuccess", Color.FromArgb(255, 0x2B, 0xC3, 0x6F)), Colors.White);
        _checkBadge.Visibility = Visibility.Collapsed;

        _errorBadge = CreateIconBadge("\uE711", ResolveColor("SystemFillColorCritical", Color.FromArgb(255, 0xE8, 0x11, 0x23)), Colors.White);
        _errorBadge.Visibility = Visibility.Collapsed;

        var badgeContainer = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeContainer.Children.Add(_idleBadge);
        badgeContainer.Children.Add(_spinner);
        badgeContainer.Children.Add(_checkBadge);
        badgeContainer.Children.Add(_errorBadge);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        var textStack = new StackPanel { Spacing = 1 };
        textStack.Children.Add(_label);
        if (showDetailProgress)
        {
            textStack.Children.Add(_detail);
            textStack.Children.Add(_detailProgress);
        }
        Grid.SetColumn(textStack, 0);
        Grid.SetColumn(badgeContainer, 1);
        grid.Children.Add(textStack);
        grid.Children.Add(badgeContainer);

        _rowBorder = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 5, 12, 5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        Element = _rowBorder;
    }

    public void SetStatus(StepStatus status)
    {
        Status = status;
        _spinner.IsActive = status == StepStatus.Running;
        _spinner.Visibility = status == StepStatus.Running ? Visibility.Visible : Visibility.Collapsed;
        _idleBadge.Visibility = status == StepStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        _checkBadge.Visibility = status == StepStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        _errorBadge.Visibility = status == StepStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        _label.Opacity = status == StepStatus.Idle ? 0.72 : 1.0;
        _label.FontWeight = status == StepStatus.Running
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        // Highlight the active step with the setup accent while it is running.
        if (status == StepStatus.Running
            && Application.Current.Resources.TryGetValue("SetupIndicatorAccentBrush", out var accent)
            && accent is SolidColorBrush accentBrush)
        {
            var c = accentBrush.Color;
            _rowBorder.Background = new SolidColorBrush(Color.FromArgb(28, c.R, c.G, c.B));
            _rowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B));
        }
        else
        {
            _rowBorder.Background = new SolidColorBrush(Colors.Transparent);
            _rowBorder.BorderBrush = new SolidColorBrush(Colors.Transparent);
        }
    }

    public void SetDetail(
        string detail,
        long completed,
        long? total,
        SetupDetailProgressUnit unit)
    {
        string measurement = unit switch
        {
            SetupDetailProgressUnit.Bytes when total is > 0 =>
                $"{FormatBytes(completed)} of {FormatBytes(total.Value)}",
            SetupDetailProgressUnit.Items when total is > 0 => $"{completed} of {total.Value}",
            _ => string.Empty,
        };
        _detail.Text = string.IsNullOrWhiteSpace(measurement) ? detail : $"{detail}  {measurement}";
        _detail.Visibility = Visibility.Visible;
        if (total is > 0)
        {
            _detailProgress.Value = Math.Clamp((double)completed / total.Value, 0, 1);
            _detailProgress.Visibility = Visibility.Visible;
        }
        else
        {
            _detailProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000d:0.0} GB"
            : $"{bytes / 1_000_000d:0} MB";

    private static Border CreateEmptyBadge()
    {
        // Use a theme-aware stroke so the pending-step ring stays visible in every theme.
        var border = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };

        if (Application.Current.Resources.TryGetValue("ControlStrongStrokeColorDefaultBrush", out var brush)
            && brush is Brush themed)
        {
            border.BorderBrush = themed;
        }
        else
        {
            border.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
        }

        return border;
    }

    private static Border CreateIconBadge(string glyph, Color background, Color foreground)
    {
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(background),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 11,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                Foreground = new SolidColorBrush(foreground),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
    }

    // Resolve a native Color theme resource (e.g. SystemFillColorSuccess) with a fallback.
    private static Color ResolveColor(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;
}
