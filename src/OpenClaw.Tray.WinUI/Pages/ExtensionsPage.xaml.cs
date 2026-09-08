using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using System.ComponentModel;

namespace OpenClawTray.Pages;

public sealed partial class ExtensionsPage : Page
{
    private enum PendingPluginAction
    {
        None,
        Install,
        SetEnabled,
        Uninstall,
    }

    private ExtensionsPageViewModel? _viewModel;
    private bool _showingInstalledSkills = true;
    private bool _showingInstalledPlugins = true;
    private SkillReviewPresentation? _skillReview;
    private long _skillReviewGeneration;
    private PluginReviewPresentation? _pluginReview;
    private long _pluginReviewGeneration;
    private PendingPluginAction _pendingPluginAction;
    private PluginCapabilityAcknowledgement? _pluginAcknowledgementOverride;
    private bool _pluginPolicyAcknowledgementRequired;
    private bool _pluginOperationInProgress;
    private bool _synchronizingAgentSelection;

    public ExtensionsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
        SkillFilterCombo.SelectedIndex = 0;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as ExtensionsPageViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SynchronizeAgentSelector();
        }
        UpdateState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _skillReviewGeneration++;
        _pluginReviewGeneration++;
        _skillReview = null;
        _pluginReview = null;
        _pendingPluginAction = PendingPluginAction.None;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtensionsPageViewModel.ConnectionScopeVersion))
        {
            CloseSkillReview();
            ClosePluginReview();
            SynchronizeAgentSelector();
        }
        UpdateState();
    }

    private void SynchronizeAgentSelector()
    {
        if (_viewModel is null)
            return;
        _synchronizingAgentSelection = true;
        try
        {
            AgentCombo.ItemsSource = _viewModel.AgentIds;
            AgentCombo.SelectedItem = _viewModel.SelectedAgentId;
        }
        finally
        {
            _synchronizingAgentSelection = false;
        }
    }

    private void UpdateState()
    {
        if (_viewModel is null)
            return;
        SkillsProgress.Visibility = _viewModel.IsLoadingSkills || _viewModel.IsSearchingSkills
            ? Visibility.Visible
            : Visibility.Collapsed;
        SkillsCountText.Text = _viewModel.SkillCountText;
        SkillsCountText.Visibility = _viewModel.SkillsSupported && _viewModel.ErrorMessage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        SkillsEmptyState.Visibility = !_viewModel.IsLoadingSkills &&
            _viewModel.SkillsSupported && _viewModel.ErrorMessage is null &&
            _showingInstalledSkills && !_viewModel.HasVisibleSkills
                ? Visibility.Visible
                : Visibility.Collapsed;

        var message = _viewModel.ErrorMessage ?? _viewModel.StatusMessage;
        PageInfoBar.IsOpen = ExtensionsTabs.SelectedIndex != 1 &&
            !string.IsNullOrWhiteSpace(message);
        PageInfoBar.Message = message ?? string.Empty;
        PageInfoBar.Severity = _viewModel.ErrorMessage is null
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Error;

        PluginsProgress.Visibility = _viewModel.IsLoadingPlugins || _viewModel.IsSearchingPlugins
            ? Visibility.Visible
            : Visibility.Collapsed;
        PluginsCountText.Text = _viewModel.PluginCountText;
        PluginsCountText.Visibility = _viewModel.PluginsSupported && _viewModel.PluginErrorMessage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PluginsEmptyState.Visibility = !_viewModel.IsLoadingPlugins &&
            _viewModel.PluginsSupported && _viewModel.PluginErrorMessage is null &&
            _showingInstalledPlugins && !_viewModel.HasInstalledPlugins
                ? Visibility.Visible
                : Visibility.Collapsed;
        var pluginMessage = _viewModel.PluginErrorMessage ?? _viewModel.PluginStatusMessage;
        PluginInfoBar.IsOpen = !string.IsNullOrWhiteSpace(pluginMessage);
        PluginInfoBar.Message = pluginMessage ?? string.Empty;
        PluginInfoBar.Severity = _viewModel.PluginErrorMessage is null
            ? InfoBarSeverity.Informational
            : InfoBarSeverity.Error;
    }

    private void OnInstalledSkillsClick(object sender, RoutedEventArgs e)
    {
        CloseSkillReview();
        _showingInstalledSkills = true;
        InstalledSkillsButton.IsChecked = true;
        DiscoverSkillsButton.IsChecked = false;
        InstalledSkillsPanel.Visibility = Visibility.Visible;
        DiscoverSkillsPanel.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    private void OnExtensionsTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null)
            return;
        CloseSkillReview();
        ClosePluginReview();
        UpdateState();
    }

    private void OnDiscoverSkillsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledSkills = false;
        InstalledSkillsButton.IsChecked = false;
        DiscoverSkillsButton.IsChecked = true;
        InstalledSkillsPanel.Visibility = Visibility.Collapsed;
        DiscoverSkillsPanel.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void OnAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingAgentSelection ||
            _viewModel is null || AgentCombo.SelectedItem is not string agentId)
            return;
        CloseSkillReview();
        _ = _viewModel.SelectAgentAsync(agentId);
    }

    private void OnSkillFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || SkillFilterCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag || !Enum.TryParse<SkillListFilter>(tag, out var filter))
        {
            return;
        }
        _viewModel.SetSkillFilter(filter);
    }

    private void OnSearchSkillsClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(SearchSkillsAsync, new AppLogger(), nameof(OnSearchSkillsClick));

    private void OnSkillSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AsyncEventHandlerGuard.Run(SearchSkillsAsync, new AppLogger(), nameof(OnSkillSearchKeyDown));
    }

    private Task SearchSkillsAsync()
    {
        CloseSkillReview();
        return _viewModel?.SearchSkillsAsync(SkillSearchBox.Text) ?? Task.CompletedTask;
    }

    private void OnReviewSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewSkillAsync(sender),
            new AppLogger(),
            nameof(OnReviewSkillClick));

    private async Task ReviewSkillAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: SkillSearchItemPresentation item })
            return;
        var generation = ++_skillReviewGeneration;
        var review = await _viewModel.ReviewSkillAsync(item);
        if (generation != _skillReviewGeneration)
            return;
        if (review is null)
        {
            UpdateState();
            return;
        }

        _skillReview = review;
        SkillReviewName.Text = review.Item.Name;
        SkillReviewSummary.Text = review.Summary;
        SkillReviewPublisher.Text = LocalizationHelper.Format("ExtensionsPage_ReviewPublisherFormat", review.Publisher);
        SkillReviewVersion.Text = LocalizationHelper.Format("ExtensionsPage_ReviewVersionFormat", review.Version);
        SkillReviewInstallIdentity.Text = LocalizationHelper.Format(
            "ExtensionsPage_SkillInstallIdentityFormat",
            review.InstallReference);
        SkillReviewMetadata.Text = review.Metadata;
        UnscannedSkillWarning.Message = LocalizationHelper.GetString("ExtensionsPage_UnscannedWarning");
        UnscannedSkillWarning.IsOpen = review.RequiresUnscannedConfirmation;
        UnscannedSkillAcknowledge.Visibility = review.RequiresUnscannedConfirmation
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnscannedSkillAcknowledge.IsChecked = false;
        InstallReviewedSkillButton.IsEnabled = review.Item.CanInstall &&
            !review.RequiresUnscannedConfirmation;
        SkillSearchResultsList.Visibility = Visibility.Collapsed;
        SkillReviewPanel.Visibility = Visibility.Visible;
    }

    private void OnUnscannedAcknowledgeChanged(object sender, RoutedEventArgs e)
    {
        if (_skillReview?.RequiresUnscannedConfirmation == true)
        {
            InstallReviewedSkillButton.IsEnabled = _skillReview.Item.CanInstall &&
                UnscannedSkillAcknowledge.IsChecked == true;
        }
    }

    private void OnCancelSkillReviewClick(object sender, RoutedEventArgs e)
        => CloseSkillReview();

    private void CloseSkillReview()
    {
        _skillReviewGeneration++;
        _skillReview = null;
        SkillReviewPanel.Visibility = Visibility.Collapsed;
        SkillSearchResultsList.Visibility = Visibility.Visible;
    }

    private void OnInstallReviewedSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            InstallReviewedSkillAsync,
            new AppLogger(),
            nameof(OnInstallReviewedSkillClick));

    private async Task InstallReviewedSkillAsync()
    {
        if (_viewModel is null || _skillReview is null)
            return;
        var review = _skillReview;
        var generation = _skillReviewGeneration;
        InstallReviewedSkillButton.IsEnabled = false;
        var acknowledged = review.RequiresUnscannedConfirmation &&
            UnscannedSkillAcknowledge.IsChecked == true;
        var outcome = await _viewModel.InstallSkillAsync(review, acknowledged);
        if (generation != _skillReviewGeneration || !ReferenceEquals(review, _skillReview))
            return;
        ShowOutcome(outcome);
        if (outcome.Succeeded)
        {
            CloseSkillReview();
        }
        else
        {
            InstallReviewedSkillButton.IsEnabled = review.Item.CanInstall &&
                (!review.RequiresUnscannedConfirmation || acknowledged);
        }
    }

    private void OnToggleSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSkillActionAsync(sender, update: false),
            new AppLogger(),
            nameof(OnToggleSkillClick));

    private void OnUpdateSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => RunSkillActionAsync(sender, update: true),
            new AppLogger(),
            nameof(OnUpdateSkillClick));

    private async Task RunSkillActionAsync(object sender, bool update)
    {
        if (_viewModel is null || sender is not Button { Tag: SkillListItemPresentation item } button)
            return;
        button.IsEnabled = false;
        try
        {
            var outcome = update
                ? await _viewModel.UpdateSkillAsync(item)
                : await _viewModel.ToggleSkillAsync(item);
            ShowOutcome(outcome);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowOutcome(SkillActionOutcome outcome)
    {
        PageInfoBar.Title = LocalizationHelper.GetString(outcome.Succeeded
            ? "ExtensionsPage_ActionCompleteTitle"
            : "ExtensionsPage_ActionCouldNotCompleteTitle");
        PageInfoBar.Message = outcome.Message;
        PageInfoBar.Severity = outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        PageInfoBar.IsOpen = true;
    }

    private void OnOpenExternalLinkClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OpenExternalLinkAsync(sender),
            new AppLogger(),
            nameof(OnOpenExternalLinkClick));

    private static async Task OpenExternalLinkAsync(object sender)
    {
        if (sender is not HyperlinkButton { Tag: string raw } ||
            !HttpUrlValidator.TryParse(raw, out var canonical, out _) ||
            canonical is null)
        {
            return;
        }
        await global::Windows.System.Launcher.LaunchUriAsync(new Uri(canonical));
    }

    private void OnInstalledPluginsClick(object sender, RoutedEventArgs e)
    {
        ClosePluginReview();
        _showingInstalledPlugins = true;
        InstalledPluginsButton.IsChecked = true;
        DiscoverPluginsButton.IsChecked = false;
        InstalledPluginsPanel.Visibility = Visibility.Visible;
        DiscoverPluginsPanel.Visibility = Visibility.Collapsed;
        UpdateState();
    }

    private void OnDiscoverPluginsClick(object sender, RoutedEventArgs e)
    {
        _showingInstalledPlugins = false;
        InstalledPluginsButton.IsChecked = false;
        DiscoverPluginsButton.IsChecked = true;
        InstalledPluginsPanel.Visibility = Visibility.Collapsed;
        DiscoverPluginsPanel.Visibility = Visibility.Visible;
        UpdateState();
    }

    private void OnSearchPluginsClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(SearchPluginsAsync, new AppLogger(), nameof(OnSearchPluginsClick));

    private void OnPluginSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        AsyncEventHandlerGuard.Run(SearchPluginsAsync, new AppLogger(), nameof(OnPluginSearchKeyDown));
    }

    private async Task SearchPluginsAsync()
    {
        if (!TryBeginPluginOperation())
            return;
        ClosePluginReview();
        try
        {
            if (_viewModel is not null)
                await _viewModel.SearchPluginsAsync(PluginSearchBox.Text);
        }
        finally
        {
            EndPluginOperation();
        }
    }

    private void OnReviewInstalledPluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewInstalledPluginAsync(sender),
            new AppLogger(),
            nameof(OnReviewInstalledPluginClick));

    private async Task ReviewInstalledPluginAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: PluginListItemPresentation item } ||
            !TryBeginPluginOperation())
            return;
        var generation = ++_pluginReviewGeneration;
        PluginReviewPresentation? review;
        try
        {
            review = await _viewModel.ReviewPluginAsync(item);
        }
        finally
        {
            EndPluginOperation();
        }
        if (generation == _pluginReviewGeneration)
            ApplyPluginReview(review, PendingPluginAction.None);
    }

    private void OnReviewSearchPluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => ReviewSearchPluginAsync(sender),
            new AppLogger(),
            nameof(OnReviewSearchPluginClick));

    private async Task ReviewSearchPluginAsync(object sender)
    {
        if (_viewModel is null || sender is not Button { Tag: PluginSearchItemPresentation item } ||
            !TryBeginPluginOperation())
            return;
        var generation = ++_pluginReviewGeneration;
        PluginReviewPresentation? review;
        try
        {
            review = await _viewModel.ReviewPluginAsync(item);
        }
        finally
        {
            EndPluginOperation();
        }
        if (generation == _pluginReviewGeneration)
        {
            ApplyPluginReview(
                review,
                item.CanInstall ? PendingPluginAction.Install : PendingPluginAction.None);
        }
    }

    private void ApplyPluginReview(
        PluginReviewPresentation? review,
        PendingPluginAction pendingAction)
    {
        if (review is null)
        {
            UpdateState();
            return;
        }
        _pluginReview = review;
        _pendingPluginAction = pendingAction;
        _pluginAcknowledgementOverride = null;
        _pluginPolicyAcknowledgementRequired = false;
        _showingInstalledPlugins = false;
        InstalledPluginsPanel.Visibility = Visibility.Collapsed;
        DiscoverPluginsPanel.Visibility = Visibility.Visible;
        InstalledPluginsButton.IsChecked = false;
        DiscoverPluginsButton.IsChecked = true;
        ApplyPluginReviewDetails(review);
        PluginRestartWarning.Message = LocalizationHelper.GetString("ExtensionsPage_PluginRestartWarning");
        PluginRestartWarning.IsOpen = pendingAction != PendingPluginAction.None;
        PluginCapabilityInfo.IsOpen = false;
        PluginInstallPolicyInfo.IsOpen = false;
        PluginInstallPolicyAcknowledge.Visibility = Visibility.Collapsed;
        PluginInstallPolicyAcknowledge.IsChecked = false;
        PluginActionAcknowledge.Visibility = pendingAction == PendingPluginAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        PluginActionAcknowledge.IsChecked = false;
        PluginActionAcknowledge.Content = LocalizationHelper.GetString(pendingAction switch
        {
            PendingPluginAction.Uninstall => "ExtensionsPage_PluginRemovalAcknowledge",
            PendingPluginAction.SetEnabled when review.InstalledItem?.Enabled == true =>
                "ExtensionsPage_PluginDisableAcknowledge",
            PendingPluginAction.Install when string.IsNullOrWhiteSpace(review.ReviewToken) =>
                "ExtensionsPage_PluginInstallAcknowledge",
            _ => "ExtensionsPage_PluginCapabilityAcknowledge",
        });
        PluginReviewActionButton.Visibility = pendingAction == PendingPluginAction.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        PluginReviewActionButton.Content = LocalizationHelper.GetString(pendingAction switch
        {
            PendingPluginAction.Install => "ExtensionsPage_InstallAction",
            PendingPluginAction.SetEnabled when review.InstalledItem?.Enabled == true => "ExtensionsPage_DisableAction",
            PendingPluginAction.SetEnabled => "ExtensionsPage_EnableAction",
            PendingPluginAction.Uninstall => "ExtensionsPage_UninstallAction",
            _ => "ExtensionsPage_CloseAction",
        });
        UpdatePluginActionEnabled();
        PluginSearchResultsList.Visibility = Visibility.Collapsed;
        PluginReviewPanel.Visibility = Visibility.Visible;
    }

    private void ApplyPluginReviewDetails(PluginReviewPresentation review)
    {
        PluginReviewName.Text = review.Name;
        PluginReviewDescription.Text = review.Description;
        PluginReviewVersion.Text = LocalizationHelper.Format("ExtensionsPage_ReviewVersionFormat", review.Version);
        PluginReviewOrigin.Text = LocalizationHelper.Format("ExtensionsPage_PluginOriginFormat", review.Origin);
        PluginReviewIdentity.Text = LocalizationHelper.Format(
            "ExtensionsPage_PluginInstallIdentityFormat",
            review.InstallIdentity);
        PluginReviewIntegrity.Text = LocalizationHelper.Format(
            "ExtensionsPage_PluginIntegrityFormat",
            review.Integrity);
        PluginReviewSurfaces.Text = review.DeclaredSurfaces;
        PluginReviewGrants.Text = review.GrantedAccess;
        PluginReviewTrust.Text = LocalizationHelper.Format("ExtensionsPage_PluginTrustFormat", review.Trust);
    }

    private void OnClosePluginReviewClick(object sender, RoutedEventArgs e)
        => ClosePluginReview();

    private void ClosePluginReview()
    {
        _pluginReviewGeneration++;
        _pluginReview = null;
        _pendingPluginAction = PendingPluginAction.None;
        _pluginAcknowledgementOverride = null;
        _pluginPolicyAcknowledgementRequired = false;
        PluginReviewPanel.Visibility = Visibility.Collapsed;
        PluginSearchResultsList.Visibility = Visibility.Visible;
    }

    private void OnTogglePluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => PrepareInstalledPluginActionAsync(sender, PendingPluginAction.SetEnabled),
            new AppLogger(),
            nameof(OnTogglePluginClick));

    private void OnUninstallPluginClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => PrepareInstalledPluginActionAsync(sender, PendingPluginAction.Uninstall),
            new AppLogger(),
            nameof(OnUninstallPluginClick));

    private async Task PrepareInstalledPluginActionAsync(object sender, PendingPluginAction action)
    {
        if (_viewModel is null || sender is not Button { Tag: PluginListItemPresentation item } ||
            !TryBeginPluginOperation())
            return;
        var generation = ++_pluginReviewGeneration;
        PluginReviewPresentation? review;
        try
        {
            review = await _viewModel.ReviewPluginAsync(item);
        }
        finally
        {
            EndPluginOperation();
        }
        if (generation == _pluginReviewGeneration)
            ApplyPluginReview(review, action);
    }

    private void OnPluginAcknowledgementChanged(object sender, RoutedEventArgs e) =>
        UpdatePluginActionEnabled();

    private void UpdatePluginActionEnabled()
    {
        var primaryAccepted = PluginActionAcknowledge.IsChecked == true;
        var policyAccepted = !_pluginPolicyAcknowledgementRequired ||
            PluginInstallPolicyAcknowledge.IsChecked == true;
        PluginReviewActionButton.IsEnabled = primaryAccepted && policyAccepted;
    }

    private void OnPluginReviewActionClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RunPendingPluginActionAsync,
            new AppLogger(),
            nameof(OnPluginReviewActionClick));

    private async Task RunPendingPluginActionAsync()
    {
        if (_viewModel is null || _pluginReview is null ||
            _pendingPluginAction == PendingPluginAction.None || !TryBeginPluginOperation())
        {
            return;
        }

        var review = _pluginReview;
        var action = _pendingPluginAction;
        var generation = _pluginReviewGeneration;
        PluginReviewActionButton.IsEnabled = false;
        PluginCapabilityAcknowledgement? acknowledgement = null;
        var requiresCapabilityAcknowledgement =
            action == PendingPluginAction.SetEnabled && review.InstalledItem?.Enabled != true ||
            action == PendingPluginAction.Install && !string.IsNullOrWhiteSpace(review.ReviewToken);
        if (requiresCapabilityAcknowledgement)
        {
            acknowledgement = _pluginAcknowledgementOverride ??
                _viewModel.CreateAcknowledgement(review);
            if (acknowledgement is null)
            {
                PluginInfoBar.Title = LocalizationHelper.GetString("ExtensionsPage_ActionCouldNotCompleteTitle");
                PluginInfoBar.Message = LocalizationHelper.GetString("ExtensionsPage_PluginReviewExpired");
                PluginInfoBar.Severity = InfoBarSeverity.Error;
                PluginInfoBar.IsOpen = true;
                EndPluginOperation();
                UpdatePluginActionEnabled();
                return;
            }
        }

        PluginActionOutcome outcome;
        try
        {
            outcome = action switch
            {
                PendingPluginAction.Install => await _viewModel.InstallPluginAsync(
                    review,
                    acknowledgement,
                    acknowledgeInstallPolicyWarning: _pluginPolicyAcknowledgementRequired &&
                        PluginInstallPolicyAcknowledge.IsChecked == true),
                PendingPluginAction.SetEnabled when review.InstalledItem is not null =>
                    await _viewModel.SetPluginEnabledAsync(review, acknowledgement),
                PendingPluginAction.Uninstall when review.InstalledItem is not null =>
                    await _viewModel.UninstallPluginAsync(review),
                _ => new PluginActionOutcome(false, LocalizationHelper.GetString("ExtensionsPage_PluginMutationUnavailable")),
            };
        }
        finally
        {
            EndPluginOperation();
        }

        if (generation != _pluginReviewGeneration ||
            !ReferenceEquals(review, _pluginReview) || action != _pendingPluginAction)
        {
            return;
        }

        if (outcome.CapabilityPrompt is { } capability)
        {
            _pluginReview = capability.Review;
            _pluginAcknowledgementOverride = capability.Acknowledgement;
            ApplyPluginReviewDetails(capability.Review);
            PluginCapabilityInfo.Message = LocalizationHelper.Format(
                "ExtensionsPage_PluginCapabilityPromptFormat",
                capability.WidenedSurfaces);
            PluginCapabilityInfo.IsOpen = true;
            PluginActionAcknowledge.Content = LocalizationHelper.GetString(
                "ExtensionsPage_PluginCapabilityAcknowledge");
            PluginActionAcknowledge.IsChecked = false;
            UpdatePluginActionEnabled();
            return;
        }

        if (outcome.InstallPolicyPrompt is { } policy)
        {
            _pluginPolicyAcknowledgementRequired = true;
            PluginInstallPolicyInfo.Message = LocalizationHelper.Format(
                "ExtensionsPage_PluginPolicyPromptFormat",
                policy.Reason,
                policy.Findings);
            PluginInstallPolicyInfo.IsOpen = true;
            PluginInstallPolicyAcknowledge.Visibility = Visibility.Visible;
            PluginInstallPolicyAcknowledge.IsChecked = false;
            UpdatePluginActionEnabled();
            return;
        }

        PluginInfoBar.Title = LocalizationHelper.GetString(outcome.Succeeded
            ? "ExtensionsPage_ActionCompleteTitle"
            : "ExtensionsPage_ActionCouldNotCompleteTitle");
        PluginInfoBar.Message = outcome.Message;
        PluginInfoBar.Severity = outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        PluginInfoBar.IsOpen = true;
        if (outcome.Succeeded)
        {
            ClosePluginReview();
        }
        else
        {
            UpdatePluginActionEnabled();
        }
    }

    private bool TryBeginPluginOperation()
    {
        if (_pluginOperationInProgress)
            return false;
        _pluginOperationInProgress = true;
        ExtensionsTabs.IsEnabled = false;
        return true;
    }

    private void EndPluginOperation()
    {
        _pluginOperationInProgress = false;
        ExtensionsTabs.IsEnabled = true;
    }
}
