using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.SetupEngine.UI;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WizardPage : Page
{
    private const int MaxWizardSteps = 50;
    private const int MaxSameStepVisits = 3;

    private SetupConfig _config = new();
    private OpenClawGatewayClient? _client;
    private string _sessionId = "";
    private string _stepId = "";
    private string _stepType = "";
    private string _currentTitle = "";
    private string _currentMessage = "";
    private string _lastProgressStepId = "";
    private WizardStepCategory _stepCategory = WizardStepCategory.Acknowledge;
    private bool _sensitive;
    private bool _errorState;
    private bool _finalizationErrorState;
    private int _operationGeneration;
    private int _wizardStepCount;
    private int _progressPolls;
    private int _totalProgressPolls;
    private readonly Dictionary<string, int> _stepVisits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WizardOptionValue> _options = [];
    private volatile bool _expectedTerminalRestart;
    // "More ▾" overflow toggle lives as a sibling of SelectOptions, so track it to remove between steps.
    private Button? _moreOptionsButton;
    // wizard.payload frames do not include plugin console output, so tail the gateway log inline.
    private WizardConsoleTail? _consoleTail;
    // Captured on connect for "Open terminal" / "Restart gateway" recovery actions.
    private GatewayHostAccessPlan _hostAccessPlan = GatewayHostAccessPlan.None();

    public WizardPage()
    {
        InitializeComponent();
        TextInput.TextChanged += (_, _) => UpdateContinueState();
        SecretInput.PasswordChanged += (_, _) => UpdateContinueState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        if (SetupPreview.IsActive)
        {
            RenderWizardPreview();
            return;
        }
        _ = StartWizardAsync();
    }

    private void RenderWizardPreview()
    {
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        ShowRecoveryActions();
        AppendTranscriptTurn("Welcome: let's connect your agent", null);
        AppendTranscriptTurn("Choose your AI provider", "Anthropic: Claude");
        AppendTranscriptTurn("Paste your API key", "••••••");

        if (SetupPreview.RequestedPage == "wizard-error")
        {
            TitleText.Text = "OpenClaw onboard hit a problem";
            ShowError("The gateway restarted before the current wizard step finished. Your setup is still installed; choose Start wizard again, or use More options to restart onboard or skip and exit.");
            return;
        }

        StatusText.Text = "A few quick questions to connect your agent";
        _stepType = "select";
        _stepId = "model";
        TitleText.Text = "Default AI model";
        SelectOptions.Visibility = Visibility.Visible;
        foreach (var (val, lbl) in new[] { ("opus", "claude-opus-4.8"), ("sonnet", "claude-sonnet-4.6"), ("haiku", "claude-haiku-4.5") })
        {
            SelectOptions.Items.Add(new ListViewItem
            {
                Content = lbl,
                Tag = val,
            });
        }
        SelectOptions.SelectedIndex = 0;
        PrimaryButton.Content = "Continue";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        AdvanceOperationGeneration();
        _expectedTerminalRestart = false;
        _ = DisconnectAsync();
    }

    private async Task StartWizardAsync(bool clearTranscript = true)
    {
        var generation = AdvanceOperationGeneration();
        try
        {
            _errorState = false;
            _finalizationErrorState = false;
            _expectedTerminalRestart = false;
            ShowRecoveryActions();
            // Cancel any in-progress server-side wizard session before starting a
            // fresh one, so the gateway doesn't reject wizard.start with "wizard
            // already running" when recovering from a previous error.
            await CancelCurrentSessionAsync();
            if (generation != _operationGeneration)
                return;

            ClearConsoleBanner();
            _sessionId = "";
            _wizardStepCount = 0;
            _progressPolls = 0;
            _totalProgressPolls = 0;
            _lastProgressStepId = "";
            _stepVisits.Clear();
            SetBusy("Connecting to gateway...");
            var client = await ConnectClientAsync();
            if (generation != _operationGeneration)
            {
                await DisconnectAndDisposeClientAsync(client);
                return;
            }

            _client = client;
            _client.StatusChanged += OnWizardClientStatusChanged;
            SetBusy("Starting wizard...");
            StartConsoleTail();
            var payload = await _client.SendWizardRequestAsync("wizard.start", timeoutMs: 30_000);
            if (generation != _operationGeneration)
                return;

            if (clearTranscript)
                TranscriptPanel.Children.Clear();
            await ApplyPayloadAsync(payload);
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            await EnterWizardErrorAsync($"Gateway wizard failed: {ex.Message}");
        }
    }

    private async Task<OpenClawGatewayClient> ConnectClientAsync()
    {
        var dataDir = SetupWindow.Active?.DataDir ?? SetupContext.ResolveDataDir();
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var record = registry.GetActive() ?? throw new InvalidOperationException("No active gateway record found.");
        _hostAccessPlan = GatewayHostAccessClassifier.Classify(record);
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var deviceToken = DeviceIdentity.TryReadStoredDeviceToken(identityPath);
        var token = deviceToken
            ?? record.SharedGatewayToken
            ?? record.BootstrapToken
            ?? throw new InvalidOperationException("No gateway credential found.");

        // The active record owns the endpoint as well as the credential identity. Resolve
        // tunnel-backed records to their Windows-side local forward instead of bypassing SSH.
        var gatewayUrl = GatewayClientEndpointResolver.Resolve(record);
        var provenanceService = new ManagedLocalGatewayPortProvenanceService(NullLogger.Instance);
        if (deviceToken is null &&
            record.SshTunnel is null &&
            GatewayRecordEditing.ResolveManagedDistroName(record) is not null &&
            GatewayRecordEditing.IsLoopbackEndpoint(record.Url))
        {
            var provenance = await provenanceService.InspectAsync(record);
            if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            {
                throw new InvalidOperationException(
                    "The managed gateway address is not owned by the verified WSL gateway; no credential was sent.");
            }
        }
        var client = new OpenClawGatewayClient(gatewayUrl, token, logger: NullLogger.Instance, identityPath: identityPath)
        {
            UseV2Signature = true
        };
        client.ReconnectAuthorizationAsync = async cancellationToken =>
        {
            if (record.SshTunnel is not null ||
                GatewayRecordEditing.ResolveManagedDistroName(record) is null ||
                !GatewayRecordEditing.IsLoopbackEndpoint(record.Url))
            {
                return ReconnectAuthorizationResult.AllowedResult;
            }
            var provenance = _expectedTerminalRestart
                ? await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                    cancellationToken => provenanceService.InspectAsync(
                        record,
                        cancellationToken),
                    noListenerRetryCount: 30,
                    retryDelay: TimeSpan.FromSeconds(1),
                    cancellationToken)
                : await provenanceService.InspectAsync(record, cancellationToken);
            return provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway
                ? ReconnectAuthorizationResult.AllowedResult
                : new ReconnectAuthorizationResult(
                    false,
                    GatewayErrorKind.LocalPortConflict,
                    provenance.Detail);
        };

        var outcome = await WaitForConnectAsync(client, TimeSpan.FromSeconds(20));
        if (!outcome)
        {
            client.Dispose();
            throw new InvalidOperationException("Could not connect to the gateway.");
        }

        return client;
    }

    private static async Task<bool> WaitForConnectAsync(OpenClawGatewayClient client, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(true);
            else if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
                tcs.TrySetResult(false);
        }

        client.StatusChanged += OnStatusChanged;
        try
        {
            await client.ConnectAsync();
            using var cts = new CancellationTokenSource(timeout);
            await using var _ = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
        }
    }

    private void OnWizardClientStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status is not (ConnectionStatus.Disconnected or ConnectionStatus.Error))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            // The pending request classifies the close code. Suppress only the duplicate
            // status error while it does so; non-1012 failures still enter the normal error path.
            if (_errorState
                || _client == null
                || !ReferenceEquals(sender, _client)
                || _expectedTerminalRestart
                || string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            _ = EnterWizardErrorAsync("Gateway connection was lost while the wizard was running.");
        });
    }

    private async Task ApplyPayloadAsync(JsonElement payload)
    {
        var generation = _operationGeneration;

        while (true)
        {
            if (payload.TryGetProperty("sessionId", out var sid))
                _sessionId = sid.GetString() ?? _sessionId;

            if (payload.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
            {
                var error = payload.TryGetProperty("error", out var err) ? err.ToString() : "";
                if (!string.IsNullOrWhiteSpace(error) && !error.Contains("this.prompt is not a function", StringComparison.OrdinalIgnoreCase))
                {
                    ShowError(error);
                    return;
                }

                await DisconnectAsync();
                if (generation != _operationGeneration || _errorState)
                    return;

                await CompleteSetupAsync(generation);
                return;
            }

            var validationError = WizardPayloadHelpers.ExtractCurrentStepValidationError(payload, _stepId);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                BusyRing.Visibility = Visibility.Collapsed;
                BusyRing.IsActive = false;
                StatusText.Text = "A few quick questions to connect your agent";
                SecondaryButton.IsEnabled = true;
                ShowRecoveryActions();
                UpdateContinueState();
                ErrorText.Text = validationError;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (!payload.TryGetProperty("step", out var step))
            {
                ShowError("Gateway wizard returned an invalid response.");
                return;
            }

            _stepId = step.TryGetProperty("id", out var id) ? id.ToString() : "";
            var rawType = step.TryGetProperty("type", out var type) ? type.ToString() : "note";
            _stepType = string.IsNullOrWhiteSpace(rawType) ? "note" : rawType.Trim().ToLowerInvariant();
            var stepIndex = payload.TryGetProperty("stepIndex", out var indexProperty) && indexProperty.TryGetInt32(out var index) ? index : 0;
            _sensitive = step.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True;
            var title = step.TryGetProperty("title", out var titleProp) ? titleProp.ToString() : "";
            var message = WizardPayloadHelpers.ExtractStepMessage(step);
            var initial = step.TryGetProperty("initialValue", out var initialProp) ? initialProp : default;
            var hasOptions = StepHasOptions(step);
            _stepCategory = WizardStepClassifier.Categorize(_stepType, hasOptions);

            if (_stepCategory == WizardStepCategory.RequiresAnswer
                && hasOptions
                && _stepType is not ("select" or "multiselect" or "text"))
            {
                _stepType = "select";
            }

            // Keep raw text for auth timeout selection; rendered URL/code rows are not TextBlocks.
            _currentTitle = title;
            _currentMessage = message;

            if (string.IsNullOrWhiteSpace(_stepId))
            {
                ShowError("Gateway wizard step is missing an id.");
                return;
            }

            // Progress carries no answer; poll until the gateway emits the next step.
            if (_stepCategory == WizardStepCategory.Progress)
            {
                if (!string.Equals(_stepId, _lastProgressStepId, StringComparison.Ordinal))
                {
                    _lastProgressStepId = _stepId;
                    _progressPolls = 0;
                }

                _progressPolls++;
                _totalProgressPolls++;
                if (_progressPolls > WizardTimeouts.MaxProgressPollsPerStep)
                {
                    ShowError($"Gateway wizard progress step '{_stepId}' did not complete after {WizardTimeouts.MaxProgressPollsPerStep} updates.");
                    return;
                }
                if (_totalProgressPolls > WizardTimeouts.MaxTotalProgressPolls)
                {
                    ShowError($"Gateway wizard did not finish after {WizardTimeouts.MaxTotalProgressPolls} progress updates.");
                    return;
                }

                RenderProgressStep(title, message);
                await Task.Delay(WizardTimeouts.ProgressPollDelay);
                if (generation != _operationGeneration || _errorState || _client == null)
                    return;

                payload = await _client.SendWizardRequestAsync(
                    "wizard.next",
                    WizardNextPayload.Acknowledge(_sessionId, _stepId),
                    timeoutMs: WizardTimeouts.ForStep(title, message, _stepId));

                if (generation != _operationGeneration || _errorState || _client == null)
                    return;

                continue;
            }

            _wizardStepCount++;
            if (_wizardStepCount > MaxWizardSteps)
            {
                ShowError($"Gateway wizard exceeded {MaxWizardSteps} steps.");
                return;
            }

            var visitKey = $"{_stepId}:{stepIndex}";
            _stepVisits.TryGetValue(visitKey, out var visits);
            _stepVisits[visitKey] = visits + 1;
            if (_stepVisits[visitKey] > MaxSameStepVisits)
            {
                ShowError($"Gateway wizard repeated step '{_stepId}' too many times.");
                return;
            }

            ResetInputs();
            TitleText.Text = string.IsNullOrWhiteSpace(title) ? DisplayTitleFor(_stepType) : title;
            RenderMessage(message);
            StepCard.MinHeight = _stepType == "note" && string.IsNullOrWhiteSpace(message) ? 140 : 260;
            ErrorText.Visibility = Visibility.Collapsed;
            BusyRing.Visibility = Visibility.Collapsed;
            BusyRing.IsActive = false;
            ShowRecoveryActions();
            StatusText.Text = "A few quick questions to connect your agent";
            PrimaryButton.IsEnabled = !WizardSelection.RequiresAnswer(_stepType);
            SecondaryButton.IsEnabled = true;
            PrimaryButton.Content = _stepType == "confirm" ? "Yes" : "Continue";
            SecondaryButton.Content = "No";
            SecondaryButton.Visibility = _stepType == "confirm" ? Visibility.Visible : Visibility.Collapsed;

            if (!BuildOptions(step, initial))
                return;

            if (_stepType == "text")
            {
                if (_sensitive)
                {
                    SecretInput.Visibility = Visibility.Visible;
                    SecretInput.Password = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
                }
                else
                {
                    TextInput.Visibility = Visibility.Visible;
                    TextInput.Text = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
                }

                UpdateContinueState();
            }

            if (_stepType == "note")
            {
                SecondaryButton.IsEnabled = false;
                SecondaryButton.Visibility = Visibility.Collapsed;
            }

            return;
        }
    }

    private static bool StepHasOptions(JsonElement step) =>
        step.TryGetProperty("options", out var options)
        && options.ValueKind == JsonValueKind.Array
        && options.EnumerateArray().Any();

    private void RenderProgressStep(string title, string message)
    {
        ResetInputs();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Working…" : title;
        RenderMessage(message);
        StepCard.MinHeight = 200;
        ErrorText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        StatusText.Text = string.IsNullOrWhiteSpace(message) ? "Working…" : "Setting things up…";
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Continue";
        SecondaryButton.IsEnabled = false;
        SecondaryButton.Visibility = Visibility.Collapsed;
        ShowRecoveryActions();
    }

    private bool BuildOptions(JsonElement step, JsonElement initial)
    {
        if (_stepType is not ("select" or "multiselect"))
            return true;

        _options.Clear();
        _options.AddRange(WizardAnswerBuilder.ReadOptions(step));

        if (!WizardSelection.HasSelectableOptions(_stepType, _options.Select(o => o.Value).ToArray()))
        {
            ShowError("Gateway wizard returned a choice step without any selectable options.");
            return false;
        }

        if (_stepType == "select")
        {
            SelectOptions.Visibility = Visibility.Visible;

            // Gateway Back is an in-band __back/back answer sent through wizard.next,
            // not a dedicated wizard.back RPC. Companion intentionally filters it
            // rather than reintroducing client-local navigation.
            var skipOptions = _options.Where(IsSkipOption).ToList();
            var moreOptions = _options.Where(IsMoreOption).ToList();
            var normalOptions = _options.Where(o => !IsSkipOption(o) && !IsMoreOption(o) && !IsBackOption(o)).ToList();

            var orderedOptions = new List<WizardOptionValue>();
            orderedOptions.AddRange(skipOptions);
            orderedOptions.AddRange(normalOptions);

            // Show first batch; if there are many options, show a "More" button to expand
            const int initialVisibleCount = 6;
            var hasOverflow = orderedOptions.Count > initialVisibleCount;
            var hasGatewayMore = moreOptions.Count > 0;
            var showMoreButton = hasOverflow || hasGatewayMore;
            var visibleOptions = hasOverflow
                ? orderedOptions.Take(initialVisibleCount).ToList()
                : orderedOptions;

            foreach (var option in visibleOptions)
            {
                SelectOptions.Items.Add(CreateOptionItem(option));
            }

            // Add "More ▼" button if there are hidden options OR gateway sent a "more" option
            if (showMoreButton)
            {
                var moreButton = new Button
                {
                    Content = "More ▾",
                    MinWidth = 100,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var remainingOptions = hasOverflow ? orderedOptions.Skip(initialVisibleCount).ToList() : new List<WizardOptionValue>();
                var gatewayMoreValue = moreOptions.Count > 0 ? moreOptions[0].Value : null;
                var capturedSkipOptions = skipOptions;
                moreButton.Click += (_, _) =>
                {
                    // Remove the More button
                    if (moreButton.Parent is Panel parent)
                        parent.Children.Remove(moreButton);
                    if (ReferenceEquals(_moreOptionsButton, moreButton))
                        _moreOptionsButton = null;

                    if (hasGatewayMore && !string.IsNullOrEmpty(gatewayMoreValue))
                    {
                        // Send the "more" option value to gateway to fetch full list, expanding inline
                        AsyncEventHandlerGuard.Run(
                            async () => await ExpandMoreOptionsAsync(gatewayMoreValue, capturedSkipOptions),
                            NullLogger.Instance,
                            "MoreExpand");
                    }
                    else
                    {
                        // Just expand remaining local options
                        foreach (var option in remainingOptions)
                        {
                            SelectOptions.Items.Add(CreateOptionItem(option));
                        }
                    }
                };
                // Insert the button after SelectOptions in the parent StackPanel
                var parentPanel = SelectOptions.Parent as Panel;
                if (parentPanel != null)
                {
                    var idx = parentPanel.Children.IndexOf(SelectOptions);
                    parentPanel.Children.Insert(idx + 1, moreButton);
                    _moreOptionsButton = moreButton;
                }
            }

            var initialValue = WizardAnswerBuilder.ValueKeys(initial).FirstOrDefault();
            var index = WizardSelection.SelectedIndex(initialValue, visibleOptions.Select(o => o.Value).ToArray());
            if (index >= 0 && index < SelectOptions.Items.Count)
                SelectOptions.SelectedIndex = index;
            else if (SelectOptions.Items.Count > 0)
                SelectOptions.SelectedIndex = 0;

            SelectOptions.SelectionChanged += (_, _) => UpdateContinueState();

            UpdateContinueState();
        }
        else
        {
            MultiOptions.Visibility = Visibility.Visible;
            var initialValues = initial.ValueKind == JsonValueKind.Array
                ? initial.EnumerateArray().Select(WizardAnswerBuilder.ValueKey).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var option in _options)
            {
                var checkBox = new CheckBox
                {
                    Content = BuildOptionContent(option),
                    Tag = option,
                    IsChecked = initialValues.Contains(option.Value),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                AutomationProperties.SetName(checkBox, OptionAccessibleName(option));
                checkBox.Checked += (_, _) => UpdateContinueState();
                checkBox.Unchecked += (_, _) => UpdateContinueState();
                MultiOptions.Children.Add(checkBox);
            }

            UpdateContinueState();
        }

        return true;
    }

    private static ListViewItem CreateOptionItem(WizardOptionValue option)
    {
        var item = new ListViewItem
        {
            Content = BuildOptionContent(option),
            Tag = option,
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(item, OptionAccessibleName(option));
        return item;
    }

    private static string OptionAccessibleName(WizardOptionValue option) =>
        string.IsNullOrWhiteSpace(option.Hint)
            ? option.Label
            : $"{option.Label}. {option.Hint}";

    private static FrameworkElement BuildOptionContent(WizardOptionValue option)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = option.Label,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(option.Hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = option.Hint,
                FontSize = 12,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            });
        }

        return panel;
    }

    private static bool IsSkipOption(WizardOptionValue option) =>
        string.Equals(option.Value, "__skip__", StringComparison.OrdinalIgnoreCase)
        || option.Label.Contains("skip", StringComparison.OrdinalIgnoreCase);

    private static bool IsMoreOption(WizardOptionValue option) =>
        string.Equals(option.Value, "__more__", StringComparison.OrdinalIgnoreCase)
        || string.Equals(option.Value, "__more", StringComparison.OrdinalIgnoreCase)
        || (option.Label.Contains("more", StringComparison.OrdinalIgnoreCase)
            && option.Label.Length < 20);

    private static bool IsBackOption(WizardOptionValue option) =>
        string.Equals(option.Value, "__back", StringComparison.OrdinalIgnoreCase)
        || string.Equals(option.Value, "back", StringComparison.OrdinalIgnoreCase);

    private static Brush ResourceBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var brush)
            && brush is Brush typedBrush
            ? typedBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            PrimaryClickAsync,
            NullLogger.Instance,
            nameof(Primary_Click));

    private async Task PrimaryClickAsync()
    {
        if (_errorState)
        {
            if (_finalizationErrorState)
            {
                _errorState = false;
                _finalizationErrorState = false;
                await CompleteSetupAsync(_operationGeneration);
                return;
            }

            await StartWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: false);
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            SecondaryClickAsync,
            NullLogger.Instance,
            nameof(Secondary_Click));

    private async Task SecondaryClickAsync()
    {
        await SendCurrentAnswerAsync(skip: true);
    }

    private async Task SendOptionValueAsync(string value)
    {
        if (_client == null) return;

        var generation = _operationGeneration;
        try
        {
            SetBusy("Loading...");
            ClearConsoleBanner();
            var parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value } };
            var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
            if (generation != _operationGeneration) return;
            await ApplyPayloadAsync(payload);
            ScrollActiveIntoView();
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration) return;
            await EnterWizardErrorAsync(ex.Message);
        }
    }

    private async Task ExpandMoreOptionsAsync(string moreValue, List<WizardOptionValue> previousSkipOptions)
    {
        if (_client == null) return;

        var generation = _operationGeneration;
        try
        {
            var parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value = moreValue } };
            var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
            if (generation != _operationGeneration) return;

            // Parse the expanded options from the response
            if (payload.TryGetProperty("step", out var step))
            {
                // Update step ID — the gateway may issue a new one for the expanded view
                if (step.TryGetProperty("id", out var expandedId))
                    _stepId = expandedId.ToString();

                var expandedOptions = WizardAnswerBuilder.ReadOptions(step).ToList();

                // Keep Gateway navigation options out of Companion's visible choices.
                var filtered = expandedOptions
                    .Where(o => !IsMoreOption(o) && !IsBackOption(o))
                    .ToList();

                // Rebuild the ListView: skip options first, then all expanded items
                SelectOptions.Items.Clear();
                _options.Clear();

                // Re-inject skip options at top
                foreach (var skip in previousSkipOptions)
                {
                    _options.Add(skip);
                    SelectOptions.Items.Add(CreateOptionItem(skip));
                }

                // Add all expanded options
                foreach (var option in filtered)
                {
                    _options.Add(option);
                    SelectOptions.Items.Add(CreateOptionItem(option));
                }

                // Select first item by default
                if (SelectOptions.Items.Count > 0)
                    SelectOptions.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration) return;
            await EnterWizardErrorAsync(ex.Message);
        }
    }

    private void StartOver_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            StartOverAsync,
            NullLogger.Instance,
            nameof(StartOver_Click));

    private async Task StartOverAsync()
    {
        var generation = AdvanceOperationGeneration();
        HideRecoveryActions();
        SetBusy("Starting over...");
        await CancelCurrentSessionAsync();
        if (generation != _operationGeneration)
            return;

        await StartWizardAsync();
    }

    private void SkipWizard_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            SkipWizardAsync,
            NullLogger.Instance,
            nameof(SkipWizard_Click));

    private async Task SendCurrentAnswerAsync(bool skip)
    {
        if (_client == null) return;

        var generation = _operationGeneration;
        var answeredQuestion = "";
        string? answeredLabel = null;
        try
        {
            object? answerValue = null;
            if (!skip && !TryBuildAnswerValue(out answerValue))
            {
                ErrorText.Text = _stepType == "multiselect"
                    ? "Choose at least one valid option."
                    : _stepType == "text"
                    ? "Enter a value to continue."
                    : "Choose a valid option.";
                ErrorText.Visibility = Visibility.Visible;
                UpdateContinueState();
                return;
            }

            SetBusy(skip ? "Skipping..." : "Submitting...");
            // The console banner shows output that arrived between the last payload
            // render and the user's current click. Once they answer, those messages
            // are "consumed" — wipe so the next step starts with a clean slate.
            ClearConsoleBanner();
            answeredQuestion = TitleText.Text;
            answeredLabel = CurrentAnswerLabel(skip);
            object parameters;
            if (skip)
            {
                parameters = _stepType == "confirm"
                    ? new { sessionId = _sessionId, answer = new { stepId = _stepId, value = false } }
                    : new { sessionId = _sessionId };
            }
            else if (_stepCategory == WizardStepCategory.NonInteractive)
            {
                parameters = WizardNextPayload.Acknowledge(_sessionId, _stepId);
            }
            else
            {
                parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value = answerValue } };
            }

            _expectedTerminalRestart =
                !skip &&
                _hostAccessPlan.CanControlWslGateway &&
                GatewayWizardRestartRecoveryPolicy.IsTerminalRestartCandidate(
                    _config.Gateway.Version,
                    _stepId,
                    _currentTitle,
                    _currentMessage);
            var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
            if (generation != _operationGeneration)
                return;

            if (string.IsNullOrWhiteSpace(WizardPayloadHelpers.ExtractCurrentStepValidationError(payload, _stepId)))
                AppendTranscriptTurn(answeredQuestion, answeredLabel);
            await ApplyPayloadAsync(payload);
            ScrollActiveIntoView();
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            if (_expectedTerminalRestart &&
                GatewayWizardRestartRecoveryPolicy.IsExpectedTerminalRestart(
                    _config.Gateway.Version,
                    _stepId,
                    ex,
                    _currentTitle,
                    _currentMessage))
            {
                StatusText.Text = "Gateway restarted; verifying endpoint ownership...";
                var reconnected = await WaitForReconnectAsync(
                    _client,
                    TimeSpan.FromSeconds(60));
                if (generation != _operationGeneration)
                    return;

                if (!reconnected)
                {
                    await EnterWizardErrorAsync(
                        "The gateway restarted after applying setup, but Companion could not verify and reconnect to its managed endpoint.");
                    return;
                }

                AppendTranscriptTurn(answeredQuestion, answeredLabel);
                await DisconnectAsync();
                if (generation != _operationGeneration || _errorState)
                    return;

                await CompleteSetupAsync(generation);
                return;
            }

            await EnterWizardErrorAsync(ex.Message);
        }
        finally
        {
            if (generation == _operationGeneration)
                _expectedTerminalRestart = false;
        }
    }

    private static async Task<bool> WaitForReconnectAsync(
        OpenClawGatewayClient client,
        TimeSpan timeout)
    {
        if (client.HasHandshakeSnapshot)
            return true;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHandshakeSucceeded(object? sender, EventArgs args) =>
            tcs.TrySetResult(true);
        void OnDisposed(object? sender, EventArgs args) =>
            tcs.TrySetResult(false);

        client.HandshakeSucceeded += OnHandshakeSucceeded;
        client.Disposed += OnDisposed;
        try
        {
            if (client.HasHandshakeSnapshot)
                return true;

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            await using var _ = timeoutCancellation.Token.Register(
                () => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            client.HandshakeSucceeded -= OnHandshakeSucceeded;
            client.Disposed -= OnDisposed;
        }
    }

    private string? CurrentAnswerLabel(bool skip)
    {
        if (skip)
            return _stepType == "confirm" ? "No" : "Skipped";

        return _stepType switch
        {
            "confirm" => "Yes",
            "text" => _sensitive ? "••••••" : (string.IsNullOrEmpty(TextInput.Text) ? null : TextInput.Text),
            "select" or "multiselect" => LabelForValues(GetSelectedOptionValues()),
            _ => null,
        };
    }

    private string? LabelForValues(string[] values)
    {
        if (values.Length == 0)
            return null;
        var labels = values.Select(v => _options.FirstOrDefault(o => o.Value == v)?.Label ?? v);
        return string.Join(", ", labels);
    }

    // Presentation-only transcript; protocol frames are unchanged.
    private void AppendTranscriptTurn(string question, string? answer)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        var grid = new Grid { Padding = new Thickness(2, 4, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ResourceBrush("SystemFillColorSuccessBrush"),
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 10,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                IsTextScaleFactorEnabled = false,
            },
        };

        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontSize = 13,
            Foreground = ResourceBrush("TextFillColorTertiaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(answer))
        {
            stack.Children.Add(new TextBlock
            {
                Text = answer,
                FontSize = 13,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(dot);
        grid.Children.Add(stack);
        TranscriptPanel.Children.Add(grid);
    }

    private void ScrollActiveIntoView()
    {
        MainScroller.UpdateLayout();
        // Bring the active step card's TITLE into view (just below the last answered
        // step) rather than jumping to the very bottom — scrolling to the bottom hid
        // the step's introduction/question when it had many options (e.g. web search).
        if (MainScroller.Content is FrameworkElement content)
        {
            try
            {
                var cardTop = StepCard.TransformToVisual(content)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                // Leave a little room above so the most recent answered step stays
                // visible for continuity, but keep the active title at the top.
                var target = Math.Max(0, cardTop - 44);
                MainScroller.ChangeView(null, target, null);
                return;
            }
            catch
            {
                // Fall back to the previous behaviour if the transform fails.
            }
        }
        MainScroller.ChangeView(null, MainScroller.ScrollableHeight, null);
    }

    private bool TryBuildAnswerValue(out object value)
    {
        value = _stepType switch
        {
            "confirm" => true,
            "select" => SelectOptions.SelectedItem is ListViewItem { Tag: WizardOptionValue option }
                    ? option.RawValue
                    : "",
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag as WizardOptionValue)
                .OfType<WizardOptionValue>()
                .Select(option => option.RawValue)
                .ToArray(),
            "text" => _sensitive ? SecretInput.Password : TextInput.Text,
            _ => "true"
        };

        if (!WizardSelection.RequiresAnswer(_stepType))
            return true;

        if (_stepType == "text")
            return WizardSelection.CanSubmitTextAnswer(_stepType);

        return !WizardSelection.ShouldDisableContinue(_stepType, GetSelectedOptionValues(), _options.Select(o => o.Value).ToArray());
    }

    private string[] GetSelectedOptionValues()
    {
        return _stepType switch
        {
            "select" => SelectOptions.SelectedItem is ListViewItem { Tag: WizardOptionValue selectedOpt }
                ? [selectedOpt.Value]
                : [],
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag is WizardOptionValue option ? option.Value : "")
                .Where(v => v.Length > 0)
                .ToArray(),
            _ => []
        };
    }

    private void UpdateContinueState()
    {
        if (_errorState || !WizardSelection.RequiresAnswer(_stepType))
            return;

        PrimaryButton.IsEnabled = _stepType == "text"
            ? WizardSelection.CanSubmitTextAnswer(_stepType)
            : !WizardSelection.ShouldDisableContinue(
                _stepType,
                GetSelectedOptionValues(),
                _options.Select(o => o.Value).ToArray());

        if (PrimaryButton.IsEnabled)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private int TimeoutForCurrentStep()
    {
        IReadOnlyCollection<WizardOptionValue>? selectedOptions = null;
        if (WizardSelection.RequiresSelection(_stepType))
        {
            var selectedValues = GetSelectedOptionValues().ToHashSet(StringComparer.Ordinal);
            selectedOptions = _options
                .Where(option => selectedValues.Contains(option.Value))
                .ToArray();
        }

        return WizardTimeouts.ForStep(_currentTitle, _currentMessage, _stepId, selectedOptions);
    }

    private void ResetInputs()
    {
        if (_moreOptionsButton?.Parent is Panel morePanel)
            morePanel.Children.Remove(_moreOptionsButton);
        _moreOptionsButton = null;
        SelectOptions.Items.Clear();
        SelectOptions.Visibility = Visibility.Collapsed;
        MultiOptions.Children.Clear();
        MultiOptions.Visibility = Visibility.Collapsed;
        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        MessageBlock.Blocks.Clear();
        MessageBlock.Visibility = Visibility.Collapsed;
        MessageCodeRows.Children.Clear();
        HideGatewayRecovery();
    }

    private void RenderMessage(string message)
    {
        MessageBlock.Blocks.Clear();
        MessageCodeRows.Children.Clear();
        MessageBlock.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(message))
            return;

        var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
        var hasContent = false;

        foreach (var line in message.Split('\n'))
        {
            // Strip leading bullet characters (•, -, *) that look redundant in the UI
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("• ") || trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                trimmed = trimmed[2..];

            var segment = WizardMessageFormatting.ClassifyLine(trimmed);

            if (segment.Kind == WizardLineKind.Code)
            {
                // Code rows with copy buttons stay as separate interactive elements
                MessageCodeRows.Children.Add(BuildCodeRow(segment.Prefix, segment.Highlight));
                continue;
            }

            if (hasContent)
                paragraph.Inlines.Add(new LineBreak());

            if (segment.Kind == WizardLineKind.Url && Uri.TryCreate(segment.Highlight, UriKind.Absolute, out var uri))
            {
                var urlIndex = segment.Text.IndexOf(segment.Highlight, StringComparison.Ordinal);
                var prefix = segment.Text[..urlIndex];
                if (!string.IsNullOrEmpty(prefix))
                    paragraph.Inlines.Add(new Run { Text = prefix });

                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = segment.Highlight });
                paragraph.Inlines.Add(link);

                var suffix = segment.Text[(urlIndex + segment.Highlight.Length)..];
                if (!string.IsNullOrEmpty(suffix))
                    paragraph.Inlines.Add(new Run { Text = suffix });
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = segment.Text });
            }

            hasContent = true;
        }

        if (hasContent)
        {
            MessageBlock.Blocks.Add(paragraph);
            MessageBlock.Visibility = Visibility.Visible;
        }
    }

    private void StartConsoleTail()
    {
        StopConsoleTail();
        var tail = new WizardConsoleTail(
            logger: NullLogger.Instance,
            distroNameOverride: _config.DistroName);
        _consoleTail = tail;
        var dispatcher = DispatcherQueue;
        tail.Start(message =>
        {
            try
            {
                dispatcher?.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_consoleTail, tail))
                        return;
                    AppendConsoleLine(message);
                });
            }
            // slopwatch-ignore: SW003 Audited non-critical fallback is intentional and the caller preserves safe behavior without this work.
            catch
            {
            }
        });
    }

    private void StopConsoleTail()
    {
        var tail = _consoleTail;
        _consoleTail = null;
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { tail?.Stop(); } catch { }
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { tail?.Dispose(); } catch { }
    }

    private void AppendConsoleLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (WizardConsoleTail.LooksLikeTerminalQrArt(message))
        {
            AppendQrConsoleBlock(message);
            ConsoleBanner.Visibility = Visibility.Visible;
            return;
        }

        foreach (var line in message.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var para = new Microsoft.UI.Xaml.Documents.Paragraph();
            para.Inlines.Add(new Run { Text = line });
            ConsoleBannerBlock.Blocks.Add(para);
        }

        ConsoleBanner.Visibility = Visibility.Visible;
    }

    private void AppendQrConsoleBlock(string message)
    {
        var text = message.Replace("\r\n", "\n").TrimEnd('\r', '\n');
        var qrText = new TextBlock
        {
            Text = text,
            FontSize = 9,
            LineHeight = 9,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };

        var qrSurface = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(12),
            Child = qrText
        };

        ConsoleBannerLines.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = qrSurface
        });
    }

    private void ClearConsoleBanner()
    {
        ConsoleBannerBlock.Blocks.Clear();
        ConsoleBannerLines.Children.Clear();
        ConsoleBanner.Visibility = Visibility.Collapsed;
    }

    private static FrameworkElement BuildLinkLine(string line, string urlText, Uri uri)
    {
        var textBlock = new TextBlock
        {
            FontSize = 14,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var urlIndex = line.IndexOf(urlText, StringComparison.Ordinal);
        var prefix = line[..urlIndex];
        if (!string.IsNullOrEmpty(prefix))
            textBlock.Inlines.Add(new Run { Text = prefix });

        var link = new Hyperlink
        {
            NavigateUri = uri
        };
        link.Inlines.Add(new Run { Text = urlText });
        textBlock.Inlines.Add(link);

        var suffix = line[(urlIndex + urlText.Length)..];
        if (!string.IsNullOrEmpty(suffix))
            textBlock.Inlines.Add(new Run { Text = suffix });

        return textBlock;
    }

    private static FrameworkElement BuildCodeRow(string prefix, string code)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 10
        };

        var label = new TextBlock { Text = prefix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center };
        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(8, 4, 8, 4) };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(codeText, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(label);
        grid.Children.Add(codeText);
        grid.Children.Add(copy);
        return grid;
    }

    private void SetBusy(string status)
    {
        StatusText.Text = status;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        HideGatewayRecovery();
    }

    private void ShowError(string message)
    {
        _errorState = true;
        _finalizationErrorState = false;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Wizard needs attention";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Start wizard again";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.IsEnabled = false;
        SecondaryButton.Visibility = Visibility.Collapsed;
        ShowRecoveryActions();
        MaybeShowGatewayRecovery();
    }

    private void ShowFinalizationError(string message)
    {
        ShowError(message);
        _finalizationErrorState = true;
        StatusText.Text = "Windows integration needs attention";
        PrimaryButton.Content = "Retry Windows integration";
    }

    private async Task EnterWizardErrorAsync(string detail)
    {
        if (_errorState)
            return;

        // Invalidate in-flight wizard.next calls before tearing down the connection.
        AdvanceOperationGeneration();
        _errorState = true;
        // Cancel the server-side wizard session before disconnecting so retries
        // don't hit a "wizard already running" error from a lingering session.
        await CancelCurrentSessionAsync();
        ShowError(detail);
    }

    // Shows the WSL recovery affordances (open a terminal / restart the gateway)
    // whenever the wizard surfaces an error AND the active gateway is an
    // app-managed WSL distro we can control. We deliberately do not parse the
    // gateway's error text: its wording is outside our control and can change, so
    // the user reads the (selectable) error message and decides what to run.
    private void MaybeShowGatewayRecovery()
    {
        HideGatewayRecovery();

        if (!_hostAccessPlan.CanControlWslGateway || string.IsNullOrWhiteSpace(_hostAccessPlan.DistroName))
            return;

        OpenGatewayTerminalButton.IsEnabled = true;
        RestartGatewayButton.IsEnabled = true;
        GatewayRecovery.Visibility = Visibility.Visible;
    }

    private void HideGatewayRecovery()
    {
        GatewayRecovery.Visibility = Visibility.Collapsed;
    }

    private void OpenGatewayTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!_hostAccessPlan.CanOpenTerminal)
            return;

        try
        {
            new GatewayTerminalLauncher(NullLogger.Instance).Open(_hostAccessPlan);
            StatusText.Text = $"Opened a terminal in {_hostAccessPlan.DistroName}. Install the tool, then choose Restart gateway.";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Couldn't open a terminal: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void RestartGateway_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RestartGatewayAsync,
            NullLogger.Instance,
            nameof(RestartGateway_Click));

    private async Task RestartGatewayAsync()
    {
        var distro = _hostAccessPlan.DistroName;
        if (!_hostAccessPlan.CanControlWslGateway || string.IsNullOrWhiteSpace(distro))
            return;

        // Claim this operation and lock the UI synchronously, before the first await,
        // so a second Restart/Skip/Open-terminal click during the disconnect can't race
        // us. SetBusy disables the primary/secondary buttons and collapses the recovery
        // panel (which hosts the Restart/Open-terminal buttons), so they stop hit-testing.
        var generation = AdvanceOperationGeneration();
        _errorState = false;
        ErrorText.Visibility = Visibility.Collapsed;
        SetBusy($"Restarting {distro}...");

        // Detach the current wizard client first so the restart-induced disconnect
        // doesn't surface as a spurious "connection lost" error mid-restart.
        await DisconnectAsync();
        if (generation != _operationGeneration)
            return;

        try
        {
            // A gateway restart spins up a fresh login shell and restarts the daemon
            // inside the distro, which can take well over the runner's default 30s
            // ceiling on a cold distro. Give it a generous timeout so a slow-but-healthy
            // restart isn't reported as a spurious timeout failure.
            var runner = new WslExeCommandRunner(NullLogger.Instance, defaultTimeout: TimeSpan.FromMinutes(2));
            var controller = new WslGatewayController(runner, NullLogger.Instance);
            var result = await controller.RunAsync(distro, WslGatewayControlAction.Restart);
            if (generation != _operationGeneration)
                return;

            if (!result.Success)
            {
                var details = string.IsNullOrWhiteSpace(result.OutputSummary)
                    ? $"wsl.exe exited with code {result.ExitCode}."
                    : result.OutputSummary;
                await EnterWizardErrorAsync($"Restarting the gateway failed: {details}");
                return;
            }

            // Gateway is back up with the freshly-installed tool on PATH. Stay on
            // this page and re-enter the gateway config wizard (provider/model
            // onboarding) — we do NOT return to Welcome or re-install WSL. The
            // gateway restart wiped its wizard session, so this resumes at the
            // first config question rather than the exact step that failed.
            await StartWizardAsync(clearTranscript: false);
        }
        catch (Exception ex)
        {
            if (generation != _operationGeneration)
                return;

            await EnterWizardErrorAsync($"Restarting the gateway failed: {ex.Message}");
        }
    }

    private async Task SkipWizardAsync()
    {
        var generation = AdvanceOperationGeneration();
        _errorState = false;
        HideRecoveryActions();
        SetBusy("Skipping wizard...");
        await CancelCurrentSessionAsync();
        await CompleteSetupAsync(generation);
    }

    private async Task CompleteSetupAsync(int generation)
    {
        if (generation != _operationGeneration || _errorState)
            return;

        var setupWindow = SetupWindow.Active;
        if (setupWindow is null or { IsClosed: true })
            return;

        SetBusy("Finishing Windows integration...");
        var contextResult = await setupWindow.ApplyWindowsNodeContextAsync();
        if (generation != _operationGeneration || setupWindow.IsClosed)
            return;

        if (!contextResult.IsSuccess)
        {
            ShowFinalizationError($"OpenClaw onboard finished, but Windows node guidance could not be installed: {contextResult.Message}");
            return;
        }

        // Permissions were collected before install, so completion goes straight to summary.
        setupWindow.NavigateToComplete(true, TimeSpan.Zero, _config!.LogPath);
    }

    private async Task CancelCurrentSessionAsync()
    {
        if (_client != null && !string.IsNullOrWhiteSpace(_sessionId))
        {
            try { await _client.SendWizardRequestAsync("wizard.cancel", new { sessionId = _sessionId }, timeoutMs: 10_000); }
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            catch { }
        }
        await DisconnectAsync();
    }

    private int AdvanceOperationGeneration() => unchecked(++_operationGeneration);

    private void ShowRecoveryActions()
    {
        MoreOptionsButton.Visibility = Visibility.Visible;
        MoreOptionsButton.IsEnabled = true;
    }

    private void HideRecoveryActions()
    {
        MoreOptionsButton.Visibility = Visibility.Collapsed;
        MoreOptionsButton.IsEnabled = false;
    }

    private async Task DisconnectAsync()
    {
        StopConsoleTail();
        var client = _client;
        if (client == null) return;
        _client = null;
        client.StatusChanged -= OnWizardClientStatusChanged;
        // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
        try { await client.DisconnectAsync(); } catch { }
        client.Dispose();
    }

    private static async Task DisconnectAndDisposeClientAsync(OpenClawGatewayClient client)
    {
        try
        {
            await client.DisconnectAsync();
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string DisplayTitleFor(string stepType) => stepType switch
    {
        "confirm" => "Confirm",
        "select" => "Choose an option",
        "multiselect" => "Choose options",
        "text" => "Enter value",
        _ => "Setup"
    };
}
